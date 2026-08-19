using System.Security.Cryptography;
using System.Text;

namespace KubeSage.Platform.Modules.Telemetry;

// A single piece of observed fact, produced by ordinary code from a real
// telemetry source.
//
// This type is the backbone of the project's central claim. Agents are not
// allowed to assert anything they cannot attach an evidence identifier to,
// and every identifier here was minted by a deterministic collector that
// recorded the exact query it ran. That is what separates an evidence-backed
// report from a plausible-sounding guess.
//
// Evidence is never invented by a model, and a model can never add to this
// collection - it can only ask a collector to gather more.
public sealed record Evidence
{
    public required string Id { get; init; }

    public required EvidenceKind Kind { get; init; }

    // Which system this came from: loki, prometheus, kubernetes or memory.
    public required string Source { get; init; }

    // When the underlying event happened, not when it was collected.
    public required DateTimeOffset ObservedAtUtc { get; init; }

    // One line, already sanitised, suitable for showing to a model.
    public required string Summary { get; init; }

    public string? Workload { get; init; }

    public string? Namespace { get; init; }

    // Structured detail. Kept as strings because this is presented to a model
    // and stored as JSON; typed values would gain nothing downstream.
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>();

    // The exact query that produced this item. Recorded so a human can rerun
    // it in Grafana and confirm the evidence independently - which is the
    // whole reason Grafana is part of this project.
    public string? Query { get; init; }

    // How many values were removed by redaction before this was allowed near
    // a model. Surfaced so that "the logs looked empty" can be distinguished
    // from "the logs were redacted".
    public int RedactedValueCount { get; init; }

    // Builds a stable identifier from the content that defines this piece of
    // evidence.
    //
    // Stability matters twice over: the same observation collected twice must
    // not produce two identifiers (which would let an agent cite the same
    // fact as if it were two independent confirmations), and an identifier
    // quoted in a stored report must still resolve later.
    public static string CreateId(EvidenceKind kind, string source, params ReadOnlySpan<string?> parts)
    {
        var builder = new StringBuilder();
        builder.Append(kind).Append('|').Append(source);

        foreach (var part in parts)
        {
            builder.Append('|').Append(part ?? string.Empty);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var prefix = kind switch
        {
            EvidenceKind.LogSample => "log",
            EvidenceKind.LogSignature => "sig",
            EvidenceKind.Metric => "met",
            EvidenceKind.KubernetesState => "k8s",
            EvidenceKind.KubernetesEvent => "evt",
            EvidenceKind.HistoricalIncident => "hist",
            EvidenceKind.Runbook => "book",
            _ => "ev"
        };

        return $"{prefix}_{Convert.ToHexStringLower(hash)[..12]}";
    }
}

public enum EvidenceKind
{
    // One or more raw log lines retrieved from Loki.
    LogSample,

    // A normalised, repeated error pattern with an occurrence count. More
    // useful than individual lines when the same failure repeats thousands of
    // times, and far cheaper in model context.
    LogSignature,

    // A metric value or series summary from Prometheus.
    Metric,

    // Observed state of a Kubernetes object: pod phase, restart count,
    // readiness, resource limits.
    KubernetesState,

    // A Kubernetes event such as BackOff, Unhealthy or Killing.
    KubernetesEvent,

    // A semantically similar incident from the platform's own history.
    HistoricalIncident,

    // A passage from the curated runbook corpus.
    Runbook
}
