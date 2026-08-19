using KubeSage.Platform.Modules.Telemetry;

namespace KubeSage.Platform.Modules.Incidents;

// How serious an incident is. Assigned by deterministic rules at detection
// time and only ever RAISED by triage, never lowered - a model is not
// permitted to talk the platform out of an alert that the rules produced.
public enum IncidentSeverity
{
    Low,
    Medium,
    High,
    Critical
}

// Broad classes of problem the detection rules can raise.
//
// These are deliberately about the SHAPE of the evidence, not the cause.
// "dependency_latency" means calls to a dependency became slow; deciding
// which dependency and why is the investigation's job.
public static class IncidentCategory
{
    public const string HttpErrorRate = "http_error_rate";
    public const string DependencyLatency = "dependency_latency";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string PodRestartLoop = "pod_restart_loop";
    public const string ReadinessFailure = "readiness_failure";
    public const string OutOfMemory = "out_of_memory";
    public const string RepeatedErrorSignature = "repeated_error_signature";
}

// What a detection rule produces.
//
// This is a value object, not a database row. It says "here is a condition I
// observed"; whether that becomes a new incident, updates an existing one, or
// is suppressed as a duplicate is decided afterwards by the deduplication
// rules. Keeping the two apart is what lets a rule stay simple and stateless.
public sealed record IncidentCandidate
{
    public required string Fingerprint { get; init; }
    public required string Category { get; init; }
    public required IncidentSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string DetectionRule { get; init; }
    public required DateTimeOffset DetectedAtUtc { get; init; }
    public required string Namespace { get; init; }

    // Workloads showing the symptom. NOT a claim about which one is at fault.
    public required IReadOnlyList<string> AffectedWorkloads { get; init; }

    // The measured values that made the rule fire, so a human can check the
    // arithmetic without rerunning anything.
    public required IReadOnlyDictionary<string, string> Signals { get; init; }

    // Evidence gathered at detection time. Stored with the incident because
    // Loki and Prometheus age their data out, and a report written days later
    // must still be able to show what it was based on.
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

// The persisted incident aggregate.
public sealed record Incident
{
    public required Guid Id { get; init; }
    public required string Fingerprint { get; init; }
    public required IncidentState State { get; init; }
    public required IncidentSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string DetectionRule { get; init; }
    public required string Namespace { get; init; }
    public required IReadOnlyList<string> AffectedWorkloads { get; init; }
    public required IReadOnlyDictionary<string, string> Signals { get; init; }

    public required DateTimeOffset FirstDetectedAtUtc { get; init; }

    // Updated every time the same fingerprint is observed again. The gap
    // between first and last detection is how long the condition persisted,
    // which is a far better measure of impact than a single timestamp.
    public required DateTimeOffset LastDetectedAtUtc { get; init; }

    public DateTimeOffset? RecoveredAtUtc { get; init; }

    // How many times the condition has been observed. A rule that fires once
    // and a rule that has fired forty times in a row are different situations.
    public required int OccurrenceCount { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    // Set when triage decides the candidate is not worth investigating, or
    // when an investigation ends without a supported conclusion.
    public string? Outcome { get; init; }

    public bool IsTerminal => IncidentStateMachine.IsTerminal(State);
}
