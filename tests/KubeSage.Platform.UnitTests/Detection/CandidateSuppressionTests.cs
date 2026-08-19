using KubeSage.Platform.Modules.Detection;
using KubeSage.Platform.Modules.Incidents;

namespace KubeSage.Platform.UnitTests.Detection;

// Suppression is what stands between one outage and a dozen investigations.
//
// The scenario in the first test is taken from a real run: scaling the
// workload database to zero produced twelve true candidates, only one of which
// described the actual problem. On hardware where an investigation takes
// minutes, getting this wrong is the difference between a usable platform and
// one that spends hours restating the same symptom.
public sealed class CandidateSuppressionTests
{
    [Fact]
    public void A_dependency_failure_suppresses_generic_log_signatures_for_the_same_workload()
    {
        // Arrange - the shape of a real database outage.
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.DependencyUnavailable, IncidentSeverity.High, ["order-api", "notification-worker"]),
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.High, ["order-api"], occurrences: 21),
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.Medium, ["notification-worker"], occurrences: 11)
        };

        // Act
        var kept = CandidateSuppression.Apply(candidates);

        // Assert - the log signatures said the same thing the dependency
        // failure already said, only less usefully.
        kept.Count.ShouldBe(1);
        kept[0].Category.ShouldBe(IncidentCategory.DependencyUnavailable);
    }

    [Fact]
    public void A_log_signature_survives_when_nothing_else_explains_that_workload()
    {
        // The signature rule is the safety net for problems the metric and
        // Kubernetes rules cannot see - an error that is handled and still
        // returns 200, for example. Suppressing it unconditionally would
        // create a blind spot.
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.HttpErrorRate, IncidentSeverity.High, ["gateway"]),
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.Medium, ["payment-simulator"], occurrences: 40)
        };

        var kept = CandidateSuppression.Apply(candidates);

        kept.Count.ShouldBe(2);
        kept.ShouldContain(c => c.Category == IncidentCategory.RepeatedErrorSignature
                                && c.AffectedWorkloads.Contains("payment-simulator"));
    }

    [Fact]
    public void Several_signatures_from_one_workload_collapse_to_the_most_frequent()
    {
        // Four different error signatures from the same service during one
        // incident is still one problem to an operator.
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.Medium, ["gateway"], occurrences: 10),
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.High, ["gateway"], occurrences: 27),
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.High, ["gateway"], occurrences: 21)
        };

        var kept = CandidateSuppression.Apply(candidates);

        kept.Count.ShouldBe(1);
        kept[0].Signals["occurrences"].ShouldBe("27");
    }

    [Fact]
    public void Distinct_real_problems_are_all_kept()
    {
        // Suppression must never merge genuinely different incidents. An
        // out-of-memory kill and a readiness failure on two different
        // workloads are two problems, and both need investigating.
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.OutOfMemory, IncidentSeverity.High, ["payment-simulator"]),
            Candidate(IncidentCategory.ReadinessFailure, IncidentSeverity.High, ["notification-worker"]),
            Candidate(IncidentCategory.DependencyLatency, IncidentSeverity.Medium, ["order-api"])
        };

        var kept = CandidateSuppression.Apply(candidates);

        kept.Count.ShouldBe(3);
    }

    [Fact]
    public void The_most_explanatory_candidate_is_ordered_first()
    {
        // Investigations are queued in this order, so when model capacity is
        // limited the most informative incident is the one that gets analysed.
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.HttpErrorRate, IncidentSeverity.High, ["gateway"]),
            Candidate(IncidentCategory.DependencyUnavailable, IncidentSeverity.High, ["order-api"])
        };

        var kept = CandidateSuppression.Apply(candidates);

        // Same severity, so the more explanatory category wins: naming the
        // failing dependency beats reporting that errors went up.
        kept[0].Category.ShouldBe(IncidentCategory.DependencyUnavailable);
    }

    [Fact]
    public void Higher_severity_outranks_a_more_explanatory_category()
    {
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.DependencyLatency, IncidentSeverity.Medium, ["order-api"]),
            Candidate(IncidentCategory.HttpErrorRate, IncidentSeverity.Critical, ["gateway"])
        };

        var kept = CandidateSuppression.Apply(candidates);

        kept[0].Severity.ShouldBe(IncidentSeverity.Critical);
    }

    [Fact]
    public void A_single_candidate_passes_through_untouched()
    {
        var candidates = new List<IncidentCandidate>
        {
            Candidate(IncidentCategory.RepeatedErrorSignature, IncidentSeverity.Medium, ["gateway"], occurrences: 12)
        };

        CandidateSuppression.Apply(candidates).Count.ShouldBe(1);
    }

    private static IncidentCandidate Candidate(
        string category,
        IncidentSeverity severity,
        string[] workloads,
        int occurrences = 0) => new()
    {
        Fingerprint = IncidentFingerprint.Create(category, "kubesage-demo", workloads, occurrences.ToString()),
        Category = category,
        Severity = severity,
        Title = $"{category} affecting {string.Join(", ", workloads)}",
        DetectionRule = "test",
        DetectedAtUtc = DateTimeOffset.UtcNow,
        Namespace = "kubesage-demo",
        AffectedWorkloads = workloads,
        Signals = new Dictionary<string, string> { ["occurrences"] = occurrences.ToString() }
    };
}
