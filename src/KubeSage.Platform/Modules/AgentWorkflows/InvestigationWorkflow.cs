using System.Diagnostics;
using System.Text.Json;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Reporting;
using KubeSage.Platform.Modules.Retrieval;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// The three-agent investigation, built as a Microsoft Agent Framework workflow.
//
// The graph deliberately alternates deterministic executors with agents:
//
//   validate -> collect evidence -> TRIAGE AGENT -> (branch)
//            -> INVESTIGATION AGENT -> validate output -> REPORT AGENT -> persist
//
// Everything that touches real systems or decides real state is an ordinary
// executor. The agents only ever receive prepared evidence and return
// structured opinions. That split is what the whole project rests on: a model
// can be wrong, so nothing a model says is allowed to become a fact without
// passing through a deterministic check first.
//
// The conditional branch after triage is a real cost control. On this hardware
// skipping an unnecessary investigation saves minutes that a genuine incident
// waiting behind it would otherwise lose.
public sealed class InvestigationWorkflow
{
    private readonly IncidentAgents _agents;
    private readonly InvestigationToolFactory _toolFactory;
    private readonly EvidenceCollector _evidenceCollector;
    private readonly AgentOutputValidator _validator;
    private readonly IncidentRepository _incidents;
    private readonly ReportRepository _reports;
    private readonly MemoryRetriever _memoryRetriever;
    private readonly SemanticMemoryIndexer _memoryIndexer;
    private readonly KubeSageOptions _options;
    private readonly ILogger<InvestigationWorkflow> _logger;

    public InvestigationWorkflow(
        IncidentAgents agents,
        InvestigationToolFactory toolFactory,
        EvidenceCollector evidenceCollector,
        AgentOutputValidator validator,
        IncidentRepository incidents,
        ReportRepository reports,
        MemoryRetriever memoryRetriever,
        SemanticMemoryIndexer memoryIndexer,
        IOptions<KubeSageOptions> options,
        ILogger<InvestigationWorkflow> logger)
    {
        _agents = agents;
        _toolFactory = toolFactory;
        _evidenceCollector = evidenceCollector;
        _validator = validator;
        _incidents = incidents;
        _reports = reports;
        _memoryRetriever = memoryRetriever;
        _memoryIndexer = memoryIndexer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InvestigationContext> RunAsync(Incident incident, CancellationToken cancellationToken)
    {
        var context = new InvestigationContext
        {
            IncidentId = incident.Id,
            InvestigationId = Guid.CreateVersion7(),
            Incident = incident,
            Budget = new InvestigationBudget(_options.Investigation.MaxToolCalls),
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        // The whole investigation shares one deadline. Without it a slow model
        // could hold the single concurrency slot indefinitely and starve every
        // other incident.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.Investigation.TimeoutSeconds));

        var workflow = BuildWorkflow(timeout.Token);

        _logger.LogInformation(
            "Investigation {InvestigationId} starting for incident {IncidentId} ({Category})",
            context.InvestigationId, incident.Id, incident.Category);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await InProcessExecution.RunAsync(workflow, context, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The budget expired rather than the platform shutting down. This
            // is a Failed investigation that may be retried, not a conclusion.
            context.FinalState = IncidentState.Failed;
            context.TerminalOutcome =
                $"the investigation exceeded its {_options.Investigation.TimeoutSeconds}s budget";

            _logger.LogWarning(
                "Investigation {InvestigationId} exceeded its time budget after {ElapsedSeconds:F0}s",
                context.InvestigationId, stopwatch.Elapsed.TotalSeconds);
        }

        stopwatch.Stop();

        // Safety net. Every path through the graph is supposed to set a
        // terminal state, but an executor that throws leaves the default in
        // place. Persisting that would leave the incident in Investigating
        // forever: never retried, never resolved, and invisible as a problem.
        if (context.FinalState == IncidentState.Investigating)
        {
            context.FinalState = IncidentState.Failed;
            context.TerminalOutcome ??=
                "the investigation ended without reaching a conclusion, most likely because an agent call failed";

            _logger.LogWarning(
                "Investigation {InvestigationId} ended with no terminal state; recording it as Failed so it can be retried",
                context.InvestigationId);
        }

        _logger.LogInformation(
            "Investigation {InvestigationId} finished in {ElapsedSeconds:F0}s with state {State} " +
            "({EvidenceCount} evidence items, {ToolCalls} tool call(s))",
            context.InvestigationId, stopwatch.Elapsed.TotalSeconds, context.FinalState,
            context.Evidence.Count, context.Budget.Used);

        await PersistAsync(context, stopwatch.Elapsed, cancellationToken);

        return context;
    }

    // Assembles the graph. Each step is a lambda executor so the deterministic
    // work and the agent calls sit side by side and the order is obvious.
    private Workflow BuildWorkflow(CancellationToken cancellationToken)
    {
        var validate = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext _, CancellationToken token) =>
                ValidateCandidateAsync(context, token),
            id: "validate-candidate");

        var collect = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext _, CancellationToken token) =>
                CollectEvidenceAsync(context, token),
            id: "collect-evidence");

        var triage = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext _, CancellationToken token) =>
                RunTriageAsync(context, token),
            id: "triage-agent");

        var investigate = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext _, CancellationToken token) =>
                RunInvestigationAsync(context, token),
            id: "investigation-agent");

        var validateOutput = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext _, CancellationToken _) =>
                ValueTask.FromResult(ValidateInvestigationOutput(context)),
            id: "validate-agent-output");

        var report = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext _, CancellationToken token) =>
                RunReportAsync(context, token),
            id: "report-agent");

        var finish = ExecutorBindingExtensions.BindAsExecutor(
            (InvestigationContext context, IWorkflowContext workflowContext, CancellationToken token) =>
                FinishAsync(context, workflowContext, token),
            id: "finish");

        var builder = new WorkflowBuilder(validate)
            .WithName("kubesage-incident-investigation")
            .WithDescription("Deterministic evidence collection with triage, investigation and report agents.");

        builder.AddEdge(validate, collect);
        builder.AddEdge(collect, triage);

        // The branch: only actionable incidents reach the expensive agent.
        // Conditions receive a nullable message because the framework may
        // evaluate an edge before a message has been produced for it.
        builder.AddEdge(triage, investigate, (InvestigationContext? context) =>
            context?.Triage?.Actionable == true && context.FinalState != IncidentState.Failed);

        builder.AddEdge(triage, finish, (InvestigationContext? context) =>
            context is null || context.Triage?.Actionable != true || context.FinalState == IncidentState.Failed);

        builder.AddEdge(investigate, validateOutput);

        // A conclusion that survived validation goes on to be written up.
        // One that did not is finished as Inconclusive - which is a valid
        // result, not an error.
        builder.AddEdge(validateOutput, report, (InvestigationContext? context) =>
            context?.Investigation?.Conclusive == true);

        builder.AddEdge(validateOutput, finish, (InvestigationContext? context) =>
            context is null || context.Investigation?.Conclusive != true);

        builder.AddEdge(report, finish);
        builder.WithOutputFrom(finish);

        return builder.Build(validateOrphans: false);
    }

    // --- Deterministic steps ------------------------------------------------

    private ValueTask<InvestigationContext> ValidateCandidateAsync(
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        // A condition that already recovered must not consume model time.
        if (context.Incident.State == IncidentState.Recovered)
        {
            context.FinalState = IncidentState.Recovered;
            context.TerminalOutcome = "the condition recovered before the investigation started";
        }

        return ValueTask.FromResult(context);
    }

    private async ValueTask<InvestigationContext> CollectEvidenceAsync(
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        // Evidence stored at detection time is loaded first. It was captured
        // when the condition was actually happening, which on slow hardware
        // may be many minutes before this investigation runs.
        var stored = await _incidents.GetEvidenceAsync(context.IncidentId, cancellationToken);
        context.AddEvidence(stored);

        // Then a fresh look, in case the situation moved on.
        try
        {
            var bundle = await _evidenceCollector.CollectAsync(
                new EvidenceRequest
                {
                    Moment = DateTimeOffset.UtcNow,
                    Window = TimeSpan.FromMinutes(_options.Detection.EvaluationWindowMinutes),
                    Workload = context.Incident.AffectedWorkloads.FirstOrDefault(),
                    Namespace = context.Incident.Namespace
                },
                cancellationToken);

            context.AddEvidence(bundle.Items);
            context.UnavailableSources.AddRange(bundle.UnavailableSources);
        }
        catch (Exception ex)
        {
            // Stored evidence alone is still enough to investigate with.
            _logger.LogWarning(ex, "Fresh evidence collection failed; continuing with stored evidence");
            context.UnavailableSources.Add($"live collection ({ex.GetType().Name})");
        }

        // Historical context is seeded here rather than left entirely to the
        // agent's tools. An investigation that makes no tool calls - which is
        // common when the pre-collected evidence is already sufficient - would
        // otherwise never see the platform's own history at all.
        await AddRetrievedContextAsync(context, cancellationToken);

        _logger.LogInformation(
            "Investigation {InvestigationId} has {EvidenceCount} evidence item(s) before triage",
            context.InvestigationId, context.Evidence.Count);

        return context;
    }

    private async Task AddRetrievedContextAsync(
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        if (!_memoryRetriever.Enabled)
        {
            return;
        }

        // The query describes the problem in the same shape a memory entry was
        // written in, which is what makes the vectors comparable.
        var query =
            $"{context.Incident.Title}. Category {context.Incident.Category} affecting " +
            $"{string.Join(", ", context.Incident.AffectedWorkloads)}.";

        var history = await _memoryRetriever.SearchSimilarIncidentsAsync(
            query,
            context.Incident.AffectedWorkloads.FirstOrDefault(),
            excludeIncidentId: context.IncidentId,
            cancellationToken);

        var runbooks = await _memoryRetriever.SearchRunbooksAsync(
            query, context.Incident.Category, cancellationToken);

        context.AddEvidence(history);
        context.AddEvidence(runbooks);

        if (history.Count + runbooks.Count > 0)
        {
            _logger.LogInformation(
                "Retrieved {HistoryCount} past incident(s) and {RunbookCount} runbook section(s) for incident {IncidentId}",
                history.Count, runbooks.Count, context.IncidentId);
        }
    }

    private InvestigationContext ValidateInvestigationOutput(InvestigationContext context)
    {
        if (context.Investigation is null)
        {
            context.FinalState = IncidentState.Failed;
            context.TerminalOutcome = "the investigation agent returned no result";
            return context;
        }

        // The critical check: strip any hypothesis whose cited evidence does
        // not exist. This is what stops a fluent but ungrounded answer being
        // stored as a finding.
        var outcome = _validator.ValidateInvestigation(context.Investigation, context.Evidence);

        context.Investigation = outcome.Value;
        context.ValidationProblems.AddRange(outcome.Problems);

        if (!outcome.IsClean)
        {
            _logger.LogWarning(
                "Investigation {InvestigationId} output needed correction: {Problems}",
                context.InvestigationId, string.Join("; ", outcome.Problems));
        }

        if (!context.Investigation.Conclusive)
        {
            context.FinalState = IncidentState.Inconclusive;
            context.TerminalOutcome = context.Investigation.Hypotheses.Length == 0
                ? "no hypothesis was supported by collected evidence"
                : "the evidence did not distinguish between possible causes";
        }

        return context;
    }

    private async ValueTask<InvestigationContext> FinishAsync(
        InvestigationContext context,
        IWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        await workflowContext.YieldOutputAsync(context, cancellationToken);
        return context;
    }

    // --- Agent steps --------------------------------------------------------

    private async ValueTask<InvestigationContext> RunTriageAsync(
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        if (context.FinalState is IncidentState.Recovered or IncidentState.Failed)
        {
            return context;
        }

        // Triage deliberately gets a SMALL slice of evidence.
        //
        // It only has to answer "is this worth investigating", and that
        // decision does not improve with sixty items - but the call time grows
        // with every one of them. Sending the full pool once pushed a triage
        // call past the model timeout, so the cheapest step in the pipeline
        // became the one that killed the investigation.
        //
        // Runbooks are excluded entirely: guidance is for the agent doing the
        // diagnosis, not for the one deciding whether to start.
        var prompt = PromptBuilder.BuildEvidencePrompt(
            context.Incident,
            SelectEvidenceForTriage(context),
            "Decide whether this incident is actionable and deserves a full investigation. " +
            "State which workloads show symptoms and what evidence is missing.");

        var result = await RunAgentAsync<TriageResult>(
            _agents.CreateTriageAgent(), IncidentAgents.TriageAgentName, prompt, context, cancellationToken);

        if (result is null)
        {
            return context;
        }

        var validated = _validator.ValidateTriage(result, context.Incident.Severity);
        context.ValidationProblems.AddRange(validated.Problems);
        context.Triage = validated.Value;

        if (!validated.Value.Actionable)
        {
            context.FinalState = IncidentState.Ignored;
            context.TerminalOutcome = validated.Value.ReasonSummary;

            _logger.LogInformation(
                "Triage judged incident {IncidentId} not actionable: {Reason}",
                context.IncidentId, validated.Value.ReasonSummary);
        }

        return context;
    }

    private async ValueTask<InvestigationContext> RunInvestigationAsync(
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        // Tools are created per investigation so the budget and evidence pool
        // belong to this run alone.
        var tools = _toolFactory.CreateFor(context, cancellationToken);
        var agent = _agents.CreateInvestigationAgent(tools);

        var missing = context.Triage?.MissingEvidence ?? [];
        var missingNote = missing.Length > 0
            ? $"\n\nTriage noted this evidence was missing: {string.Join("; ", missing)}. " +
              "Use your tools to fill those gaps if it would change your conclusion."
            : string.Empty;

        var prompt = PromptBuilder.BuildEvidencePrompt(
            context.Incident,
            SelectEvidenceForPrompt(context),
            "Determine the root cause. Remember that the workload showing errors is often not the " +
            "one at fault. Rank hypotheses by confidence and cite the evidence id behind each one. " +
            $"You may make at most {context.Budget.Remaining} tool call(s)." + missingNote);

        var result = await RunAgentAsync<InvestigationResult>(
            agent, IncidentAgents.InvestigationAgentName, prompt, context, cancellationToken);

        if (result is not null)
        {
            context.Investigation = result;
        }
        else
        {
            context.FinalState = IncidentState.Failed;
            context.TerminalOutcome ??= "the investigation agent did not return a usable result";
        }

        return context;
    }

    private async ValueTask<InvestigationContext> RunReportAsync(
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        var investigation = context.Investigation!;

        var findings = string.Join("\n", investigation.Hypotheses.Select((h, index) =>
            $"{index + 1}. [{h.Confidence:P0}] {h.Statement} " +
            $"(category: {h.RootCauseCategory}, suspected workload: {h.SuspectedWorkload}, " +
            $"evidence: {string.Join(", ", h.EvidenceIds)})"));

        var caveat = context.UnavailableSources.Count > 0
            ? $"\n\nNote: these telemetry sources were unavailable and the report must say so: " +
              string.Join(", ", context.UnavailableSources)
            : string.Empty;

        // The report agent is given the VALIDATED findings, not the raw
        // evidence pool, so it cannot introduce a cause the investigation did
        // not actually reach.
        var prompt = PromptBuilder.BuildEvidencePrompt(
            context.Incident,
            SelectEvidenceForPrompt(context),
            $"""
             The investigation reached these validated findings, ranked by confidence:

             {findings}

             Impact as assessed by the investigation: {investigation.ImpactSummary}

             Write the incident report. Do not introduce any cause not listed above and do not
             raise the confidence beyond what is shown.{caveat}
             """);

        var result = await RunAgentAsync<ReportResult>(
            _agents.CreateReportAgent(), IncidentAgents.ReportAgentName, prompt, context, cancellationToken);

        if (result is null)
        {
            context.FinalState = IncidentState.Failed;
            context.TerminalOutcome ??= "the report agent did not return a usable result";
            return context;
        }

        var validated = _validator.ValidateReport(result, context.Evidence);
        context.ValidationProblems.AddRange(validated.Problems);
        context.Report = validated.Value;
        context.FinalState = IncidentState.Reported;

        return context;
    }

    // Runs one agent, records the execution, and turns any failure into a
    // null result rather than an exception - so one agent failing ends the
    // investigation cleanly instead of losing the work already done.
    private async Task<T?> RunAgentAsync<T>(
        AIAgent agent,
        string agentName,
        string prompt,
        InvestigationContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        var started = DateTimeOffset.UtcNow;
        var toolsBefore = context.ToolsUsed.Count;

        try
        {
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
            var text = response.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ModelOutputException(
                    "The agent returned an empty message. With Gemma 4 this usually means the " +
                    "answer went to the reasoning channel instead of the content channel.");
            }

            var value = JsonSerializer.Deserialize<T>(text, SerializerOptions)
                        ?? throw new ModelOutputException("The agent returned JSON null.");

            context.AgentExecutions.Add(new AgentExecutionRecord
            {
                AgentName = agentName,
                StartedAtUtc = started,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Succeeded = true,
                ToolCallCount = context.ToolsUsed.Count - toolsBefore,
                ToolsUsed = context.ToolsUsed.Skip(toolsBefore).Distinct().ToArray(),
                Result = JsonSerializer.SerializeToElement(value)
            });

            _logger.LogInformation(
                "Agent {AgentName} completed in {DurationSeconds:F0}s",
                agentName, (DateTimeOffset.UtcNow - started).TotalSeconds);

            return value;
        }
        // The guard matters: an HttpClient timeout surfaces as
        // TaskCanceledException, which is an OperationCanceledException. An
        // earlier version excluded all of those and the failure vanished - the
        // agent recorded nothing, the workflow ended silently, and the incident
        // was left stuck mid-pipeline. Only OUR OWN cancellation is rethrown.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            context.AgentExecutions.Add(new AgentExecutionRecord
            {
                AgentName = agentName,
                StartedAtUtc = started,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Succeeded = false,
                FailureReason = ex.Message,
                ToolCallCount = context.ToolsUsed.Count - toolsBefore
            });

            _logger.LogError(ex, "Agent {AgentName} failed", agentName);

            context.FinalState = IncidentState.Failed;
            context.TerminalOutcome = $"{agentName} agent failed: {ex.Message}";

            return null;
        }
    }

    private async Task PersistAsync(
        InvestigationContext context,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            // Evidence gathered by tools during the run is stored alongside
            // what detection captured, so a report read later can still show
            // everything it was based on.
            await _incidents.SaveEvidenceAsync(context.IncidentId, context.Evidence, cancellationToken);

            await _reports.SaveInvestigationAsync(context, duration, cancellationToken);

            if (context.Report is not null)
            {
                await _reports.SaveReportAsync(context, cancellationToken);

                // Only conclusive investigations become memory. Recording an
                // inconclusive one would fill the corpus with entries saying
                // "we did not work this out", which would crowd out useful
                // history and could steer a later investigation to give up.
                await _memoryIndexer.IndexIncidentAsync(context, cancellationToken);
            }

            await _incidents.TransitionAsync(
                context.IncidentId, context.FinalState, context.TerminalOutcome, cancellationToken);
        }
        catch (Exception ex)
        {
            // Losing the result of a multi-minute investigation to a storage
            // error is worth shouting about.
            _logger.LogError(ex, "Could not persist investigation {InvestigationId}", context.InvestigationId);
            throw;
        }
    }

    // Applies the configured evidence ceiling before anything reaches a model.
    // Without this the prompt grows with the incident, and a large incident
    // produces both a slower call and a truncated answer.
    private IReadOnlyList<Evidence> SelectEvidenceForPrompt(InvestigationContext context) =>
        EvidenceSelector.Select(context.Evidence, _options.Investigation.MaxEvidenceItems);

    // A deliberately narrow view for the triage decision: cluster state and
    // metrics, which say whether something is actually wrong, without the log
    // detail or the runbook text that only matter once diagnosing starts.
    private static IReadOnlyList<Evidence> SelectEvidenceForTriage(InvestigationContext context) =>
        EvidenceSelector.Select(
            context.Evidence.Where(e => e.Kind is not EvidenceKind.Runbook),
            maxItems: 15);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
