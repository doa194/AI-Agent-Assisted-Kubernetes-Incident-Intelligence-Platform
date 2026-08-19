using System.Text.Json;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Kubernetes;
using KubeSage.Platform.Modules.Retrieval;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// The complete set of actions an agent is permitted to take.
//
// This is an allow-list, not a filter. There is no generic "run this query"
// tool, no shell, no kubectl, and nothing that writes. An agent cannot reach
// the cluster except through one of the methods below, each of which validates
// its inputs and clamps its ranges before touching anything.
//
// Three independent layers protect the cluster, and they were designed to fail
// separately:
//
//   1. this allow-list - no mutating operation is even expressible;
//   2. input validation and range clamping in TelemetryQuery;
//   3. Kubernetes RBAC - the service account has no write verb at all, so even
//      a bug here cannot change the cluster.
//
// A budget is enforced across the whole investigation. Its purpose is not
// cost but time: on a local model each tool call plus the reasoning around it
// takes real minutes, and an agent that loops would consume the entire
// investigation window without producing anything.
public sealed class InvestigationTools
{
    private readonly LokiClient _loki;
    private readonly PrometheusClient _prometheus;
    private readonly KubernetesEvidenceClient _kubernetes;
    private readonly MemoryRetriever _memory;
    private readonly InvestigationOptions _options;
    private readonly ILogger<InvestigationTools> _logger;

    public InvestigationTools(
        LokiClient loki,
        PrometheusClient prometheus,
        KubernetesEvidenceClient kubernetes,
        MemoryRetriever memory,
        IOptions<KubeSageOptions> options,
        ILogger<InvestigationTools> logger)
    {
        _loki = loki;
        _prometheus = prometheus;
        _kubernetes = kubernetes;
        _memory = memory;
        _options = options.Value.Investigation;
        _logger = logger;
    }

    // Set per investigation so a retrieved past incident is never the incident
    // currently being investigated - which would otherwise look like strong
    // corroboration of whatever it already says.
    public Guid? CurrentIncidentId { get; set; }

    // The tools an agent may call, described for the prompt. Names here are
    // the contract; adding one means adding a case in ExecuteAsync too.
    public static IReadOnlyList<ToolDescriptor> Descriptors { get; } =
    [
        new("SearchLogs",
            "Search logs for one workload. Arguments: workload (required), level (error|warn|info), contains (text), minutes (1-60)."),
        new("SearchLogsAroundTimestamp",
            "Fetch logs surrounding a moment in time. Arguments: workload, timestampUtc (ISO-8601), beforeSeconds, afterSeconds."),
        new("GetPodStatus",
            "Current pod phase, readiness, restart count and termination reason. Arguments: workload (optional)."),
        new("GetRestartHistory",
            "Restart counts and waiting/termination reasons for all pods. No arguments."),
        new("GetKubernetesEvents",
            "Recent Kubernetes events such as BackOff, Unhealthy, Killing. Arguments: workload (optional), minutes."),
        new("GetDeploymentStatus",
            "Desired versus ready replicas for deployments. Arguments: workload (optional)."),
        new("GetServiceMetrics",
            "Error rate, latency percentiles and dependency timings. Arguments: workload (required), minutes."),
        new("SearchSimilarIncidents",
            "Find past incidents resembling a description. These are HISTORICAL, not evidence about the current incident. Arguments: query (required), workload."),
        new("SearchRunbooks",
            "Find runbook guidance for a described problem. This is documentation, not an observation. Arguments: query (required), category.")
    ];

    // Runs one tool call, enforcing the budget.
    //
    // Failures are returned as text rather than thrown, so an agent that asks
    // for something out of bounds learns why and can adjust, instead of the
    // whole investigation collapsing on a single bad argument.
    public async Task<ToolCallResult> ExecuteAsync(
        ToolCall call,
        InvestigationBudget budget,
        CancellationToken cancellationToken)
    {
        if (!budget.TryConsume())
        {
            return ToolCallResult.Rejected(
                $"Tool call budget of {_options.MaxToolCalls} exhausted. " +
                "Draw a conclusion from the evidence already gathered, or report it as inconclusive.");
        }

        try
        {
            var evidence = await DispatchAsync(call, cancellationToken);

            _logger.LogInformation(
                "Investigation tool {Tool} returned {EvidenceCount} evidence item(s) ({Remaining} call(s) left)",
                call.Name, evidence.Count, budget.Remaining);

            return ToolCallResult.Success(evidence);
        }
        catch (TelemetryQueryRejectedException ex)
        {
            // The agent asked for something outside its permitted scope.
            // Recorded at warning level: it is not a crash, but it is worth
            // seeing that a boundary was reached.
            _logger.LogWarning("Tool {Tool} rejected: {Reason}", call.Name, ex.Message);
            return ToolCallResult.Rejected(ex.Message);
        }
        catch (TelemetryUnavailableException ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} could not reach its telemetry source", call.Name);
            return ToolCallResult.Rejected(
                $"That telemetry source is currently unavailable ({ex.Message}). " +
                "Continue with the evidence you have and note the gap.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} failed unexpectedly", call.Name);
            return ToolCallResult.Rejected($"The tool failed: {ex.GetType().Name}.");
        }
    }

    private async Task<IReadOnlyList<Evidence>> DispatchAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        return call.Name switch
        {
            "SearchLogs" => await _loki.SearchLogsAsync(
                new LogSearchRequest
                {
                    Workload = call.GetString("workload"),
                    Level = call.GetString("level"),
                    Contains = call.GetString("contains"),
                    Start = now - Minutes(call, 15),
                    End = now,
                    Limit = 40
                },
                cancellationToken),

            "SearchLogsAroundTimestamp" => await _loki.SearchAroundAsync(
                call.GetTimestamp("timestampUtc") ?? now,
                call.GetString("workload"),
                namespaceName: null,
                before: TimeSpan.FromSeconds(Math.Clamp(call.GetInt("beforeSeconds") ?? 120, 10, 900)),
                after: TimeSpan.FromSeconds(Math.Clamp(call.GetInt("afterSeconds") ?? 60, 10, 900)),
                limit: 40,
                cancellationToken),

            "GetPodStatus" => await _kubernetes.GetPodStatusAsync(
                null, call.GetString("workload"), cancellationToken),

            "GetRestartHistory" => (await _kubernetes.GetPodStatusAsync(null, null, cancellationToken))
                .Where(e => e.Attributes.ContainsKey("restartCount"))
                .ToList(),

            "GetKubernetesEvents" => await _kubernetes.GetEventsAsync(
                null, call.GetString("workload"), now - Minutes(call, 30), cancellationToken),

            "GetDeploymentStatus" => await _kubernetes.GetDeploymentStatusAsync(
                null, call.GetString("workload"), cancellationToken),

            "SearchSimilarIncidents" => await _memory.SearchSimilarIncidentsAsync(
                call.GetString("query") ?? throw new TelemetryQueryRejectedException(
                    "SearchSimilarIncidents requires a 'query' argument describing the problem."),
                call.GetString("workload"),
                CurrentIncidentId,
                cancellationToken),

            "SearchRunbooks" => await _memory.SearchRunbooksAsync(
                call.GetString("query") ?? throw new TelemetryQueryRejectedException(
                    "SearchRunbooks requires a 'query' argument describing the problem."),
                call.GetString("category"),
                cancellationToken),

            "GetServiceMetrics" => await _prometheus.GetServiceMetricsAsync(
                call.GetString("workload") ?? throw new TelemetryQueryRejectedException(
                    "GetServiceMetrics requires a 'workload' argument."),
                Minutes(call, 15),
                cancellationToken),

            _ => throw new TelemetryQueryRejectedException(
                $"'{call.Name}' is not an available tool. Available tools: " +
                string.Join(", ", Descriptors.Select(d => d.Name)))
        };
    }

    // Time ranges are clamped here as well as in the telemetry layer, so a
    // tool argument can never widen a query beyond an hour.
    private static TimeSpan Minutes(ToolCall call, int fallback) =>
        TimeSpan.FromMinutes(Math.Clamp(call.GetInt("minutes") ?? fallback, 1, 60));
}

public sealed record ToolDescriptor(string Name, string Description);

// A tool call requested by an agent. Arguments arrive as JSON, so every
// accessor is defensive: a missing or wrongly typed argument returns null
// rather than throwing, and the tool decides whether that is fatal.
public sealed record ToolCall(string Name, JsonElement Arguments)
{
    public string? GetString(string key) =>
        Arguments.ValueKind == JsonValueKind.Object &&
        Arguments.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public int? GetInt(string key)
    {
        if (Arguments.ValueKind != JsonValueKind.Object || !Arguments.TryGetProperty(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            // Models frequently return numbers as strings despite the schema.
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public DateTimeOffset? GetTimestamp(string key) =>
        DateTimeOffset.TryParse(GetString(key), out var value) ? value : null;
}

public sealed record ToolCallResult(bool Succeeded, IReadOnlyList<Evidence> Evidence, string? Message)
{
    public static ToolCallResult Success(IReadOnlyList<Evidence> evidence) => new(true, evidence, null);

    public static ToolCallResult Rejected(string message) => new(false, [], message);
}

// Tracks how many tool calls an investigation has left.
//
// Deliberately a simple counter rather than anything cleverer: the limit needs
// to be obvious in logs and impossible to argue with.
public sealed class InvestigationBudget
{
    private int _used;

    public InvestigationBudget(int maxToolCalls) => MaxToolCalls = maxToolCalls;

    public int MaxToolCalls { get; }

    public int Used => _used;

    public int Remaining => Math.Max(0, MaxToolCalls - _used);

    public bool TryConsume()
    {
        if (_used >= MaxToolCalls)
        {
            return false;
        }

        _used++;
        return true;
    }
}
