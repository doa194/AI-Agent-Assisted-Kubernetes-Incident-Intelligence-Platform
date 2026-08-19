using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Kubernetes;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Telemetry;

// Gathers a correlated evidence bundle for one moment in time.
//
// This is the deterministic step that runs BEFORE any agent is involved. By
// the time the Triage Agent sees anything, this class has already fetched the
// logs, metrics, pod state and Kubernetes events surrounding the incident,
// redacted them, and given each item a stable identifier.
//
// The ordering matters to the whole design: an agent never decides what
// evidence exists, only what to make of the evidence that was collected, and
// what additional bounded queries are worth running.
public sealed class EvidenceCollector
{
    private readonly LokiClient _loki;
    private readonly PrometheusClient _prometheus;
    private readonly KubernetesEvidenceClient _kubernetes;
    private readonly TelemetryOptions _telemetry;
    private readonly ILogger<EvidenceCollector> _logger;

    public EvidenceCollector(
        LokiClient loki,
        PrometheusClient prometheus,
        KubernetesEvidenceClient kubernetes,
        IOptions<KubeSageOptions> options,
        ILogger<EvidenceCollector> logger)
    {
        _loki = loki;
        _prometheus = prometheus;
        _kubernetes = kubernetes;
        _telemetry = options.Value.Telemetry;
        _logger = logger;
    }

    // Collects everything relevant to a workload around a moment in time.
    //
    // Each source is fetched independently and a failure in one does not
    // abort the others. That is the behaviour an incident actually needs: if
    // Loki is struggling, Kubernetes state and metrics are still worth having
    // and may well be enough to reach a conclusion.
    public async Task<EvidenceBundle> CollectAsync(
        EvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var namespaceName = request.Namespace ?? _telemetry.WorkloadNamespace;
        var window = request.Window;
        var start = request.Moment - window;
        var end = request.Moment;

        var evidence = new List<Evidence>();
        var failures = new List<string>();

        // --- Kubernetes state: cheapest, most authoritative, most specific ---
        await AddAsync(evidence, failures, "kubernetes pod state", () =>
            _kubernetes.GetPodStatusAsync(namespaceName, request.Workload, cancellationToken));

        await AddAsync(evidence, failures, "kubernetes deployment state", () =>
            _kubernetes.GetDeploymentStatusAsync(namespaceName, request.Workload, cancellationToken));

        await AddAsync(evidence, failures, "kubernetes events", () =>
            _kubernetes.GetEventsAsync(namespaceName, request.Workload, start, cancellationToken));

        // --- Metrics: quantifies the impact ---
        if (!string.IsNullOrWhiteSpace(request.Workload))
        {
            await AddAsync(evidence, failures, "service metrics", () =>
                _prometheus.GetServiceMetricsAsync(request.Workload, window, cancellationToken));
        }

        // --- Logs: signatures first, because they compress best ---
        await AddAsync(evidence, failures, "log signatures", () =>
            _loki.GetErrorSignaturesAsync(namespaceName, request.Workload, start, end, cancellationToken));

        // A small number of raw error lines, for the detail a signature loses.
        await AddAsync(evidence, failures, "error log samples", () =>
            _loki.SearchLogsAsync(
                new LogSearchRequest
                {
                    Namespace = namespaceName,
                    Workload = request.Workload,
                    Level = "error",
                    Start = start,
                    End = end,
                    Limit = Math.Min(request.MaxLogLines, 50)
                },
                cancellationToken));

        var bundle = new EvidenceBundle
        {
            Items = evidence,
            CollectedAtUtc = DateTimeOffset.UtcNow,
            WindowStartUtc = start,
            WindowEndUtc = end,
            Namespace = namespaceName,
            Workload = request.Workload,
            UnavailableSources = failures
        };

        _logger.LogInformation(
            "Collected {EvidenceCount} evidence items for {Workload} over {WindowMinutes}m ({FailureCount} source(s) unavailable)",
            evidence.Count, request.Workload ?? "all workloads", window.TotalMinutes, failures.Count);

        return bundle;
    }

    private async Task AddAsync(
        List<Evidence> target,
        List<string> failures,
        string description,
        Func<Task<IReadOnlyList<Evidence>>> collect)
    {
        try
        {
            target.AddRange(await collect());
        }
        catch (TelemetryQueryRejectedException ex)
        {
            // A rejected query is a programming or configuration mistake, not
            // an outage, so it is logged at a higher level.
            _logger.LogError(ex, "Evidence collection for {Description} was rejected", description);
            failures.Add($"{description} (rejected: {ex.Message})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Evidence collection for {Description} failed", description);
            failures.Add($"{description} ({ex.GetType().Name})");
        }
    }
}

public sealed record EvidenceRequest
{
    public required DateTimeOffset Moment { get; init; }
    public required TimeSpan Window { get; init; }
    public string? Workload { get; init; }
    public string? Namespace { get; init; }
    public int MaxLogLines { get; init; } = 50;
}

// A correlated set of observations, ready to be handed to the workflow.
public sealed record EvidenceBundle
{
    public required IReadOnlyList<Evidence> Items { get; init; }
    public required DateTimeOffset CollectedAtUtc { get; init; }
    public required DateTimeOffset WindowStartUtc { get; init; }
    public required DateTimeOffset WindowEndUtc { get; init; }
    public string? Namespace { get; init; }
    public string? Workload { get; init; }

    // Sources that could not be reached. Carried through to the report so a
    // conclusion drawn from partial data says so, instead of appearing as
    // confident as one drawn from complete data.
    public IReadOnlyList<string> UnavailableSources { get; init; } = [];

    public bool IsComplete => UnavailableSources.Count == 0;

    public IEnumerable<Evidence> OfKind(EvidenceKind kind) => Items.Where(item => item.Kind == kind);

    public Evidence? ById(string id) =>
        Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
}
