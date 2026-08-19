using KubeSage.Platform.Modules.Detection;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Persistence;

namespace KubeSage.Platform.Api;

// The operator API for incidents.
//
// Read-only apart from /analysis/run, which exists for diagnostics. Autonomous
// operation is the primary workflow: incidents appear here because detection
// found them, not because anyone asked.
internal static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/incidents").WithTags("Incidents");

        group.MapGet("/", async (
            IncidentRepository incidents,
            string? state,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            IncidentState? parsed = null;

            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<IncidentState>(state, ignoreCase: true, out var value))
                {
                    return Results.BadRequest(new
                    {
                        error = "unknown_state",
                        allowed = Enum.GetNames<IncidentState>()
                    });
                }

                parsed = value;
            }

            var results = await incidents.ListAsync(
                parsed, Math.Clamp(limit ?? 50, 1, 200), cancellationToken);

            return Results.Ok(results.Select(ToSummary));
        });

        group.MapGet("/{id:guid}", async (
            IncidentRepository incidents,
            Guid id,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.GetAsync(id, cancellationToken);

            if (incident is null)
            {
                return Results.NotFound(new { error = "incident_not_found", id });
            }

            // Evidence is returned with the incident because the two are only
            // meaningful together: a conclusion without the observations
            // behind it is exactly what this project is trying to avoid.
            var evidence = await incidents.GetEvidenceAsync(id, cancellationToken);

            return Results.Ok(new
            {
                incident = ToDetail(incident),
                evidence = evidence.Select(item => new
                {
                    item.Id,
                    kind = item.Kind.ToString(),
                    item.Source,
                    observedAtUtc = item.ObservedAtUtc,
                    item.Workload,
                    item.Summary,
                    item.Attributes,
                    item.Query
                })
            });
        });

        // Diagnostic trigger. Autonomous detection runs on its own schedule;
        // this only exists so an operator can force a pass while testing.
        endpoints.MapPost("/analysis/run", async (
            DetectionEngine engine,
            CancellationToken cancellationToken) =>
        {
            var result = await engine.RunPassAsync(cancellationToken);

            return Results.Ok(new
            {
                evaluatedAtUtc = result.EvaluatedAtUtc,
                candidatesEvaluated = result.CandidatesEvaluated,
                incidentsCreated = result.IncidentsCreated,
                repeatObservations = result.Deduplicated
            });
        }).WithTags("Analysis");

        // Queue depth, so an operator can see whether work is piling up
        // behind a slow or unavailable model.
        endpoints.MapGet("/cluster/status", async (
            IncidentRepository incidents,
            WorkQueue workQueue,
            CancellationToken cancellationToken) =>
        {
            var open = await incidents.ListInFlightAsync(cancellationToken);
            var depth = await workQueue.GetDepthAsync(cancellationToken);

            return Results.Ok(new
            {
                openIncidents = open.Count,
                incidentsByState = open
                    .GroupBy(incident => incident.State.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                workQueue = depth
            });
        }).WithTags("Status");

        return endpoints;
    }

    private static object ToSummary(Incident incident) => new
    {
        incident.Id,
        state = incident.State.ToString(),
        severity = incident.Severity.ToString(),
        incident.Category,
        incident.Title,
        incident.AffectedWorkloads,
        firstDetectedAtUtc = incident.FirstDetectedAtUtc,
        lastDetectedAtUtc = incident.LastDetectedAtUtc,
        incident.OccurrenceCount
    };

    private static object ToDetail(Incident incident) => new
    {
        incident.Id,
        incident.Fingerprint,
        state = incident.State.ToString(),
        severity = incident.Severity.ToString(),
        incident.Category,
        incident.Title,
        incident.DetectionRule,
        incident.Namespace,
        incident.AffectedWorkloads,
        // The measured values that made the rule fire, so the decision can be
        // checked rather than trusted.
        incident.Signals,
        firstDetectedAtUtc = incident.FirstDetectedAtUtc,
        lastDetectedAtUtc = incident.LastDetectedAtUtc,
        recoveredAtUtc = incident.RecoveredAtUtc,
        incident.OccurrenceCount,
        incident.Outcome,
        updatedAtUtc = incident.UpdatedAtUtc
    };
}
