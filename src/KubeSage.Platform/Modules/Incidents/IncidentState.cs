namespace KubeSage.Platform.Modules.Incidents;

// The lifecycle of an incident, and the rules about how it may move.
//
// Every transition is deterministic and persisted. Nothing about this file
// involves a model: an agent can report that it failed or that it reached a
// conclusion, but the decision about what state that puts the incident in is
// made here, by ordinary code.
//
// Keeping it that way is what makes the system recoverable. After a restart
// the platform can look at a stored state and know exactly what should happen
// next, without asking anything to re-reason about it.
public enum IncidentState
{
    // Raised by a detection rule. Nothing has looked at it yet.
    Candidate,

    // The Triage Agent is deciding whether it is worth investigating.
    Triaging,

    // The Investigation Agent is gathering evidence and forming hypotheses.
    Investigating,

    // A report has been produced and stored. Terminal for a successful run.
    Reported,

    // Triage judged it not actionable. Terminal.
    Ignored,

    // Investigated, but the evidence did not support a conclusion. Terminal,
    // and a legitimate outcome rather than a failure - a wrong confident
    // answer is worse than an honest "not enough evidence".
    Inconclusive,

    // Something went wrong in the pipeline: the model was unreachable, output
    // failed validation, or the time budget expired. Retryable.
    Failed,

    // The underlying condition stopped and stayed gone. Terminal.
    Recovered
}

public static class IncidentStateMachine
{
    // Allowed moves. Anything not listed here is refused.
    private static readonly Dictionary<IncidentState, IncidentState[]> Allowed = new()
    {
        [IncidentState.Candidate] =
        [
            IncidentState.Triaging,
            // A condition that clears before triage even starts is common
            // during a short blip, and should not spend model time.
            IncidentState.Recovered,
            IncidentState.Failed
        ],

        [IncidentState.Triaging] =
        [
            IncidentState.Investigating,
            IncidentState.Ignored,
            IncidentState.Recovered,
            IncidentState.Failed
        ],

        [IncidentState.Investigating] =
        [
            IncidentState.Reported,
            IncidentState.Inconclusive,
            IncidentState.Recovered,
            IncidentState.Failed
        ],

        // A failed run can be retried from the beginning, or be abandoned if
        // the condition recovered while it was failing.
        [IncidentState.Failed] =
        [
            IncidentState.Triaging,
            IncidentState.Investigating,
            IncidentState.Recovered
        ],

        // Terminal states. A reported or inconclusive incident can still be
        // marked recovered later, which is how the timeline gets closed off.
        [IncidentState.Reported] = [IncidentState.Recovered],
        [IncidentState.Inconclusive] = [IncidentState.Recovered],
        [IncidentState.Ignored] = [IncidentState.Recovered],
        [IncidentState.Recovered] = []
    };

    public static bool CanTransition(IncidentState from, IncidentState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    // Throws rather than returning false, because an invalid transition is a
    // programming error. Letting it through silently would corrupt the
    // incident history, which is the one thing that has to stay trustworthy.
    public static void EnsureTransition(IncidentState from, IncidentState to)
    {
        if (from == to)
        {
            return;
        }

        if (!CanTransition(from, to))
        {
            throw new InvalidIncidentTransitionException(from, to);
        }
    }

    // True once no further work will be scheduled for this incident.
    public static bool IsTerminal(IncidentState state) => state is
        IncidentState.Reported or
        IncidentState.Ignored or
        IncidentState.Inconclusive or
        IncidentState.Recovered;

    // True while the pipeline still owes this incident some work. Used after
    // a restart to find incidents that were mid-flight when the process died.
    public static bool IsInFlight(IncidentState state) => state is
        IncidentState.Candidate or
        IncidentState.Triaging or
        IncidentState.Investigating or
        IncidentState.Failed;
}

public sealed class InvalidIncidentTransitionException : Exception
{
    public InvalidIncidentTransitionException(IncidentState from, IncidentState to)
        : base($"An incident cannot move from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public IncidentState From { get; }

    public IncidentState To { get; }
}
