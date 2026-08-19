using KubeSage.Platform.Modules.Incidents;

namespace KubeSage.Platform.Modules.Detection;

// Reduces one detection pass to the candidates actually worth investigating.
//
// Why this is necessary, from a real observed run: taking the workload
// database down produced TWELVE candidates in a single pass - the database
// readiness failure, connection failures from two services, elevated error
// rates on two services, and seven separate repeated-error-signature
// candidates for the log lines those failures produced.
//
// Every one of them was a true observation. Only one of them was the incident.
//
// This matters far beyond tidiness. Each candidate becomes an investigation,
// and an investigation on a local 12B model takes minutes. Twelve of them for
// one outage would occupy the model for hours and bury the useful conclusion
// among eleven restatements of the same symptom.
//
// The suppression rules below are deterministic and ordered by how much a
// signal EXPLAINS rather than how loud it is. No model is involved.
public static class CandidateSuppression
{
    // Categories ordered by explanatory power, most explanatory first.
    //
    // A dependency failure names the thing that broke. An error rate only says
    // that something did. A repeated log signature says the least of all: it
    // is a safety net for problems the other rules cannot see.
    private static readonly string[] Precedence =
    [
        IncidentCategory.OutOfMemory,
        IncidentCategory.DependencyUnavailable,
        IncidentCategory.DependencyLatency,
        IncidentCategory.PodRestartLoop,
        IncidentCategory.ReadinessFailure,
        IncidentCategory.HttpErrorRate,
        IncidentCategory.RepeatedErrorSignature
    ];

    // Categories that explain a failure well enough to make a generic
    // repeated-error-signature candidate for the same workload redundant.
    private static readonly HashSet<string> Explanatory = new(StringComparer.Ordinal)
    {
        IncidentCategory.OutOfMemory,
        IncidentCategory.DependencyUnavailable,
        IncidentCategory.DependencyLatency,
        IncidentCategory.PodRestartLoop,
        IncidentCategory.ReadinessFailure,
        IncidentCategory.HttpErrorRate
    };

    public static IReadOnlyList<IncidentCandidate> Apply(IReadOnlyList<IncidentCandidate> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        // Workloads for which a more explanatory rule already fired.
        var explained = candidates
            .Where(candidate => Explanatory.Contains(candidate.Category))
            .SelectMany(candidate => candidate.AffectedWorkloads)
            .ToHashSet(StringComparer.Ordinal);

        var kept = new List<IncidentCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate.Category != IncidentCategory.RepeatedErrorSignature)
            {
                kept.Add(candidate);
                continue;
            }

            // A log-signature candidate is only useful when nothing else
            // explained that workload. When something did, those log lines are
            // the same failure seen from a different angle.
            if (candidate.AffectedWorkloads.All(workload => explained.Contains(workload)))
            {
                continue;
            }

            kept.Add(candidate);
        }

        // Several distinct error signatures from one workload are still one
        // problem from an operator's point of view. Only the most frequent is
        // kept, since it is the one most likely to describe the cause.
        var collapsed = kept
            .Where(candidate => candidate.Category == IncidentCategory.RepeatedErrorSignature)
            .GroupBy(candidate => string.Join(",", candidate.AffectedWorkloads), StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(OccurrenceCount)
                .Take(1));

        var result = kept
            .Where(candidate => candidate.Category != IncidentCategory.RepeatedErrorSignature)
            .Concat(collapsed)
            // Highest severity first, then most explanatory category. This is
            // the order investigations are queued in, so the most informative
            // incident reaches the model first when capacity is limited.
            .OrderByDescending(candidate => candidate.Severity)
            .ThenBy(candidate => Array.IndexOf(Precedence, candidate.Category))
            .ToList();

        return result;
    }

    private static int OccurrenceCount(IncidentCandidate candidate) =>
        candidate.Signals.TryGetValue("occurrences", out var raw) &&
        int.TryParse(raw, out var value)
            ? value
            : 0;
}
