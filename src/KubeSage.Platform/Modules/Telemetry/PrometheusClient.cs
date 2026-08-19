using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Telemetry;

// Reads metrics from Prometheus and turns them into normalised Evidence.
//
// Every PromQL expression in this file is written here, in code. A caller
// supplies a workload name and a time window, never a query. That is a
// deliberate restriction: letting an agent write arbitrary PromQL would make
// query cost unbounded and give it a way to read series it was never meant
// to see. The set of questions that can be asked is fixed and small, which is
// also what makes the answers comparable between incidents.
public sealed class PrometheusClient
{
    private readonly HttpClient _http;
    private readonly TelemetryQuery _guard;
    private readonly ILogger<PrometheusClient> _logger;

    public PrometheusClient(HttpClient http, TelemetryQuery guard, ILogger<PrometheusClient> logger)
    {
        _http = http;
        _guard = guard;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/-/healthy", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus health check failed");
            return false;
        }
    }

    // Fraction of requests a workload answered with 5xx over the window.
    //
    // Only 5xx counts. A 4xx means the caller sent something invalid, which
    // the traffic generator does deliberately and continuously; counting it
    // would make every healthy period look like an incident.
    public async Task<ServiceRate?> GetHttpErrorRateAsync(
        string workload,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var name = _guard.RequireWorkload(workload);
        var range = FormatRange(window);

        var errors = await ScalarAsync(
            $"sum(increase(kubesage_http_requests_total{{service=\"{name}\", status_class=\"5xx\"}}[{range}]))",
            cancellationToken);

        var total = await ScalarAsync(
            $"sum(increase(kubesage_http_requests_total{{service=\"{name}\"}}[{range}]))",
            cancellationToken);

        if (total is null or 0)
        {
            return null;
        }

        return new ServiceRate(name, (errors ?? 0) / total.Value, total.Value);
    }

    // 95th percentile request duration. Reported from the histogram rather
    // than an average because an average hides the tail, and the tail is
    // exactly what a latency incident looks like.
    public async Task<double?> GetLatencyP95Async(
        string workload,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var name = _guard.RequireWorkload(workload);
        var range = FormatRange(window);

        return await ScalarAsync(
            "histogram_quantile(0.95, sum by (le) (rate(" +
            $"kubesage_http_request_duration_seconds_bucket{{service=\"{name}\"}}[{range}])))",
            cancellationToken);
    }

    // Failures calling a downstream dependency, broken down by which
    // dependency and what kind of failure.
    //
    // This is the single most valuable metric for root-cause work in this
    // workload: it says "order-api could not reach payment-simulator, and it
    // was a timeout", which points at the cause rather than the symptom.
    public async Task<IReadOnlyList<DependencyFailure>> GetDependencyFailuresAsync(
        string? workload,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var range = FormatRange(window);
        var selector = string.IsNullOrWhiteSpace(workload)
            ? string.Empty
            : $"service=\"{_guard.RequireWorkload(workload)}\"";

        var query =
            $"sum by (service, dependency, kind) (increase(kubesage_dependency_failures_total{{{selector}}}[{range}]))";

        var samples = await VectorAsync(query, cancellationToken);

        return samples
            .Where(sample => sample.Value > 0)
            .Select(sample => new DependencyFailure(
                Service: sample.Labels.GetValueOrDefault("service", "unknown"),
                Dependency: sample.Labels.GetValueOrDefault("dependency", "unknown"),
                Kind: sample.Labels.GetValueOrDefault("kind", "unknown"),
                Count: sample.Value))
            .OrderByDescending(failure => failure.Count)
            .ToList();
    }

    // How long calls to each dependency are taking. A dependency whose
    // latency has jumped while its failure count is still low is the early
    // signature of the payment-latency scenario.
    public async Task<IReadOnlyList<DependencyLatency>> GetDependencyLatencyAsync(
        string? workload,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var range = FormatRange(window);
        var selector = string.IsNullOrWhiteSpace(workload)
            ? string.Empty
            : $"service=\"{_guard.RequireWorkload(workload)}\"";

        var query =
            "histogram_quantile(0.95, sum by (service, dependency, le) (rate(" +
            $"kubesage_dependency_duration_seconds_bucket{{{selector}}}[{range}])))";

        var samples = await VectorAsync(query, cancellationToken);

        return samples
            .Where(sample => !double.IsNaN(sample.Value))
            .Select(sample => new DependencyLatency(
                Service: sample.Labels.GetValueOrDefault("service", "unknown"),
                Dependency: sample.Labels.GetValueOrDefault("dependency", "unknown"),
                P95Seconds: sample.Value))
            .OrderByDescending(latency => latency.P95Seconds)
            .ToList();
    }

    // A compact picture of one service's health, returned as evidence.
    public async Task<IReadOnlyList<Evidence>> GetServiceMetricsAsync(
        string workload,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var name = _guard.RequireWorkload(workload);
        var observedAt = DateTimeOffset.UtcNow;
        var evidence = new List<Evidence>();

        var errorRate = await GetHttpErrorRateAsync(name, window, cancellationToken);
        if (errorRate is not null)
        {
            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(EvidenceKind.Metric, "prometheus", name, "http_error_rate", Bucket(observedAt)),
                Kind = EvidenceKind.Metric,
                Source = "prometheus",
                ObservedAtUtc = observedAt,
                Workload = name,
                Summary =
                    $"{name}: {errorRate.Ratio:P1} of requests returned 5xx " +
                    $"over the last {window.TotalMinutes:F0} minutes ({errorRate.TotalRequests:F0} requests)",
                Query = $"sum(increase(kubesage_http_requests_total{{service=\"{name}\", status_class=\"5xx\"}}[{FormatRange(window)}]))",
                Attributes = new Dictionary<string, string>
                {
                    ["errorRatio"] = errorRate.Ratio.ToString("F4", CultureInfo.InvariantCulture),
                    ["totalRequests"] = errorRate.TotalRequests.ToString("F0", CultureInfo.InvariantCulture),
                    ["windowMinutes"] = window.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)
                }
            });
        }

        var latency = await GetLatencyP95Async(name, window, cancellationToken);
        if (latency is not null && !double.IsNaN(latency.Value))
        {
            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(EvidenceKind.Metric, "prometheus", name, "latency_p95", Bucket(observedAt)),
                Kind = EvidenceKind.Metric,
                Source = "prometheus",
                ObservedAtUtc = observedAt,
                Workload = name,
                Summary = $"{name}: 95th percentile request duration {latency.Value:F3}s",
                Query = $"histogram_quantile(0.95, ... kubesage_http_request_duration_seconds_bucket{{service=\"{name}\"}})",
                Attributes = new Dictionary<string, string>
                {
                    ["p95Seconds"] = latency.Value.ToString("F4", CultureInfo.InvariantCulture)
                }
            });
        }

        foreach (var failure in await GetDependencyFailuresAsync(name, window, cancellationToken))
        {
            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(
                    EvidenceKind.Metric, "prometheus",
                    failure.Service, failure.Dependency, failure.Kind, Bucket(observedAt)),
                Kind = EvidenceKind.Metric,
                Source = "prometheus",
                ObservedAtUtc = observedAt,
                Workload = failure.Service,
                Summary =
                    $"{failure.Service}: {failure.Count:F0} '{failure.Kind}' failures calling {failure.Dependency}",
                Query = "sum by (service, dependency, kind) (increase(kubesage_dependency_failures_total[...]))",
                Attributes = new Dictionary<string, string>
                {
                    ["dependency"] = failure.Dependency,
                    ["failureKind"] = failure.Kind,
                    ["count"] = failure.Count.ToString("F0", CultureInfo.InvariantCulture)
                }
            });
        }

        foreach (var latencyEntry in await GetDependencyLatencyAsync(name, window, cancellationToken))
        {
            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(
                    EvidenceKind.Metric, "prometheus",
                    latencyEntry.Service, latencyEntry.Dependency, "latency", Bucket(observedAt)),
                Kind = EvidenceKind.Metric,
                Source = "prometheus",
                ObservedAtUtc = observedAt,
                Workload = latencyEntry.Service,
                Summary =
                    $"{latencyEntry.Service}: calls to {latencyEntry.Dependency} " +
                    $"take {latencyEntry.P95Seconds:F3}s at the 95th percentile",
                Query = "histogram_quantile(0.95, ... kubesage_dependency_duration_seconds_bucket)",
                Attributes = new Dictionary<string, string>
                {
                    ["dependency"] = latencyEntry.Dependency,
                    ["p95Seconds"] = latencyEntry.P95Seconds.ToString("F4", CultureInfo.InvariantCulture)
                }
            });
        }

        return evidence;
    }

    // Which workloads are currently reporting metrics at all. Used by
    // detection to know what it should be evaluating.
    public async Task<IReadOnlyList<string>> GetKnownServicesAsync(CancellationToken cancellationToken)
    {
        var samples = await VectorAsync(
            "count by (service) (kubesage_http_requests_total)", cancellationToken);

        return samples
            .Select(sample => sample.Labels.GetValueOrDefault("service"))
            .Where(service => !string.IsNullOrEmpty(service))
            .Select(service => service!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(service => service, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<double?> ScalarAsync(string query, CancellationToken cancellationToken)
    {
        var samples = await VectorAsync(query, cancellationToken);
        return samples.Count == 0 ? null : samples[0].Value;
    }

    private async Task<IReadOnlyList<PrometheusSample>> VectorAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/query?query={Uri.EscapeDataString(query)}";

        using var response = await _http.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new TelemetryUnavailableException(
                $"Prometheus returned {(int)response.StatusCode} for '{query}': {body[..Math.Min(body.Length, 300)]}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var samples = new List<PrometheusSample>();

        if (!payload.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            return samples;
        }

        foreach (var item in result.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);

            if (item.TryGetProperty("metric", out var metric) && metric.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in metric.EnumerateObject())
                {
                    labels[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            // An instant vector sample is ["<unix time>", "<value as string>"].
            if (item.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.Array &&
                value.GetArrayLength() >= 2 &&
                double.TryParse(value[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                samples.Add(new PrometheusSample(labels, parsed));
            }
        }

        return samples;
    }

    private static string FormatRange(TimeSpan window) =>
        $"{Math.Max(1, (int)Math.Round(window.TotalMinutes))}m";

    // Rounds the observation time down to the minute when building evidence
    // identifiers. Without this, collecting the same metric twice a few
    // seconds apart would mint two identifiers for what is really one fact,
    // and an agent could cite both as independent corroboration.
    private static string Bucket(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);

    private sealed record PrometheusSample(Dictionary<string, string> Labels, double Value);
}

public sealed record ServiceRate(string Service, double Ratio, double TotalRequests);

public sealed record DependencyFailure(string Service, string Dependency, string Kind, double Count);

public sealed record DependencyLatency(string Service, string Dependency, double P95Seconds);
