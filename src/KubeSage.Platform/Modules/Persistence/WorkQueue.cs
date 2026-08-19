using System.Text.Json;
using Dapper;
using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KubeSage.Platform.Modules.Persistence;

// The durable work queue that drives all autonomous processing.
//
// Everything the platform does without being asked - the startup report, the
// scheduled analysis, an investigation triggered by a detection rule - goes
// through here rather than being started directly. That indirection is what
// buys the properties the project needs:
//
//   durability   the work is a committed database row, so killing the process
//                mid-investigation loses nothing;
//   idempotency  a partial unique index on (kind, dedup_key) means the same
//                event raised twice cannot create two investigations;
//   retry        a failed item becomes available again after a backoff, up to
//                a limit, and then stops rather than looping forever;
//   backpressure claiming is bounded, so a slow local model is never asked to
//                serve more investigations than it can.
//
// The claim uses SELECT ... FOR UPDATE SKIP LOCKED, which is what lets several
// workers share the queue without blocking each other or handing the same row
// to two of them.
public sealed class WorkQueue
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly InvestigationOptions _options;
    private readonly ILogger<WorkQueue> _logger;

    // Identifies this process in the leased_by column. Useful when looking at
    // the table by hand to see which instance is holding what.
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public WorkQueue(
        NpgsqlDataSource dataSource,
        IOptions<KubeSageOptions> options,
        ILogger<WorkQueue> logger)
    {
        _dataSource = dataSource;
        _options = options.Value.Investigation;
        _logger = logger;
    }

    // Adds work if an equivalent item is not already waiting or in progress.
    // Returns the item id, or null when it was suppressed as a duplicate.
    public async Task<Guid?> EnqueueAsync(
        string kind,
        string dedupKey,
        object payload,
        CancellationToken cancellationToken,
        DateTimeOffset? availableAt = null)
    {
        const string sql =
            """
            INSERT INTO work_items (id, kind, dedup_key, payload, state, max_attempts, available_at_utc)
            VALUES (@id, @kind, @dedupKey, @payload::jsonb, 'Pending', @maxAttempts, @availableAt)
            ON CONFLICT DO NOTHING
            RETURNING id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var id = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql, new
        {
            id = Guid.CreateVersion7(),
            kind,
            dedupKey,
            payload = JsonSerializer.Serialize(payload),
            maxAttempts = _options.MaxRetries + 1,
            availableAt = (availableAt ?? DateTimeOffset.UtcNow).UtcDateTime
        }, cancellationToken: cancellationToken));

        if (id is null)
        {
            _logger.LogDebug(
                "Work item {Kind}/{DedupKey} already queued or running; not enqueued again", kind, dedupKey);
        }
        else
        {
            _logger.LogInformation("Queued {Kind} work item {WorkItemId} ({DedupKey})", kind, id, dedupKey);
        }

        return id;
    }

    // Takes ownership of up to `limit` items.
    //
    // A row is claimable when it is Pending and due, OR when it is Claimed but
    // its lease has expired - which is exactly the state left behind by a
    // process that died mid-investigation.
    public async Task<IReadOnlyList<WorkItem>> ClaimAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            WITH claimable AS (
                SELECT id
                FROM work_items
                WHERE (state = 'Pending' AND available_at_utc <= now() AT TIME ZONE 'utc')
                   OR (state = 'Claimed' AND leased_until_utc < now() AT TIME ZONE 'utc')
                ORDER BY available_at_utc
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE work_items w
            SET state            = 'Claimed',
                attempt          = w.attempt + 1,
                leased_until_utc = (now() AT TIME ZONE 'utc') + make_interval(secs => @leaseSeconds),
                leased_by        = @workerId,
                updated_at_utc   = now() AT TIME ZONE 'utc'
            FROM claimable c
            WHERE w.id = c.id
            RETURNING w.id, w.kind, w.dedup_key AS DedupKey, w.payload::text AS Payload,
                      w.attempt, w.max_attempts AS MaxAttempts
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<WorkItem>(new CommandDefinition(sql, new
        {
            limit,
            leaseSeconds = (double)_options.WorkLeaseSeconds,
            workerId = _workerId
        }, cancellationToken: cancellationToken));

        return rows.AsList();
    }

    public async Task CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE work_items
            SET state            = 'Completed',
                completed_at_utc = now() AT TIME ZONE 'utc',
                updated_at_utc   = now() AT TIME ZONE 'utc',
                leased_until_utc = NULL,
                leased_by        = NULL
            WHERE id = @id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
    }

    // Records a failure. The item is retried after an exponential backoff
    // until max_attempts is reached, after which it is left in Failed for an
    // operator to look at rather than retried forever.
    public async Task FailAsync(WorkItem item, string error, CancellationToken cancellationToken)
    {
        var exhausted = item.Attempt >= item.MaxAttempts;

        // 30s, 60s, 120s, ... capped so a long outage does not push the next
        // attempt hours into the future.
        var delaySeconds = Math.Min(
            _options.RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, item.Attempt - 1)),
            900);

        const string sql =
            """
            UPDATE work_items
            SET state            = @state,
                last_error       = @error,
                available_at_utc = @availableAt,
                leased_until_utc = NULL,
                leased_by        = NULL,
                updated_at_utc   = now() AT TIME ZONE 'utc',
                completed_at_utc = CASE WHEN @state = 'Failed' THEN now() AT TIME ZONE 'utc' ELSE NULL END
            WHERE id = @id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            id = item.Id,
            state = exhausted ? "Failed" : "Pending",
            // Truncated: an error message is for diagnosis, and a megabyte of
            // stack trace in a queue row helps nobody.
            error = error.Length > 2000 ? error[..2000] : error,
            availableAt = DateTimeOffset.UtcNow.AddSeconds(exhausted ? 0 : delaySeconds).UtcDateTime
        }, cancellationToken: cancellationToken));

        if (exhausted)
        {
            _logger.LogError(
                "Work item {WorkItemId} ({Kind}) failed permanently after {Attempts} attempts: {Error}",
                item.Id, item.Kind, item.Attempt, error);
        }
        else
        {
            _logger.LogWarning(
                "Work item {WorkItemId} ({Kind}) failed on attempt {Attempt}, retrying in {DelaySeconds}s: {Error}",
                item.Id, item.Kind, item.Attempt, delaySeconds, error);
        }
    }

    // Extends the lease on work that is still running.
    //
    // Needed because an investigation on a slow local model can legitimately
    // outlast its lease. Without this, a healthy long-running investigation
    // would be claimed a second time and produce a duplicate report.
    public async Task<bool> RenewLeaseAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE work_items
            SET leased_until_utc = (now() AT TIME ZONE 'utc') + make_interval(secs => @leaseSeconds),
                updated_at_utc   = now() AT TIME ZONE 'utc'
            WHERE id = @id AND state = 'Claimed' AND leased_by = @workerId
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            id,
            leaseSeconds = (double)_options.WorkLeaseSeconds,
            workerId = _workerId
        }, cancellationToken: cancellationToken));

        return affected > 0;
    }

    // Counts by state, for the status endpoint and for tests that need to
    // assert the queue drained.
    public async Task<IReadOnlyDictionary<string, int>> GetDepthAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT state, count(*)::int AS count FROM work_items GROUP BY state";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string State, int Count)>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));

        return rows.ToDictionary(row => row.State, row => row.Count, StringComparer.Ordinal);
    }

    // Releases leases held by THIS worker at start-up.
    //
    // After an unclean shutdown the rows this process was holding would
    // otherwise sit unavailable until their lease expired, delaying recovery
    // for no reason. Only this worker's own rows are touched, so a second
    // instance is unaffected.
    public async Task<int> ReleaseOwnStaleLeasesAsync(CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE work_items
            SET state            = 'Pending',
                leased_until_utc = NULL,
                leased_by        = NULL,
                updated_at_utc   = now() AT TIME ZONE 'utc'
            WHERE state = 'Claimed' AND leased_by = @workerId
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var released = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { workerId = _workerId }, cancellationToken: cancellationToken));

        if (released > 0)
        {
            _logger.LogInformation(
                "Released {Count} work item(s) left claimed by a previous run of this worker", released);
        }

        return released;
    }
}

public sealed record WorkItem
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string DedupKey { get; init; }
    public required string Payload { get; init; }
    public required int Attempt { get; init; }
    public required int MaxAttempts { get; init; }

    // Case-insensitive on purpose. Payloads are written from anonymous
    // objects, whose property names follow C# casing, while the consuming
    // records use their own. A case-sensitive read silently produced a
    // default-valued payload - the work item was then marked complete having
    // done nothing at all, which looked exactly like a healthy queue draining.
    public T? PayloadAs<T>() => JsonSerializer.Deserialize<T>(Payload, PayloadSerializerOptions);

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

// The kinds of work the platform schedules. Kept as constants rather than an
// enum because they are stored as text and must stay stable across upgrades.
public static class WorkKind
{
    public const string StartupAnalysis = "startup-analysis";
    public const string ScheduledAnalysis = "scheduled-analysis";
    public const string Investigation = "investigation";
}
