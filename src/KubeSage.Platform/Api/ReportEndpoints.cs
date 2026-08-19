using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Reporting;

namespace KubeSage.Platform.Api;

// Read-only access to generated incident reports.
//
// Every report is returned with its evidence identifiers, and a companion
// endpoint resolves those identifiers to the actual observations. That pairing
// is the point: a report is only worth anything if the reader can check it,
// and checking means seeing the evidence and the query that produced it.
internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/reports").WithTags("Reports");

        group.MapGet("/latest", async (
            ReportRepository reports,
            CancellationToken cancellationToken) =>
        {
            var report = await reports.GetLatestAsync(cancellationToken);

            return report is null
                ? Results.NotFound(new { error = "no_reports_yet" })
                : Results.Ok(ToResponse(report));
        });

        group.MapGet("/", async (
            ReportRepository reports,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var results = await reports.ListAsync(Math.Clamp(limit ?? 20, 1, 100), cancellationToken);
            return Results.Ok(results.Select(ToResponse));
        });

        // A report together with the evidence it cites, resolved. This is the
        // endpoint that makes a conclusion verifiable rather than merely
        // readable.
        group.MapGet("/{id:guid}/evidence", async (
            ReportRepository reports,
            IncidentRepository incidents,
            Guid id,
            CancellationToken cancellationToken) =>
        {
            var all = await reports.ListAsync(100, cancellationToken);
            var report = all.FirstOrDefault(r => r.Id == id);

            if (report is null)
            {
                return Results.NotFound(new { error = "report_not_found", id });
            }

            // A cluster report describes the whole system and has no incident
            // to resolve evidence against, so its citations are listed without
            // expansion rather than the request failing.
            if (report.IncidentId is null)
            {
                return Results.Ok(new
                {
                    report = ToResponse(report),
                    citedEvidence = Array.Empty<object>(),
                    note = "This is a cluster-level report; its evidence is not attached to a single incident."
                });
            }

            var evidence = await incidents.GetEvidenceAsync(report.IncidentId.Value, cancellationToken);
            var cited = report.EvidenceIds.ToHashSet(StringComparer.Ordinal);

            return Results.Ok(new
            {
                report = ToResponse(report),
                citedEvidence = evidence
                    .Where(item => cited.Contains(item.Id))
                    .Select(item => new
                    {
                        item.Id,
                        kind = item.Kind.ToString(),
                        item.Source,
                        observedAtUtc = item.ObservedAtUtc,
                        item.Workload,
                        item.Summary,
                        // Included so a human can rerun it in Grafana and
                        // confirm the claim independently.
                        item.Query
                    })
            });
        });

        return endpoints;
    }

    private static object ToResponse(StoredReport report) => new
    {
        report.Id,
        // Distinguishes a whole-cluster health report from an incident report.
        // Without it the two are indistinguishable in the API, and a caller
        // cannot tell why incidentId is null.
        report.Kind,
        report.IncidentId,
        report.InvestigationId,
        report.Title,
        report.Summary,
        report.Severity,
        report.AffectedWorkloads,
        report.Impact,
        report.Timeline,
        report.LikelyRootCause,
        report.RootCauseCategory,
        report.Confidence,
        report.AlternativeHypotheses,
        report.RecommendedActions,
        report.VerificationSteps,
        report.EvidenceIds,
        createdAtUtc = report.CreatedAtUtc
    };
}
