using System.Text.RegularExpressions;
using KubeSage.Platform.Configuration;

namespace KubeSage.Platform.Modules.Telemetry;

// Validates and clamps every telemetry request before it reaches Loki,
// Prometheus or the Kubernetes API.
//
// Two different threats are handled here, and they need different answers:
//
//  * ACCIDENT. A detection rule or an agent asks for six hours of logs across
//    every namespace. Nothing malicious, but it would overwhelm Loki and
//    flood the model's context. Answer: clamp the request to the configured
//    ceiling rather than refusing, so the caller still gets useful data.
//
//  * INJECTION. An agent supplies a workload name that is really a fragment
//    of LogQL or PromQL, attempting to widen the query beyond what it was
//    allowed. Answer: refuse outright. Identifiers are matched against a
//    strict pattern and free text is escaped, never interpolated raw.
//
// Refusing is deliberate for the second case: silently sanitising a hostile
// identifier would hide the attempt, and a rejected tool call is something an
// operator can see in the logs.
public sealed partial class TelemetryQuery
{
    private readonly TelemetryOptions _telemetry;
    private readonly KubernetesOptions _kubernetes;

    public TelemetryQuery(TelemetryOptions telemetry, KubernetesOptions kubernetes)
    {
        _telemetry = telemetry;
        _kubernetes = kubernetes;
    }

    // Kubernetes object names are already restricted to this alphabet, so
    // anything outside it is either a mistake or an attempt to inject.
    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.Compiled)]
    private static partial Regex KubernetesName();

    public string RequireWorkload(string? workload)
    {
        if (string.IsNullOrWhiteSpace(workload) || !KubernetesName().IsMatch(workload))
        {
            throw new TelemetryQueryRejectedException(
                $"'{workload}' is not a valid workload name. Expected a lower-case Kubernetes name.");
        }

        return workload;
    }

    public string RequireNamespace(string? candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate) ? _telemetry.WorkloadNamespace : candidate;

        if (!KubernetesName().IsMatch(value))
        {
            throw new TelemetryQueryRejectedException($"'{value}' is not a valid namespace name.");
        }

        // The allow-list is the real boundary. Even with a syntactically
        // valid name, a namespace outside it is refused before any request
        // leaves this process.
        if (!_kubernetes.AllowedNamespaces.Contains(value, StringComparer.Ordinal))
        {
            throw new TelemetryQueryRejectedException(
                $"Namespace '{value}' is not in the allowed list " +
                $"({string.Join(", ", _kubernetes.AllowedNamespaces)}).");
        }

        return value;
    }

    // Clamps a requested time window to something the telemetry systems can
    // answer cheaply. Returns the window actually used.
    public TimeWindow ClampWindow(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new TelemetryQueryRejectedException("The end of a query window must be after its start.");
        }

        var maximum = TimeSpan.FromMinutes(_telemetry.MaxQueryRangeMinutes);

        // Trim from the START, keeping the end. During an investigation the
        // most recent data is what matters, so if something has to be
        // dropped it should be the oldest.
        return end - start > maximum
            ? new TimeWindow(end - maximum, end, WasClamped: true)
            : new TimeWindow(start, end, WasClamped: false);
    }

    public int ClampLineLimit(int requested) =>
        Math.Clamp(requested <= 0 ? _telemetry.MaxLogLinesPerQuery : requested, 1, _telemetry.MaxLogLinesPerQuery);

    public int ClampItemLimit(int requested) =>
        Math.Clamp(requested <= 0 ? _kubernetes.MaxItemsPerQuery : requested, 1, _kubernetes.MaxItemsPerQuery);

    // Escapes free text so it can be embedded in a LogQL string literal.
    //
    // This is what stops a search term such as  " } |= "  from closing the
    // literal and appending new query clauses.
    public static string EscapeLogQlLiteral(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Bounded first: an enormous search term is itself a problem.
        var bounded = text.Length > 200 ? text[..200] : text;

        return bounded
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);
    }
}

public readonly record struct TimeWindow(DateTimeOffset Start, DateTimeOffset End, bool WasClamped)
{
    public TimeSpan Duration => End - Start;
}

// Thrown when a request is refused rather than clamped. Surfaced to the agent
// as a tool error so it can adjust, and recorded so an operator can see that
// a boundary was hit.
public sealed class TelemetryQueryRejectedException : Exception
{
    public TelemetryQueryRejectedException(string message) : base(message) { }
}
