using System.Text.Json;
using Dapper;
using KubeSage.Platform.Modules.Telemetry;
using Npgsql;

namespace KubeSage.Platform.Modules.Incidents;

// Persistence for incidents and their evidence.
//
// The deduplication decision lives here rather than in the detector, because
// it needs to look at what is already stored. A detector stays a pure function
// of its snapshot; this class decides whether the candidate it produced is a
// new incident, another occurrence of an existing one, or a duplicate to
// suppress.
public sealed class IncidentRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<IncidentRepository> _logger;

    public IncidentRepository(NpgsqlDataSource dataSource, ILogger<IncidentRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    // Records a candidate, returning what happened to it.
    //
    // The three outcomes are genuinely different and the caller acts on each
    // differently: only Created starts an investigation.
    public async Task<CandidateOutcome> RecordCandidateAsync(
        IncidentCandidate candidate,
        TimeSpan deduplicationCooldown,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Lock the newest matching incident so two concurrent detection passes
        // cannot both decide they are creating the first one.
        const string findSql =
            """
            SELECT id, state, severity, last_detected_at_utc AS LastDetectedAtUtc, occurrence_count AS OccurrenceCount
            FROM incidents
            WHERE fingerprint = @fingerprint
            ORDER BY last_detected_at_utc DESC
            LIMIT 1
            FOR UPDATE
            """;

        var existing = await connection.QuerySingleOrDefaultAsync<ExistingIncident>(
            new CommandDefinition(findSql, new { candidate.Fingerprint }, transaction,
                cancellationToken: cancellationToken));

        if (existing is not null)
        {
            var state = Enum.Parse<IncidentState>(existing.State);
            var age = candidate.DetectedAtUtc - Utc(existing.LastDetectedAtUtc);

            // Still open, or closed very recently: this is the same condition
            // continuing, so the existing incident is updated instead of a new
            // one being raised.
            var withinCooldown = age <= deduplicationCooldown;

            if (!IncidentStateMachine.IsTerminal(state) || withinCooldown)
            {
                const string touchSql =
                    """
                    UPDATE incidents
                    SET last_detected_at_utc = @detectedAt,
                        occurrence_count     = occurrence_count + 1,
                        updated_at_utc       = @detectedAt,
                        -- Severity may rise while a condition persists, but is
                        -- never lowered: a problem that got worse should not
                        -- look calmer because a later pass measured a dip.
                        severity = CASE WHEN @severityRank > @existingRank THEN @severity ELSE severity END
                    WHERE id = @id
                    """;

                await connection.ExecuteAsync(new CommandDefinition(touchSql, new
                {
                    id = existing.Id,
                    detectedAt = candidate.DetectedAtUtc.UtcDateTime,
                    severity = candidate.Severity.ToString(),
                    severityRank = (int)candidate.Severity,
                    existingRank = (int)Enum.Parse<IncidentSeverity>(existing.Severity)
                }, transaction, cancellationToken: cancellationToken));

                await transaction.CommitAsync(cancellationToken);

                _logger.LogDebug(
                    "Incident {IncidentId} ({Category}) observed again; occurrence {Count}",
                    existing.Id, candidate.Category, existing.OccurrenceCount + 1);

                return new CandidateOutcome(CandidateDisposition.Deduplicated, existing.Id);
            }
        }

        // New condition, or the same one recurring after the cooldown expired.
        var incidentId = Guid.CreateVersion7();

        const string insertSql =
            """
            INSERT INTO incidents (
                id, fingerprint, state, severity, category, title, detection_rule, namespace,
                affected_workloads, signals, first_detected_at_utc, last_detected_at_utc,
                occurrence_count, updated_at_utc)
            VALUES (
                @id, @fingerprint, 'Candidate', @severity, @category, @title, @detectionRule, @namespace,
                @affectedWorkloads, @signals::jsonb, @detectedAt, @detectedAt,
                1, @detectedAt)
            """;

        await connection.ExecuteAsync(new CommandDefinition(insertSql, new
        {
            id = incidentId,
            candidate.Fingerprint,
            severity = candidate.Severity.ToString(),
            candidate.Category,
            candidate.Title,
            detectionRule = candidate.DetectionRule,
            @namespace = candidate.Namespace,
            affectedWorkloads = candidate.AffectedWorkloads.ToArray(),
            signals = JsonSerializer.Serialize(candidate.Signals),
            detectedAt = candidate.DetectedAtUtc.UtcDateTime
        }, transaction, cancellationToken: cancellationToken));

        await SaveEvidenceAsync(connection, transaction, incidentId, candidate.Evidence, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Incident {IncidentId} created: {Title} (category={Category} severity={Severity} rule={Rule})",
            incidentId, candidate.Title, candidate.Category, candidate.Severity, candidate.DetectionRule);

        return new CandidateOutcome(CandidateDisposition.Created, incidentId);
    }

    // Stores evidence against an incident.
    //
    // ON CONFLICT DO NOTHING because evidence identifiers are deterministic:
    // collecting the same observation twice must not create a second row that
    // an agent could then cite as independent corroboration.
    public async Task SaveEvidenceAsync(
        Guid incidentId,
        IEnumerable<Evidence> evidence,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await SaveEvidenceAsync(connection, null, incidentId, evidence, cancellationToken);
    }

    private static async Task SaveEvidenceAsync(
        NpgsqlConnection connection,
        System.Data.Common.DbTransaction? transaction,
        Guid incidentId,
        IEnumerable<Evidence> evidence,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO incident_evidence (
                id, incident_id, kind, source, observed_at_utc, workload, namespace,
                summary, attributes, query, redacted_count)
            VALUES (
                @id, @incidentId, @kind, @source, @observedAt, @workload, @namespace,
                @summary, @attributes::jsonb, @query, @redactedCount)
            ON CONFLICT (id) DO NOTHING
            """;

        foreach (var item in evidence)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                id = item.Id,
                incidentId,
                kind = item.Kind.ToString(),
                item.Source,
                observedAt = item.ObservedAtUtc.UtcDateTime,
                item.Workload,
                @namespace = item.Namespace,
                item.Summary,
                attributes = JsonSerializer.Serialize(item.Attributes),
                item.Query,
                redactedCount = item.RedactedValueCount
            }, transaction, cancellationToken: cancellationToken));
        }
    }

    // Moves an incident to a new state, refusing transitions the state machine
    // does not allow. The check happens inside the transaction that performs
    // the write, so a concurrent update cannot slip past it.
    public async Task<bool> TransitionAsync(
        Guid incidentId,
        IncidentState target,
        string? outcome,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var currentRaw = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT state FROM incidents WHERE id = @incidentId FOR UPDATE",
            new { incidentId }, transaction, cancellationToken: cancellationToken));

        if (currentRaw is null)
        {
            return false;
        }

        var current = Enum.Parse<IncidentState>(currentRaw);

        if (current == target)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        IncidentStateMachine.EnsureTransition(current, target);

        const string sql =
            """
            UPDATE incidents
            SET state            = @state,
                outcome          = COALESCE(@outcome, outcome),
                recovered_at_utc = CASE WHEN @state = 'Recovered'
                                        THEN now() AT TIME ZONE 'utc' ELSE recovered_at_utc END,
                updated_at_utc   = now() AT TIME ZONE 'utc'
            WHERE id = @incidentId
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            incidentId,
            state = target.ToString(),
            outcome
        }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Incident {IncidentId} moved {From} -> {To}{Outcome}",
            incidentId, current, target, outcome is null ? "" : $" ({outcome})");

        return true;
    }

    public async Task<Incident?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<IncidentRow>(new CommandDefinition(
            $"{IncidentSelect} WHERE id = @id", new { id }, cancellationToken: cancellationToken));

        return row?.ToIncident();
    }

    public async Task<IReadOnlyList<Incident>> ListAsync(
        IncidentState? state,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = state is null
            ? $"{IncidentSelect} ORDER BY first_detected_at_utc DESC LIMIT @limit"
            : $"{IncidentSelect} WHERE state = @state ORDER BY first_detected_at_utc DESC LIMIT @limit";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<IncidentRow>(new CommandDefinition(
            sql, new { state = state?.ToString(), limit }, cancellationToken: cancellationToken));

        return rows.Select(row => row.ToIncident()).ToList();
    }

    // Incidents whose processing was interrupted. Used at start-up to put work
    // back on the queue, which is what makes a mid-investigation crash
    // recoverable rather than a silent loss.
    public async Task<IReadOnlyList<Incident>> ListInFlightAsync(CancellationToken cancellationToken)
    {
        const string states = "('Candidate', 'Triaging', 'Investigating', 'Failed')";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<IncidentRow>(new CommandDefinition(
            $"{IncidentSelect} WHERE state IN {states} ORDER BY first_detected_at_utc",
            cancellationToken: cancellationToken));

        return rows.Select(row => row.ToIncident()).ToList();
    }

    public async Task<IReadOnlyList<Evidence>> GetEvidenceAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT id, kind, source, observed_at_utc AS ObservedAtUtc, workload, namespace,
                   summary, attributes::text AS Attributes, query, redacted_count AS RedactedCount
            FROM incident_evidence
            WHERE incident_id = @incidentId
            ORDER BY observed_at_utc
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EvidenceRow>(new CommandDefinition(
            sql, new { incidentId }, cancellationToken: cancellationToken));

        return rows.Select(row => row.ToEvidence()).ToList();
    }

    // Open incidents whose condition has not been observed for long enough to
    // consider it resolved.
    public async Task<IReadOnlyList<Incident>> ListRecoveredCandidatesAsync(
        TimeSpan confirmationWindow,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT id, fingerprint, state, severity, category, title, detection_rule AS DetectionRule,
                   namespace, affected_workloads AS AffectedWorkloads, signals::text AS Signals,
                   first_detected_at_utc AS FirstDetectedAtUtc, last_detected_at_utc AS LastDetectedAtUtc,
                   recovered_at_utc AS RecoveredAtUtc, occurrence_count AS OccurrenceCount,
                   outcome, updated_at_utc AS UpdatedAtUtc
            FROM incidents
            WHERE state IN ('Candidate', 'Triaging', 'Investigating', 'Reported', 'Inconclusive', 'Failed')
              AND last_detected_at_utc < @cutoff
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<IncidentRow>(new CommandDefinition(sql, new
        {
            cutoff = DateTimeOffset.UtcNow.Subtract(confirmationWindow).UtcDateTime
        }, cancellationToken: cancellationToken));

        return rows.Select(row => row.ToIncident()).ToList();
    }

    private const string IncidentSelect =
        """
        SELECT id, fingerprint, state, severity, category, title, detection_rule AS DetectionRule,
               namespace, affected_workloads AS AffectedWorkloads, signals::text AS Signals,
               first_detected_at_utc AS FirstDetectedAtUtc, last_detected_at_utc AS LastDetectedAtUtc,
               recovered_at_utc AS RecoveredAtUtc, occurrence_count AS OccurrenceCount,
               outcome, updated_at_utc AS UpdatedAtUtc
        FROM incidents
        """;

    // The row types below are plain mutable classes with a parameterless
    // constructor, not positional records.
    //
    // Dapper picks a constructor by matching the reader's column types, and
    // Npgsql hands back DateTime (with Kind=Utc) for a timestamptz column, not
    // DateTimeOffset. A positional record therefore fails to materialise at
    // run time with an error that names every column and explains none of
    // them. Mapping to DateTime here and converting once in the To... method
    // keeps the conversion explicit and the failure impossible.
    private sealed class ExistingIncident
    {
        public Guid Id { get; init; }
        public string State { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public DateTime LastDetectedAtUtc { get; init; }
        public int OccurrenceCount { get; init; }
    }

    private sealed class IncidentRow
    {
        public Guid Id { get; init; }
        public string Fingerprint { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string DetectionRule { get; init; } = string.Empty;
        public string Namespace { get; init; } = string.Empty;
        public string[] AffectedWorkloads { get; init; } = [];
        public string Signals { get; init; } = "{}";
        public DateTime FirstDetectedAtUtc { get; init; }
        public DateTime LastDetectedAtUtc { get; init; }
        public DateTime? RecoveredAtUtc { get; init; }
        public int OccurrenceCount { get; init; }
        public string? Outcome { get; init; }
        public DateTime UpdatedAtUtc { get; init; }

        public Incident ToIncident() => new()
        {
            Id = Id,
            Fingerprint = Fingerprint,
            State = Enum.Parse<IncidentState>(State),
            Severity = Enum.Parse<IncidentSeverity>(Severity),
            Category = Category,
            Title = Title,
            DetectionRule = DetectionRule,
            Namespace = Namespace,
            AffectedWorkloads = AffectedWorkloads,
            Signals = JsonSerializer.Deserialize<Dictionary<string, string>>(Signals) ?? [],
            FirstDetectedAtUtc = Utc(FirstDetectedAtUtc),
            LastDetectedAtUtc = Utc(LastDetectedAtUtc),
            RecoveredAtUtc = RecoveredAtUtc is null ? null : Utc(RecoveredAtUtc.Value),
            OccurrenceCount = OccurrenceCount,
            Outcome = Outcome,
            UpdatedAtUtc = Utc(UpdatedAtUtc)
        };
    }

    private sealed class EvidenceRow
    {
        public string Id { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public DateTime ObservedAtUtc { get; init; }
        public string? Workload { get; init; }
        public string? Namespace { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string Attributes { get; init; } = "{}";
        public string? Query { get; init; }
        public int RedactedCount { get; init; }

        public Evidence ToEvidence() => new()
        {
            Id = Id,
            Kind = Enum.Parse<EvidenceKind>(Kind),
            Source = Source,
            ObservedAtUtc = Utc(ObservedAtUtc),
            Workload = Workload,
            Namespace = Namespace,
            Summary = Summary,
            Attributes = JsonSerializer.Deserialize<Dictionary<string, string>>(Attributes) ?? [],
            Query = Query,
            RedactedValueCount = RedactedCount
        };
    }

    // Every timestamp column in this schema is timestamptz and every value is
    // stored in UTC, so the offset is always zero.
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public enum CandidateDisposition
{
    // A new incident was raised. Only this outcome schedules an investigation.
    Created,

    // The same ongoing condition; the existing incident was updated.
    Deduplicated
}

public sealed record CandidateOutcome(CandidateDisposition Disposition, Guid IncidentId);
