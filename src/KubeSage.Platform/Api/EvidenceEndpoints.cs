using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Kubernetes;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Api;

// Read-only endpoints that expose the deterministic evidence layer directly.
//
// These exist so the observability half of this project can be demonstrated
// and trusted WITHOUT any AI involvement. Given a workload and a moment in
// time, they return exactly the correlated logs, metrics, pod state and
// events an investigation would receive - each with the query that produced
// it, so a person can reproduce it in Grafana.
//
// That independence is the point. If these endpoints return good evidence,
// the deterministic system is useful on its own; the agents then add
// explanation on top of something already known to be sound.
internal static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/evidence").WithTags("Evidence");

        // Correlated bundle for a workload over a recent window.
        group.MapGet("/", async (
            EvidenceCollector collector,
            IOptions<KubeSageOptions> options,
            string? workload,
            int? windowMinutes,
            CancellationToken cancellationToken) =>
        {
            var telemetry = options.Value.Telemetry;
            var window = TimeSpan.FromMinutes(
                Math.Clamp(windowMinutes ?? 15, 1, telemetry.MaxQueryRangeMinutes));

            try
            {
                var bundle = await collector.CollectAsync(
                    new EvidenceRequest
                    {
                        Moment = DateTimeOffset.UtcNow,
                        Window = window,
                        Workload = workload
                    },
                    cancellationToken);

                return Results.Ok(ToResponse(bundle));
            }
            catch (TelemetryQueryRejectedException ex)
            {
                return Results.BadRequest(new { error = "query_rejected", detail = ex.Message });
            }
        });

        // Cluster state only. Answers "what does Kubernetes think is wrong"
        // without touching Loki or Prometheus, which is the fastest useful
        // question during an incident.
        group.MapGet("/kubernetes", async (
            KubernetesEvidenceClient kubernetes,
            string? workload,
            string? ns,
            int? sinceMinutes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var since = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(Math.Clamp(sinceMinutes ?? 30, 1, 240));

                var pods = await kubernetes.GetPodStatusAsync(ns, workload, cancellationToken);
                var deployments = await kubernetes.GetDeploymentStatusAsync(ns, workload, cancellationToken);
                var events = await kubernetes.GetEventsAsync(ns, workload, since, cancellationToken);

                return Results.Ok(new
                {
                    pods = pods.Select(ToItem),
                    deployments = deployments.Select(ToItem),
                    events = events.Select(ToItem)
                });
            }
            catch (TelemetryQueryRejectedException ex)
            {
                return Results.BadRequest(new { error = "query_rejected", detail = ex.Message });
            }
        });

        // Repeated error patterns with counts. Usually the single most
        // informative view during an incident.
        group.MapGet("/log-signatures", async (
            LokiClient loki,
            string? workload,
            string? ns,
            int? windowMinutes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var end = DateTimeOffset.UtcNow;
                var start = end - TimeSpan.FromMinutes(Math.Clamp(windowMinutes ?? 15, 1, 240));

                var signatures = await loki.GetErrorSignaturesAsync(ns, workload, start, end, cancellationToken);
                return Results.Ok(signatures.Select(ToItem));
            }
            catch (TelemetryQueryRejectedException ex)
            {
                return Results.BadRequest(new { error = "query_rejected", detail = ex.Message });
            }
            catch (TelemetryUnavailableException ex)
            {
                return Results.Json(
                    new { error = "telemetry_unavailable", detail = ex.Message }, statusCode: 503);
            }
        });

        return endpoints;
    }

    private static object ToResponse(EvidenceBundle bundle) => new
    {
        collectedAtUtc = bundle.CollectedAtUtc,
        windowStartUtc = bundle.WindowStartUtc,
        windowEndUtc = bundle.WindowEndUtc,
        bundle.Namespace,
        bundle.Workload,
        isComplete = bundle.IsComplete,
        unavailableSources = bundle.UnavailableSources,
        itemCount = bundle.Items.Count,
        // Grouped by kind so the shape of the evidence is obvious at a glance.
        items = bundle.Items
            .GroupBy(item => item.Kind)
            .ToDictionary(
                group => group.Key.ToString(),
                group => group.Select(ToItem).ToList())
    };

    private static object ToItem(Evidence evidence) => new
    {
        evidence.Id,
        kind = evidence.Kind.ToString(),
        evidence.Source,
        observedAtUtc = evidence.ObservedAtUtc,
        evidence.Workload,
        evidence.Summary,
        evidence.Attributes,
        // Included deliberately: this is what lets a human verify the claim
        // independently rather than taking the platform's word for it.
        evidence.Query,
        evidence.RedactedValueCount
    };
}
