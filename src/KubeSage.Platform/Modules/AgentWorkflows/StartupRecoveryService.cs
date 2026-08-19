using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Persistence;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Puts unfinished work back on the queue after the platform restarts.
//
// The durable queue already survives a crash, but there is a gap it cannot
// close on its own. Consider the sequence:
//
//   1. a work item is claimed and marked Completed,
//   2. the dispatcher moves the incident to Investigating,
//   3. the process dies mid-investigation.
//
// The work item is finished, so nothing will reclaim it. The incident is
// Investigating, so nothing will re-detect it - deduplication sees an open
// incident with that fingerprint and correctly suppresses a new one. The
// incident is now stranded: real, unresolved, and invisible to every other
// mechanism.
//
// This service closes that gap by reconciling incident state against the
// queue at start-up. It is the difference between "the queue is durable" and
// "the work actually gets done".
internal sealed class StartupRecoveryService : BackgroundService
{
    private readonly IncidentRepository _incidents;
    private readonly WorkQueue _workQueue;
    private readonly ILogger<StartupRecoveryService> _logger;

    public StartupRecoveryService(
        IncidentRepository incidents,
        WorkQueue workQueue,
        ILogger<StartupRecoveryService> logger)
    {
        _incidents = incidents;
        _workQueue = workQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short delay so migrations and the other hosted services have
        // settled before anything is requeued.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var inFlight = await _incidents.ListInFlightAsync(stoppingToken);

            if (inFlight.Count == 0)
            {
                _logger.LogInformation("Startup recovery: no unfinished incidents");
                return;
            }

            var requeued = 0;

            foreach (var incident in inFlight)
            {
                // Enqueue is idempotent on (kind, dedup_key) for unfinished
                // work, so an incident whose item is still Pending or Claimed
                // is left alone. Only genuinely stranded work is requeued, and
                // a duplicate investigation is impossible either way.
                var id = await _workQueue.EnqueueAsync(
                    WorkKind.Investigation,
                    incident.Id.ToString(),
                    new { incidentId = incident.Id, trigger = "startup-recovery", category = incident.Category },
                    stoppingToken);

                if (id is not null)
                {
                    requeued++;

                    _logger.LogInformation(
                        "Startup recovery: requeued incident {IncidentId} ({State}, {Category}) that was left unfinished",
                        incident.Id, incident.State, incident.Category);
                }
            }

            _logger.LogInformation(
                "Startup recovery complete: {InFlightCount} unfinished incident(s), {RequeuedCount} requeued, " +
                "{AlreadyQueued} already had queued work",
                inFlight.Count, requeued, inFlight.Count - requeued);
        }
        catch (OperationCanceledException)
        {
            // Shutting down again.
        }
        catch (Exception ex)
        {
            // Recovery failing must not stop the platform starting. Detection
            // still runs, and the stranded incidents remain visible in the API.
            _logger.LogError(ex, "Startup recovery failed; some incidents may remain unfinished");
        }
    }
}
