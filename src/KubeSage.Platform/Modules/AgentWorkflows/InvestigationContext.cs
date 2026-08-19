using System.Text.Json;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Extensions.AI;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Everything one investigation knows, carried between the workflow steps.
//
// A single mutable object is passed along the graph rather than each step
// returning a new message type. That is a deliberate simplification: the
// deterministic steps and the agent steps all need the same growing pool of
// evidence, and threading it through six separate message shapes would add
// ceremony without adding safety.
//
// What DOES stay strictly controlled is who may add to the evidence pool.
// Only AddEvidence, called by deterministic collectors and by the tool layer,
// can do it. An agent never writes here; it can only ask a tool to collect
// more, and the tool records what it found.
public sealed class InvestigationContext
{
    private readonly Dictionary<string, Evidence> _evidence = new(StringComparer.Ordinal);

    public required Guid IncidentId { get; init; }

    public required Guid InvestigationId { get; init; }

    public required Incident Incident { get; init; }

    public required InvestigationBudget Budget { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    // Telemetry sources that could not be reached. Carried all the way to the
    // report so a conclusion drawn from partial data is presented as such.
    public List<string> UnavailableSources { get; } = [];

    // Names of the tools the investigation agent actually called, for audit.
    public List<string> ToolsUsed { get; } = [];

    // Problems the validator found in agent output. A report produced from
    // output that needed correcting carries that fact.
    public List<string> ValidationProblems { get; } = [];

    public List<AgentExecutionRecord> AgentExecutions { get; } = [];

    public TriageResult? Triage { get; set; }

    public InvestigationResult? Investigation { get; set; }

    public ReportResult? Report { get; set; }

    // Set when the workflow ends early: triage judged it not actionable, or
    // the evidence did not support a conclusion.
    public string? TerminalOutcome { get; set; }

    public IncidentState FinalState { get; set; } = IncidentState.Investigating;

    public IReadOnlyCollection<Evidence> Evidence => _evidence.Values;

    // Adds evidence, ignoring anything already present.
    //
    // Evidence identifiers are deterministic, so collecting the same
    // observation twice yields the same id. Deduplicating here stops an agent
    // being able to inflate apparent corroboration by asking for the same
    // thing repeatedly.
    public int AddEvidence(IEnumerable<Evidence> items)
    {
        var added = 0;

        foreach (var item in items)
        {
            if (_evidence.TryAdd(item.Id, item))
            {
                added++;
            }
        }

        return added;
    }

    public bool HasExpired(TimeSpan budget) => DateTimeOffset.UtcNow - StartedAtUtc > budget;
}

// One agent's execution, recorded for the audit trail.
//
// Note the absence of any field for the model's reasoning: the decision is
// kept, the thinking behind it is not.
public sealed record AgentExecutionRecord
{
    public required string AgentName { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public int ToolCallCount { get; init; }
    public string[] ToolsUsed { get; init; } = [];
    public JsonElement? Result { get; init; }

    public int DurationMs => (int)(CompletedAtUtc - StartedAtUtc).TotalMilliseconds;
}
