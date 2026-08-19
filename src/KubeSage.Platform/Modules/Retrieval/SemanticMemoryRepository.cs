using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using Pgvector;

namespace KubeSage.Platform.Modules.Retrieval;

// Reads and writes semantic incident memory.
//
// Retrieval here is deliberately narrow-then-similar rather than
// similar-then-filter: SQL predicates on workload, category and kind are
// applied first, and vector distance only decides the ordering within what
// survives. On a small corpus that matters a great deal - pure similarity will
// happily return the single most "textually alike" memory even when it comes
// from an unrelated service, and an agent shown an unrelated past incident
// tends to reason toward it.
public sealed class SemanticMemoryRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SemanticMemoryRepository> _logger;

    public SemanticMemoryRepository(NpgsqlDataSource dataSource, ILogger<SemanticMemoryRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    // Inserts or updates one memory.
    //
    // Keyed on (kind, source_ref) so re-indexing the same incident or runbook
    // section replaces it. Without that, every restart would add another copy
    // of every runbook, and those copies would then compete with each other
    // for the top-K slots in every search.
    public async Task UpsertAsync(MemoryRecord record, float[] embedding, CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO semantic_memory (
                id, kind, incident_id, source_ref, title, content, content_hash,
                workload, category, root_cause_category, severity, embedding,
                occurred_at_utc, created_at_utc, updated_at_utc)
            VALUES (
                @id, @kind, @incidentId, @sourceRef, @title, @content, @contentHash,
                @workload, @category, @rootCauseCategory, @severity, @embedding,
                @occurredAt, now() AT TIME ZONE 'utc', now() AT TIME ZONE 'utc')
            ON CONFLICT (kind, source_ref) DO UPDATE SET
                title               = EXCLUDED.title,
                content             = EXCLUDED.content,
                content_hash        = EXCLUDED.content_hash,
                workload            = EXCLUDED.workload,
                category            = EXCLUDED.category,
                root_cause_category = EXCLUDED.root_cause_category,
                severity            = EXCLUDED.severity,
                embedding           = EXCLUDED.embedding,
                occurred_at_utc     = EXCLUDED.occurred_at_utc,
                updated_at_utc      = now() AT TIME ZONE 'utc'
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("kind", record.Kind);
        command.Parameters.AddWithValue("incidentId", (object?)record.IncidentId ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceRef", record.SourceRef);
        command.Parameters.AddWithValue("title", record.Title);
        command.Parameters.AddWithValue("content", record.Content);
        command.Parameters.AddWithValue("contentHash", HashContent(record.Content));
        command.Parameters.AddWithValue("workload", (object?)record.Workload ?? DBNull.Value);
        command.Parameters.AddWithValue("category", (object?)record.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("rootCauseCategory", (object?)record.RootCauseCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("severity", (object?)record.Severity ?? DBNull.Value);
        command.Parameters.AddWithValue("occurredAt",
            (object?)record.OccurredAtUtc?.UtcDateTime ?? DBNull.Value);

        // Npgsql cannot infer the PostgreSQL type of a Vector, so it has to be
        // stated. Without this the write fails with an InvalidCastException
        // that never mentions vectors.
        command.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "embedding",
            DataTypeName = "vector",
            Value = new Vector(embedding)
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // True when this source is already indexed with identical content.
    //
    // Used by the runbook indexer so start-up only embeds what actually
    // changed. Embedding is the slow part, and runbooks rarely change.
    public async Task<bool> IsCurrentAsync(string kind, string sourceRef, string content, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT content_hash FROM semantic_memory WHERE kind = @kind AND source_ref = @sourceRef",
            new { kind, sourceRef }, cancellationToken: cancellationToken));

        return stored is not null && string.Equals(stored, HashContent(content), StringComparison.Ordinal);
    }

    // Finds memories similar to a query vector.
    //
    // maxDistance is applied as a cut-off rather than always returning topK.
    // Returning a weak match is worse than returning nothing: an agent given
    // an unrelated past incident will try to make it fit.
    public async Task<IReadOnlyList<MemoryMatch>> SearchAsync(
        float[] queryEmbedding,
        MemorySearchFilter filter,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            conditions.Add("kind = @kind");
        }

        if (!string.IsNullOrWhiteSpace(filter.Workload))
        {
            conditions.Add("(workload IS NULL OR workload = @workload)");
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            conditions.Add("(category IS NULL OR category = @category)");
        }

        // An incident must never retrieve itself as a similar past incident.
        if (filter.ExcludeIncidentId is not null)
        {
            conditions.Add("(incident_id IS NULL OR incident_id <> @excludeIncidentId)");
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        // <=> is pgvector's cosine distance: 0 is identical, 2 is opposite.
        var sql =
            $"""
             SELECT id, kind, incident_id AS IncidentId, source_ref AS SourceRef, title, content,
                    workload, category, root_cause_category AS RootCauseCategory, severity,
                    occurred_at_utc AS OccurredAtUtc,
                    embedding <=> @query AS Distance
             FROM semantic_memory
             {where}
             ORDER BY embedding <=> @query
             LIMIT @topK
             """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "query",
            DataTypeName = "vector",
            Value = new Vector(queryEmbedding)
        });

        command.Parameters.AddWithValue("topK", filter.TopK);

        if (!string.IsNullOrWhiteSpace(filter.Kind)) command.Parameters.AddWithValue("kind", filter.Kind);
        if (!string.IsNullOrWhiteSpace(filter.Workload)) command.Parameters.AddWithValue("workload", filter.Workload);
        if (!string.IsNullOrWhiteSpace(filter.Category)) command.Parameters.AddWithValue("category", filter.Category);
        if (filter.ExcludeIncidentId is not null)
            command.Parameters.AddWithValue("excludeIncidentId", filter.ExcludeIncidentId.Value);

        var matches = new List<MemoryMatch>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var distance = reader.GetDouble(reader.GetOrdinal("Distance"));

            if (distance > filter.MaxDistance)
            {
                // Ordered by distance, so everything after this is worse.
                break;
            }

            matches.Add(new MemoryMatch
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                IncidentId = reader.IsDBNull(reader.GetOrdinal("IncidentId"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("IncidentId")),
                SourceRef = reader.GetString(reader.GetOrdinal("SourceRef")),
                Title = reader.GetString(reader.GetOrdinal("title")),
                Content = reader.GetString(reader.GetOrdinal("content")),
                Workload = GetNullableString(reader, "workload"),
                Category = GetNullableString(reader, "category"),
                RootCauseCategory = GetNullableString(reader, "RootCauseCategory"),
                Severity = GetNullableString(reader, "severity"),
                OccurredAtUtc = reader.IsDBNull(reader.GetOrdinal("OccurredAtUtc"))
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(
                        reader.GetDateTime(reader.GetOrdinal("OccurredAtUtc")), DateTimeKind.Utc)),
                Distance = distance
            });
        }

        _logger.LogDebug(
            "Semantic search returned {MatchCount} match(es) within distance {MaxDistance}",
            matches.Count, filter.MaxDistance);

        return matches;
    }

    public async Task<int> CountAsync(string? kind, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            kind is null
                ? "SELECT count(*)::int FROM semantic_memory"
                : "SELECT count(*)::int FROM semantic_memory WHERE kind = @kind",
            new { kind }, cancellationToken: cancellationToken));
    }

    private static string? GetNullableString(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    // Line endings are normalised so the same runbook checked out on Windows
    // and Linux does not look like two different documents.
    private static string HashContent(string content)
    {
        var normalised = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }
}

public static class MemoryKind
{
    public const string Incident = "incident";
    public const string Runbook = "runbook";
}

public sealed record MemoryRecord
{
    public required string Kind { get; init; }
    public required string SourceRef { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public Guid? IncidentId { get; init; }
    public string? Workload { get; init; }
    public string? Category { get; init; }
    public string? RootCauseCategory { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
}

public sealed record MemorySearchFilter
{
    public string? Kind { get; init; }
    public string? Workload { get; init; }
    public string? Category { get; init; }
    public Guid? ExcludeIncidentId { get; init; }
    public int TopK { get; init; } = 5;
    public double MaxDistance { get; init; } = 0.65;
}

public sealed record MemoryMatch
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public Guid? IncidentId { get; init; }
    public required string SourceRef { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string? Workload { get; init; }
    public string? Category { get; init; }
    public string? RootCauseCategory { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }

    // Cosine distance: 0 identical, 1 unrelated, 2 opposite.
    public required double Distance { get; init; }

    // How well the text matched. Deliberately NOT the same thing as how
    // confident an investigation should be in a root cause: a strongly
    // matching past incident can still be the wrong explanation for this one,
    // and the two numbers are reported separately so they cannot be conflated.
    public double RetrievalConfidence => Math.Round(Math.Clamp(1.0 - Distance, 0.0, 1.0), 3);

    public string DistanceLabel => Distance.ToString("F3", CultureInfo.InvariantCulture);
}
