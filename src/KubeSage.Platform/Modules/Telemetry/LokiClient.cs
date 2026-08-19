using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Telemetry;

// Reads logs from Loki and turns them into normalised Evidence.
//
// Raw log lines stay in Loki. This adapter fetches only the bounded slice an
// investigation actually asked for, redacts it, and returns evidence items
// that carry the exact query used - so a human can paste that query into
// Grafana and see the same thing.
//
// No language model is involved anywhere in this file. Collection is
// deterministic; interpretation happens later and only on what this returns.
public sealed class LokiClient
{
    private readonly HttpClient _http;
    private readonly TelemetryQuery _guard;
    private readonly SensitiveDataRedactor _redactor;
    private readonly TelemetryOptions _options;
    private readonly ILogger<LokiClient> _logger;

    public LokiClient(
        HttpClient http,
        TelemetryQuery guard,
        SensitiveDataRedactor redactor,
        IOptions<KubeSageOptions> options,
        ILogger<LokiClient> logger)
    {
        _http = http;
        _guard = guard;
        _redactor = redactor;
        _options = options.Value.Telemetry;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/ready", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loki readiness check failed");
            return false;
        }
    }

    // Fetches log lines for one workload, optionally filtered by severity and
    // by a substring.
    public async Task<IReadOnlyList<Evidence>> SearchLogsAsync(
        LogSearchRequest request,
        CancellationToken cancellationToken)
    {
        var namespaceName = _guard.RequireNamespace(request.Namespace);
        var window = _guard.ClampWindow(request.Start, request.End);
        var limit = _guard.ClampLineLimit(request.Limit);

        // Both of these end up inside the stream selector, so both are
        // validated rather than escaped. A label matcher is not a string
        // literal - escaping would not make a hostile value safe there, so
        // anything that is not a plain identifier is refused outright.
        var workload = ValidateWorkload(request.Workload);
        var level = ValidateLevel(request.Level);

        var selector = BuildSelector(namespaceName, workload, level);
        var query = selector;

        if (!string.IsNullOrWhiteSpace(request.Contains))
        {
            // |= is Loki's "line contains" filter. The term is escaped so it
            // cannot terminate the string literal and add query clauses.
            query += $" |= \"{TelemetryQuery.EscapeLogQlLiteral(request.Contains)}\"";
        }

        var entries = await QueryRangeAsync(query, window, limit, cancellationToken);

        return entries
            .Select(entry => ToEvidence(entry, query))
            .ToList();
    }

    // Fetches the logs immediately surrounding a moment in time.
    //
    // This is the query that answers "what was happening when it broke",
    // which is usually far more informative than a keyword search: the line
    // that explains a failure often contains none of the words you would
    // think to search for.
    public async Task<IReadOnlyList<Evidence>> SearchAroundAsync(
        DateTimeOffset moment,
        string? workload,
        string? namespaceName,
        TimeSpan before,
        TimeSpan after,
        int limit,
        CancellationToken cancellationToken)
    {
        var request = new LogSearchRequest
        {
            Namespace = namespaceName,
            Workload = workload,
            Start = moment - before,
            End = moment + after,
            Limit = limit
        };

        return await SearchLogsAsync(request, cancellationToken);
    }

    // Groups error and warning lines into repeated signatures with counts.
    //
    // This is normally the first thing worth looking at during an incident:
    // "the same database connection failure 412 times" is a far better piece
    // of evidence than 412 nearly identical lines, and it costs a fraction of
    // the model context.
    public async Task<IReadOnlyList<Evidence>> GetErrorSignaturesAsync(
        string? namespaceName,
        string? workload,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var resolvedNamespace = _guard.RequireNamespace(namespaceName);
        var window = _guard.ClampWindow(start, end);
        var validatedWorkload = ValidateWorkload(workload);

        // Both severities are fetched together; a warning that repeats
        // hundreds of times is as diagnostic as an error.
        var selector = BuildSelector(resolvedNamespace, validatedWorkload, level: null);
        var query = $"{selector} | json | level=~\"error|warn\"";

        var entries = await QueryRangeAsync(query, window, _options.MaxLogLinesPerQuery, cancellationToken);

        var groups = entries
            .GroupBy(entry => LogSignature.Hash(entry.Message))
            .OrderByDescending(group => group.Count())
            .Take(25);

        var evidence = new List<Evidence>();

        foreach (var group in groups)
        {
            var sample = group.First();
            var normalised = LogSignature.Normalise(sample.Message);
            var redaction = _redactor.Redact(normalised);
            var first = group.Min(e => e.Timestamp);
            var last = group.Max(e => e.Timestamp);

            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(EvidenceKind.LogSignature, "loki", group.Key, sample.Workload),
                Kind = EvidenceKind.LogSignature,
                Source = "loki",
                ObservedAtUtc = last,
                Workload = sample.Workload,
                Namespace = resolvedNamespace,
                Summary = $"{group.Count()}x [{sample.Level}] {redaction.Text}",
                RedactedValueCount = redaction.RedactionCount,
                Query = query,
                Attributes = new Dictionary<string, string>
                {
                    ["occurrences"] = group.Count().ToString(CultureInfo.InvariantCulture),
                    ["signatureHash"] = group.Key,
                    ["level"] = sample.Level ?? "unknown",
                    ["firstSeenUtc"] = first.ToString("O"),
                    ["lastSeenUtc"] = last.ToString("O"),
                    ["distinctPods"] = group.Select(e => e.Pod).Distinct().Count().ToString(CultureInfo.InvariantCulture)
                }
            });
        }

        return evidence;
    }

    // An absent workload means "all workloads in this namespace", which is a
    // legitimate query. A PRESENT one must be a valid Kubernetes name.
    private string? ValidateWorkload(string? workload) =>
        string.IsNullOrWhiteSpace(workload) ? null : _guard.RequireWorkload(workload);

    // Severity is a closed set, so it is checked against that set rather than
    // pattern-matched. Anything else is a caller mistake worth surfacing.
    private static string? ValidateLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }

        return AllowedLevels.Contains(level, StringComparer.Ordinal)
            ? level
            : throw new TelemetryQueryRejectedException(
                $"'{level}' is not a valid log level. Expected one of: {string.Join(", ", AllowedLevels)}.");
    }

    private static readonly string[] AllowedLevels = ["trace", "debug", "info", "warn", "error", "fatal"];

    // Builds the stream selector. Only the three low-cardinality labels
    // Fluent Bit attaches are usable here; anything else must be matched with
    // a line filter after the selector.
    private static string BuildSelector(string namespaceName, string? workload, string? level)
    {
        var parts = new List<string> { $"namespace=\"{namespaceName}\"" };

        if (!string.IsNullOrWhiteSpace(workload))
        {
            // Container name equals the service name for the demo workload.
            parts.Add($"container=\"{workload}\"");
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            parts.Add($"level=\"{level}\"");
        }

        return $"{{{string.Join(", ", parts)}}}";
    }

    private async Task<List<LokiEntry>> QueryRangeAsync(
        string query,
        TimeWindow window,
        int limit,
        CancellationToken cancellationToken)
    {
        // Loki expects nanosecond epoch timestamps.
        var start = window.Start.ToUnixTimeMilliseconds() * 1_000_000L;
        var end = window.End.ToUnixTimeMilliseconds() * 1_000_000L;

        var url = "/loki/api/v1/query_range" +
                  $"?query={Uri.EscapeDataString(query)}" +
                  $"&start={start}&end={end}&limit={limit}&direction=backward";

        using var response = await _http.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new TelemetryUnavailableException(
                $"Loki returned {(int)response.StatusCode} for query '{query}': {Truncate(body, 300)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseStreams(payload);
    }

    private static List<LokiEntry> ParseStreams(JsonElement payload)
    {
        var entries = new List<LokiEntry>();

        if (!payload.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var stream in result.EnumerateArray())
        {
            var labels = stream.TryGetProperty("stream", out var labelElement)
                ? labelElement
                : default;

            if (!stream.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var pair in values.EnumerateArray())
            {
                if (pair.GetArrayLength() < 2)
                {
                    continue;
                }

                var nanoseconds = pair[0].GetString();
                var line = pair[1].GetString() ?? string.Empty;

                entries.Add(BuildEntry(nanoseconds, line, labels));
            }
        }

        return entries;
    }

    // Fluent Bit ships the whole record as JSON, so the useful fields are
    // read from the line itself. A line that is not JSON (PostgreSQL, for
    // example) still produces valid evidence, just with fewer fields.
    private static LokiEntry BuildEntry(string? nanoseconds, string line, JsonElement labels)
    {
        var timestamp = ParseTimestamp(nanoseconds);

        string? level = ReadLabel(labels, "level");
        string? workload = ReadLabel(labels, "container");
        string? pod = null;
        var message = line;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                message = ReadString(root, "msg") ?? line;
                level ??= ReadString(root, "level");
                workload ??= ReadString(root, "service");
                pod = ReadString(root, "pod");

                // Fields the demo services attach that materially help an
                // investigation are promoted into the message, because they
                // are what identifies WHICH dependency or operation failed.
                var dependency = ReadString(root, "dependency");
                var operation = ReadString(root, "operation");
                var correlationId = ReadString(root, "correlationId");

                var suffix = new List<string>();
                if (operation is not null) suffix.Add($"operation={operation}");
                if (dependency is not null) suffix.Add($"dependency={dependency}");
                if (correlationId is not null) suffix.Add($"correlationId={correlationId}");

                var errorMessage = ReadString(root, "errorMessage");
                if (errorMessage is not null) suffix.Add($"error={errorMessage}");

                if (suffix.Count > 0)
                {
                    message = $"{message} ({string.Join(", ", suffix)})";
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON. The raw line is still perfectly good evidence.
        }

        return new LokiEntry(timestamp, level, workload, pod, message);
    }

    private static DateTimeOffset ParseTimestamp(string? nanoseconds)
    {
        if (long.TryParse(nanoseconds, CultureInfo.InvariantCulture, out var value))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000L);
        }

        return DateTimeOffset.UtcNow;
    }

    private static string? ReadLabel(JsonElement labels, string name) =>
        labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private Evidence ToEvidence(LokiEntry entry, string query)
    {
        var redaction = _redactor.Redact(entry.Message);

        return new Evidence
        {
            Id = Evidence.CreateId(
                EvidenceKind.LogSample, "loki",
                entry.Timestamp.ToString("O"), entry.Workload, entry.Pod, LogSignature.Hash(entry.Message)),
            Kind = EvidenceKind.LogSample,
            Source = "loki",
            ObservedAtUtc = entry.Timestamp,
            Workload = entry.Workload,
            Summary = $"[{entry.Level ?? "info"}] {Truncate(redaction.Text, 500)}",
            RedactedValueCount = redaction.RedactionCount,
            Query = query,
            Attributes = new Dictionary<string, string>
            {
                ["level"] = entry.Level ?? "unknown",
                ["pod"] = entry.Pod ?? "unknown",
                ["timestampUtc"] = entry.Timestamp.ToString("O")
            }
        };
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "...";

    private sealed record LokiEntry(
        DateTimeOffset Timestamp,
        string? Level,
        string? Workload,
        string? Pod,
        string Message);
}

public sealed record LogSearchRequest
{
    public string? Namespace { get; init; }
    public string? Workload { get; init; }
    public string? Level { get; init; }
    public string? Contains { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public int Limit { get; init; }
}

// Raised when a telemetry system cannot answer. Kept distinct from a rejected
// query, because the correct response is different: an unavailable dependency
// means the investigation should report reduced confidence, whereas a
// rejected query means the caller asked for something it may not have.
public sealed class TelemetryUnavailableException : Exception
{
    public TelemetryUnavailableException(string message) : base(message) { }

    public TelemetryUnavailableException(string message, Exception inner) : base(message, inner) { }
}
