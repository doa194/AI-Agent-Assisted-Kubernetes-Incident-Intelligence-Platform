using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Telemetry;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Checks an agent's structured output against the evidence that was actually
// collected.
//
// This is the mechanism behind the project's central claim. Schema-constrained
// generation guarantees the SHAPE of an answer; nothing about it guarantees
// the CONTENT is true. A model can produce a perfectly well-formed hypothesis
// citing evidence identifiers it invented, and without this class that would
// be stored and served as if it were grounded.
//
// The validator is deliberately strict in one direction and forgiving in
// another:
//
//   * an invented evidence identifier is REMOVED, and a claim left with no
//     supporting evidence is rejected entirely;
//   * a hypothesis that is merely weak or uncertain is KEPT, because ranking
//     uncertain possibilities is a legitimate part of investigation.
public sealed class AgentOutputValidator
{
    private readonly ILogger<AgentOutputValidator> _logger;

    public AgentOutputValidator(ILogger<AgentOutputValidator> logger) => _logger = logger;

    // Validates the investigation result, dropping unsupported claims.
    public ValidationOutcome<InvestigationResult> ValidateInvestigation(
        InvestigationResult result,
        IReadOnlyCollection<Evidence> availableEvidence)
    {
        var knownIds = availableEvidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var accepted = new List<HypothesisResult>();

        foreach (var hypothesis in result.Hypotheses)
        {
            var cited = hypothesis.EvidenceIds ?? [];
            var real = cited.Where(knownIds.Contains).ToArray();
            var invented = cited.Except(real, StringComparer.Ordinal).ToArray();

            if (invented.Length > 0)
            {
                // Recorded rather than silently dropped: a model that
                // fabricates identifiers is a problem worth being able to see.
                problems.Add(
                    $"hypothesis '{Shorten(hypothesis.Statement)}' cited {invented.Length} " +
                    $"evidence id(s) that do not exist: {string.Join(", ", invented.Take(3))}");

                _logger.LogWarning(
                    "Investigation cited {InventedCount} non-existent evidence id(s); they were removed",
                    invented.Length);
            }

            if (real.Length == 0)
            {
                // An unsupported claim is discarded. This is the single most
                // important rule in the file.
                problems.Add(
                    $"hypothesis '{Shorten(hypothesis.Statement)}' was rejected because no cited evidence exists");
                continue;
            }

            accepted.Add(hypothesis with
            {
                EvidenceIds = real,
                // Confidence arrives from the model unbounded in practice
                // despite the schema; clamping keeps stored values comparable.
                Confidence = Math.Clamp(hypothesis.Confidence, 0.0, 1.0)
            });
        }

        // Every hypothesis failing means there is no grounded conclusion, no
        // matter what the model asserted about being conclusive.
        var conclusive = result.Conclusive && accepted.Count > 0;

        var validated = result with
        {
            Conclusive = conclusive,
            Hypotheses = accepted
                .OrderByDescending(h => h.Confidence)
                .ToArray()
        };

        return new ValidationOutcome<InvestigationResult>(validated, problems);
    }

    // Validates the report. The report agent receives already-validated
    // investigation output, so this mainly guards against it introducing new
    // unsupported citations of its own.
    public ValidationOutcome<ReportResult> ValidateReport(
        ReportResult result,
        IReadOnlyCollection<Evidence> availableEvidence)
    {
        var knownIds = availableEvidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        var cited = result.EvidenceIds ?? [];
        var real = cited.Where(knownIds.Contains).ToArray();
        var invented = cited.Except(real, StringComparer.Ordinal).ToArray();

        if (invented.Length > 0)
        {
            problems.Add($"report cited {invented.Length} evidence id(s) that do not exist");
            _logger.LogWarning(
                "Report cited {InventedCount} non-existent evidence id(s); they were removed", invented.Length);
        }

        if (real.Length == 0)
        {
            problems.Add("report cites no real evidence");
        }

        var validated = result with
        {
            EvidenceIds = real,
            Confidence = Math.Clamp(result.Confidence, 0.0, 1.0)
        };

        return new ValidationOutcome<ReportResult>(validated, problems);
    }

    // Validates a cluster health summary.
    //
    // Same rule as everywhere else: a citation that does not resolve is
    // removed. A cluster report is allowed to end up citing nothing, because
    // "healthy, nothing to report" is a legitimate conclusion - unlike an
    // incident hypothesis, which is worthless without support.
    public ValidationOutcome<ClusterHealthResult> ValidateClusterReport(
        ClusterHealthResult result,
        IReadOnlyCollection<Evidence> availableEvidence)
    {
        var knownIds = availableEvidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        var cited = result.EvidenceIds ?? [];
        var real = cited.Where(knownIds.Contains).ToArray();

        if (cited.Length != real.Length)
        {
            problems.Add($"cluster report cited {cited.Length - real.Length} evidence id(s) that do not exist");
            _logger.LogWarning(
                "Cluster report cited {InventedCount} non-existent evidence id(s); they were removed",
                cited.Length - real.Length);
        }

        return new ValidationOutcome<ClusterHealthResult>(result with { EvidenceIds = real }, problems);
    }

    // Validates triage and enforces the severity floor.
    public ValidationOutcome<TriageResult> ValidateTriage(
        TriageResult result,
        IncidentSeverity detectedSeverity)
    {
        var problems = new List<string>();

        if (!Enum.TryParse<IncidentSeverity>(result.Severity, ignoreCase: true, out var proposed))
        {
            problems.Add($"triage returned an unknown severity '{result.Severity}'; the detected severity is kept");
            proposed = detectedSeverity;
        }

        // The deterministic rules measured something real. Triage may decide
        // it is worse than the rules thought, but it may not talk the platform
        // out of an alert a threshold produced.
        var effective = proposed < detectedSeverity ? detectedSeverity : proposed;

        if (proposed < detectedSeverity)
        {
            problems.Add(
                $"triage proposed {proposed} but detection measured {detectedSeverity}; " +
                "the higher severity is kept");
        }

        var validated = result with { Severity = effective.ToString() };

        return new ValidationOutcome<TriageResult>(validated, problems);
    }

    private static string Shorten(string value) =>
        value.Length <= 60 ? value : value[..60] + "...";
}

// The validated value plus anything that had to be corrected or rejected.
//
// Problems are carried forward rather than thrown away: a report produced from
// output that needed correcting should say so, because it is a signal about
// how much to trust the result.
public sealed record ValidationOutcome<T>(T Value, IReadOnlyList<string> Problems)
{
    public bool IsClean => Problems.Count == 0;
}
