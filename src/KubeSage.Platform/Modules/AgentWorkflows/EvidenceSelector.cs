using KubeSage.Platform.Modules.Telemetry;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Chooses which evidence to put in front of an agent when there is more of it
// than the budget allows.
//
// This exists because of a real failure. An investigation with 113 evidence
// items produced a hypothesis that tried to cite 44 of them, ran past the
// output token limit, and returned JSON truncated in the middle of an array -
// nine minutes of model time wasted on an unparsable answer.
//
// Two things were wrong, and both are fixed here and in the schema:
//   * the configured evidence ceiling was never actually applied;
//   * nothing discouraged citing everything at once, even though a hypothesis
//     supported by forty pieces of evidence is less discriminating than one
//     supported by four, not more.
//
// The ordering below reflects how much each kind of evidence contributes per
// item, which is roughly the inverse of how many of them there are.
public static class EvidenceSelector
{
    // Most informative per item first.
    //
    // Cluster state is authoritative and there is very little of it. Metrics
    // quantify impact in one line. A log signature already summarises hundreds
    // of lines with a count. Individual log samples are the most numerous and
    // the least informative each, so they are trimmed first.
    private static readonly EvidenceKind[] Priority =
    [
        EvidenceKind.KubernetesState,
        EvidenceKind.KubernetesEvent,
        EvidenceKind.Metric,
        EvidenceKind.LogSignature,
        EvidenceKind.HistoricalIncident,
        EvidenceKind.Runbook,
        EvidenceKind.LogSample
    ];

    public static IReadOnlyList<Evidence> Select(IEnumerable<Evidence> evidence, int maxItems)
    {
        var all = evidence.ToList();

        if (all.Count <= maxItems)
        {
            return all;
        }

        var selected = new List<Evidence>(maxItems);

        foreach (var kind in Priority)
        {
            if (selected.Count >= maxItems)
            {
                break;
            }

            var ofKind = all
                .Where(item => item.Kind == kind)
                // Newest first: during an incident the most recent
                // observations are the ones that describe the current state.
                .OrderByDescending(item => item.ObservedAtUtc)
                .Take(maxItems - selected.Count);

            selected.AddRange(ofKind);
        }

        return selected;
    }
}
