using System.Text.Json;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Persistence;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// The background worker that makes investigation autonomous.
//
// It claims work from the durable queue and runs the three-agent workflow. No
// user request is involved anywhere: incidents reach this loop because
// detection found them.
//
// Concurrency is the important setting. It defaults to ONE because a 12B model
// on local hardware does not gain throughput from parallel investigations - it
// loses both to memory pressure and timeouts. Claiming a bounded number of
// items is the backpressure mechanism.
internal sealed class InvestigationDispatcher : BackgroundService
{
    private readonly WorkQueue _workQueue;
    private readonly IncidentRepository _incidents;
    private readonly InvestigationWorkflow _workflow;
    private readonly ClusterAnalysis _clusterAnalysis;
    private readonly OllamaChatClientAdapter _chatClient;
    private readonly InvestigationOptions _options;
    private readonly ILogger<InvestigationDispatcher> _logger;

    public InvestigationDispatcher(
        WorkQueue workQueue,
        IncidentRepository incidents,
        InvestigationWorkflow workflow,
        ClusterAnalysis clusterAnalysis,
        OllamaChatClientAdapter chatClient,
        IOptions<KubeSageOptions> options,
        ILogger<InvestigationDispatcher> logger)
    {
        _workQueue = workQueue;
        _incidents = incidents;
        _workflow = workflow;
        _clusterAnalysis = clusterAnalysis;
        _chatClient = chatClient;
        _options = options.Value.Investigation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Work this process was holding when it last died is released
        // immediately, rather than waiting out a lease that can be tens of
        // minutes long.
        await _workQueue.ReleaseOwnStaleLeasesAsync(stoppingToken);

        _logger.LogInformation(
            "Investigation dispatcher started with concurrency {MaxConcurrent} and a {TimeoutSeconds}s budget per investigation",
            _options.MaxConcurrent, _options.TimeoutSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.DispatcherPollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAvailableWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The dispatcher must never die. Losing it would silently stop
                // all autonomous analysis while the platform still looked
                // healthy.
                _logger.LogError(ex, "Dispatcher iteration failed; continuing");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Investigation dispatcher stopped");
    }

    private async Task ProcessAvailableWorkAsync(CancellationToken stoppingToken)
    {
        var items = await _workQueue.ClaimAsync(_options.MaxConcurrent, stoppingToken);

        if (items.Count == 0)
        {
            return;
        }

        // Checked once per batch rather than per item. When the model is down
        // the work is released back to the queue immediately, so it is retried
        // later instead of burning attempts against an unreachable server.
        if (!await _chatClient.IsAvailableAsync(stoppingToken))
        {
            _logger.LogWarning(
                "Ollama is unavailable; releasing {Count} claimed work item(s) to retry later", items.Count);

            foreach (var item in items)
            {
                await _workQueue.FailAsync(item, "the model server was unavailable", stoppingToken);
            }

            return;
        }

        foreach (var item in items)
        {
            await ProcessItemAsync(item, stoppingToken);
        }
    }

    private async Task ProcessItemAsync(WorkItem item, CancellationToken stoppingToken)
    {
        // A long investigation can outlive its lease, so the lease is renewed
        // in the background while it runs. Without this a healthy long run
        // would be claimed a second time and produce a duplicate report.
        using var leaseRenewal = new CancellationTokenSource();
        var renewalTask = RenewLeaseAsync(item.Id, leaseRenewal.Token);

        try
        {
            switch (item.Kind)
            {
                case WorkKind.Investigation:
                    await RunInvestigationAsync(item, stoppingToken);
                    break;

                case WorkKind.StartupAnalysis:
                case WorkKind.ScheduledAnalysis:
                    await RunPeriodicAnalysisAsync(item, stoppingToken);
                    break;

                default:
                    _logger.LogWarning("Unknown work kind '{Kind}'; marking it complete", item.Kind);
                    break;
            }

            await _workQueue.CompleteAsync(item.Id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. The lease will expire and another run picks it up.
            _logger.LogInformation("Work item {WorkItemId} interrupted by shutdown; it stays queued", item.Id);
        }
        catch (Exception ex)
        {
            await _workQueue.FailAsync(item, $"{ex.GetType().Name}: {ex.Message}", stoppingToken);
        }
        finally
        {
            await leaseRenewal.CancelAsync();

            try
            {
                await renewalTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the renewal loop is stopped.
            }
        }
    }

    private async Task RunInvestigationAsync(WorkItem item, CancellationToken stoppingToken)
    {
        var payload = item.PayloadAs<InvestigationPayload>();

        if (payload is null || payload.IncidentId == Guid.Empty)
        {
            // Thrown rather than skipped. Returning quietly here would mark
            // the item Completed, and a queue that drains without doing any
            // work is indistinguishable from a healthy one - which is exactly
            // how a payload deserialisation bug hid itself once already.
            throw new InvalidOperationException(
                $"Work item {item.Id} carried no usable incident id. Payload: {item.Payload}");
        }

        var incident = await _incidents.GetAsync(payload.IncidentId, stoppingToken);

        if (incident is null)
        {
            _logger.LogWarning("Incident {IncidentId} no longer exists; discarding its work", payload.IncidentId);
            return;
        }

        if (IncidentStateMachine.IsTerminal(incident.State))
        {
            // Already resolved or already reported - most often because the
            // condition recovered while this item sat in the queue.
            _logger.LogInformation(
                "Incident {IncidentId} is already {State}; skipping investigation",
                incident.Id, incident.State);
            return;
        }

        // Advance the incident to Investigating, taking a route the state
        // machine actually permits from wherever it currently is.
        //
        // This matters for RESUMED work. After a crash an incident is often
        // already Investigating, and unconditionally moving it back to
        // Triaging is a transition the state machine forbids - so recovery
        // threw every time, and the incident failed permanently on the very
        // path that was supposed to rescue it.
        switch (incident.State)
        {
            case IncidentState.Candidate:
                await _incidents.TransitionAsync(incident.Id, IncidentState.Triaging, null, stoppingToken);
                await _incidents.TransitionAsync(incident.Id, IncidentState.Investigating, null, stoppingToken);
                break;

            case IncidentState.Triaging:
            case IncidentState.Failed:
                await _incidents.TransitionAsync(incident.Id, IncidentState.Investigating, null, stoppingToken);
                break;

            case IncidentState.Investigating:
                // Resumed mid-flight. Already in the right state; the whole
                // workflow re-runs from the beginning, which is safe because
                // every step is idempotent and evidence is deduplicated by id.
                _logger.LogInformation(
                    "Resuming incident {IncidentId}, which was left mid-investigation by an earlier run",
                    incident.Id);
                break;

            default:
                _logger.LogWarning(
                    "Incident {IncidentId} is in unexpected state {State}; skipping", incident.Id, incident.State);
                return;
        }

        await _workflow.RunAsync(incident with { State = IncidentState.Investigating }, stoppingToken);
    }

    // Startup and scheduled analysis produce a cluster health summary rather
    // than an incident report. They run through the same queue so they are
    // subject to the same concurrency limit and the same durability.
    // Startup and scheduled analysis produce a whole-cluster health report.
    //
    // Deliberately different from an incident investigation: it answers "how
    // is everything" rather than "why did this break", and it runs even when
    // nothing is wrong - a report confirming the cluster is healthy, naming
    // what was checked, is evidence the platform is actually watching.
    private async Task RunPeriodicAnalysisAsync(WorkItem item, CancellationToken stoppingToken)
    {
        var reportId = await _clusterAnalysis.RunAsync(item.Kind, stoppingToken);

        if (reportId is null)
        {
            _logger.LogWarning(
                "{Kind} produced no report; see the preceding log entry for why", item.Kind);
        }
    }

    private async Task RenewLeaseAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        // Renew at roughly a third of the lease so two renewals can fail
        // before the work is considered abandoned.
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.WorkLeaseSeconds / 3.0));

        try
        {
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!await _workQueue.RenewLeaseAsync(workItemId, cancellationToken))
                {
                    _logger.LogWarning(
                        "Lease renewal for work item {WorkItemId} failed; another worker may have claimed it",
                        workItemId);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the work finished.
        }
    }

    private sealed record InvestigationPayload(Guid IncidentId, string? Trigger, string? Category);
}
