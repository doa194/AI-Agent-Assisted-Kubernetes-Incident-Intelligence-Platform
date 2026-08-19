using System.Text.Json;
using Dapper;
using KubeSage.Platform.Modules.AgentWorkflows;
using KubeSage.Platform.Modules.Incidents;
using Npgsql;

namespace KubeSage.Platform.Modules.Reporting;

// Stores investigations, agent executions, hypotheses and reports.
//
// What is written here is the permanent record of what the AI layer concluded
// and why. Two rules shape it:
//
//   * only VALIDATED structured results are stored - never raw model text,
//     and never the model's private reasoning;
//   * every stored claim keeps its evidence identifiers, so a report read
//     months later can still be traced back to the observations behind it.
public sealed class ReportRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ReportRepository> _logger;

    public ReportRepository(NpgsqlDataSource dataSource, ILogger<ReportRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    // Writes the investigation, its agent executions and its hypotheses in one
    // transaction, so a partial record can never be read as a complete one.
    public async Task SaveInvestigationAsync(
        InvestigationContext context,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO investigations (
                id, incident_id, state, attempt, started_at_utc, completed_at_utc, duration_ms,
                failure_reason, evidence_complete, unavailable_sources)
            VALUES (
                @id, @incidentId, @state, 1, @startedAt, @completedAt, @durationMs,
                @failureReason, @evidenceComplete, @unavailableSources)
            ON CONFLICT (id) DO NOTHING
            """,
            new
            {
                id = context.InvestigationId,
                incidentId = context.IncidentId,
                state = context.FinalState.ToString(),
                startedAt = context.StartedAtUtc.UtcDateTime,
                completedAt = DateTimeOffset.UtcNow.UtcDateTime,
                durationMs = (int)duration.TotalMilliseconds,
                failureReason = context.FinalState is IncidentState.Failed or IncidentState.Inconclusive
                    ? context.TerminalOutcome
                    : null,
                evidenceComplete = context.UnavailableSources.Count == 0,
                unavailableSources = context.UnavailableSources.ToArray()
            },
            transaction, cancellationToken: cancellationToken));

        foreach (var execution in context.AgentExecutions)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO agent_executions (
                    id, investigation_id, agent_name, started_at_utc, completed_at_utc,
                    duration_ms, tool_call_count, tools_used, succeeded, failure_reason, result)
                VALUES (
                    @id, @investigationId, @agentName, @startedAt, @completedAt,
                    @durationMs, @toolCallCount, @toolsUsed, @succeeded, @failureReason, @result::jsonb)
                """,
                new
                {
                    id = Guid.CreateVersion7(),
                    investigationId = context.InvestigationId,
                    agentName = execution.AgentName,
                    startedAt = execution.StartedAtUtc.UtcDateTime,
                    completedAt = execution.CompletedAtUtc.UtcDateTime,
                    durationMs = execution.DurationMs,
                    toolCallCount = execution.ToolCallCount,
                    toolsUsed = execution.ToolsUsed,
                    succeeded = execution.Succeeded,
                    failureReason = execution.FailureReason,
                    // The validated structured result only. There is no column
                    // for chain of thought anywhere in this schema.
                    result = execution.Result?.GetRawText()
                },
                transaction, cancellationToken: cancellationToken));
        }

        var hypotheses = context.Investigation?.Hypotheses ?? [];

        for (var rank = 0; rank < hypotheses.Length; rank++)
        {
            var hypothesis = hypotheses[rank];

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO hypotheses (
                    id, investigation_id, rank, statement, root_cause_category,
                    suspected_workload, confidence, evidence_ids)
                VALUES (
                    @id, @investigationId, @rank, @statement, @rootCauseCategory,
                    @suspectedWorkload, @confidence, @evidenceIds)
                """,
                new
                {
                    id = Guid.CreateVersion7(),
                    investigationId = context.InvestigationId,
                    rank = rank + 1,
                    statement = hypothesis.Statement,
                    rootCauseCategory = hypothesis.RootCauseCategory,
                    suspectedWorkload = hypothesis.SuspectedWorkload,
                    confidence = hypothesis.Confidence,
                    evidenceIds = hypothesis.EvidenceIds
                },
                transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveReportAsync(InvestigationContext context, CancellationToken cancellationToken)
    {
        var report = context.Report!;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reports (
                id, incident_id, investigation_id, kind, title, summary, severity,
                affected_workloads, impact, timeline, likely_root_cause, root_cause_category,
                confidence, alternatives, recommended_actions, verification_steps, evidence_ids)
            VALUES (
                @id, @incidentId, @investigationId, @kind, @title, @summary, @severity,
                @affectedWorkloads, @impact, @timeline::jsonb, @likelyRootCause, @rootCauseCategory,
                @confidence, @alternatives::jsonb, @recommendedActions::jsonb,
                @verificationSteps::jsonb, @evidenceIds)
            """,
            new
            {
                id = Guid.CreateVersion7(),
                incidentId = context.IncidentId,
                investigationId = context.InvestigationId,
                kind = "incident",
                title = report.Title,
                summary = report.Summary,
                severity = context.Triage?.Severity ?? context.Incident.Severity.ToString(),
                affectedWorkloads = context.Triage?.AffectedWorkloads
                                    ?? context.Incident.AffectedWorkloads.ToArray(),
                impact = report.Impact,
                timeline = JsonSerializer.Serialize(report.Timeline),
                likelyRootCause = report.LikelyRootCause,
                rootCauseCategory = report.RootCauseCategory,
                confidence = report.Confidence,
                alternatives = JsonSerializer.Serialize(report.AlternativeHypotheses),
                recommendedActions = JsonSerializer.Serialize(report.RecommendedActions),
                verificationSteps = JsonSerializer.Serialize(report.VerificationSteps),
                evidenceIds = report.EvidenceIds
            },
            cancellationToken: cancellationToken));

        // The report is also written to the application log, because section
        // 14 of the requirements asks for automatically generated reports to
        // appear in structured operational output, not only in the API.
        _logger.LogInformation(
            "Incident report generated for {IncidentId}: {Title} | root cause: {RootCause} " +
            "(category={RootCauseCategory}, confidence={Confidence:P0}, evidence={EvidenceCount} item(s))",
            context.IncidentId, report.Title, report.LikelyRootCause,
            report.RootCauseCategory, report.Confidence, report.EvidenceIds.Length);
    }

    // Stores a whole-cluster report.
    //
    // incident_id and investigation_id are deliberately null: this describes
    // the system, not one failure. A database check constraint still requires
    // them for reports of kind 'incident', so relaxing those columns cannot
    // produce an orphaned incident report by mistake.
    public async Task<Guid> SaveClusterReportAsync(
        string kind,
        AgentWorkflows.ClusterHealthResult result,
        IReadOnlyList<Telemetry.Evidence> evidence,
        int openIncidentCount,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reports (
                id, incident_id, investigation_id, kind, title, summary, severity,
                affected_workloads, impact, timeline, likely_root_cause, root_cause_category,
                confidence, alternatives, recommended_actions, verification_steps, evidence_ids)
            VALUES (
                @id, NULL, NULL, @kind, @title, @summary, @severity,
                @workloads, @impact, '[]'::jsonb, NULL, NULL,
                NULL, '[]'::jsonb, @concerns::jsonb, '[]'::jsonb, @evidenceIds)
            """,
            new
            {
                id,
                kind,
                title = result.Headline,
                summary = result.Summary,
                // The cluster status doubles as the report's severity so a
                // single "latest report" view can show both kinds sensibly.
                severity = result.OverallStatus,
                workloads = result.WorkloadsReviewed,
                impact = $"{openIncidentCount} open incident(s), {evidence.Count} evidence item(s) examined",
                concerns = JsonSerializer.Serialize(result.Concerns),
                evidenceIds = result.EvidenceIds
            },
            cancellationToken: cancellationToken));

        // Written to the log as well as the database, so autonomous output is
        // visible in the operational stream and not only through the API.
        _logger.LogInformation(
            "Cluster {Kind} report: [{Status}] {Headline} | reviewed {WorkloadCount} workload(s), " +
            "{ConcernCount} concern(s), {EvidenceCount} evidence item(s) cited",
            kind, result.OverallStatus, result.Headline,
            result.WorkloadsReviewed.Length, result.Concerns.Length, result.EvidenceIds.Length);

        return id;
    }

    public async Task<StoredReport?> GetLatestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<ReportRow>(new CommandDefinition(
            $"{ReportSelect} ORDER BY created_at_utc DESC LIMIT 1",
            cancellationToken: cancellationToken));

        return row?.ToReport();
    }

    public async Task<IReadOnlyList<StoredReport>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<ReportRow>(new CommandDefinition(
            $"{ReportSelect} ORDER BY created_at_utc DESC LIMIT @limit",
            new { limit }, cancellationToken: cancellationToken));

        return rows.Select(row => row.ToReport()).ToList();
    }

    public async Task<StoredReport?> GetForIncidentAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<ReportRow>(new CommandDefinition(
            $"{ReportSelect} WHERE incident_id = @incidentId ORDER BY created_at_utc DESC LIMIT 1",
            new { incidentId }, cancellationToken: cancellationToken));

        return row?.ToReport();
    }

    private const string ReportSelect =
        """
        SELECT id, kind, incident_id AS IncidentId, investigation_id AS InvestigationId, title, summary,
               severity, affected_workloads AS AffectedWorkloads, impact, timeline::text AS Timeline,
               likely_root_cause AS LikelyRootCause, root_cause_category AS RootCauseCategory,
               confidence, alternatives::text AS Alternatives,
               recommended_actions::text AS RecommendedActions,
               verification_steps::text AS VerificationSteps, evidence_ids AS EvidenceIds,
               created_at_utc AS CreatedAtUtc
        FROM reports
        """;

    // Mutable class with a parameterless constructor: Dapper matches
    // constructors by the reader's column types, and Npgsql returns DateTime
    // for timestamptz, which a positional record would fail to bind.
    private sealed class ReportRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "incident";
        public Guid? IncidentId { get; init; }
        public Guid? InvestigationId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string[] AffectedWorkloads { get; init; } = [];
        public string? Impact { get; init; }
        public string Timeline { get; init; } = "[]";
        public string? LikelyRootCause { get; init; }
        public string? RootCauseCategory { get; init; }
        public double? Confidence { get; init; }
        public string Alternatives { get; init; } = "[]";
        public string RecommendedActions { get; init; } = "[]";
        public string VerificationSteps { get; init; } = "[]";
        public string[] EvidenceIds { get; init; } = [];
        public DateTime CreatedAtUtc { get; init; }

        public StoredReport ToReport() => new()
        {
            Id = Id,
            Kind = Kind,
            IncidentId = IncidentId,
            InvestigationId = InvestigationId,
            Title = Title,
            Summary = Summary,
            Severity = Severity,
            AffectedWorkloads = AffectedWorkloads,
            Impact = Impact,
            Timeline = Deserialize(Timeline),
            LikelyRootCause = LikelyRootCause,
            RootCauseCategory = RootCauseCategory,
            Confidence = Confidence,
            AlternativeHypotheses = Deserialize(Alternatives),
            RecommendedActions = Deserialize(RecommendedActions),
            VerificationSteps = Deserialize(VerificationSteps),
            EvidenceIds = EvidenceIds,
            CreatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc))
        };

        private static string[] Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

public sealed record StoredReport
{
    public required Guid Id { get; init; }
    public string Kind { get; init; } = "incident";
    public Guid? IncidentId { get; init; }
    public Guid? InvestigationId { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Severity { get; init; }
    public required string[] AffectedWorkloads { get; init; }
    public string? Impact { get; init; }
    public string[] Timeline { get; init; } = [];
    public string? LikelyRootCause { get; init; }
    public string? RootCauseCategory { get; init; }
    public double? Confidence { get; init; }
    public string[] AlternativeHypotheses { get; init; } = [];
    public string[] RecommendedActions { get; init; } = [];
    public string[] VerificationSteps { get; init; } = [];
    public required string[] EvidenceIds { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
