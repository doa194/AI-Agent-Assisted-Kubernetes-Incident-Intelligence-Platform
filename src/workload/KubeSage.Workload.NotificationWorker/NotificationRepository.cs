using Dapper;
using Npgsql;

namespace KubeSage.Workload.NotificationWorker;

// Database access for the notification queue.
public sealed class NotificationRepository
{
    private readonly string _connectionString;

    public NotificationRepository(string connectionString) => _connectionString = connectionString;

    // Claims a batch of pending notifications.
    //
    // FOR UPDATE SKIP LOCKED lets several replicas of the worker share the
    // queue safely: each one takes rows nobody else has locked instead of
    // waiting behind them.
    public async Task<IReadOnlyList<PendingNotification>> ClaimPendingAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var claimed = await connection.QueryAsync<PendingNotification>(
            new CommandDefinition(
                """
                UPDATE notifications
                SET status = 'processing', claimed_at_utc = now() AT TIME ZONE 'utc'
                WHERE id IN (
                    SELECT id FROM notifications
                    WHERE status = 'pending'
                    ORDER BY created_at_utc
                    LIMIT @batchSize
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id AS Id, order_id AS OrderId, channel AS Channel
                """,
                new { batchSize },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return claimed.AsList();
    }

    public async Task MarkDeliveredAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE notifications
            SET status = 'delivered', delivered_at_utc = now() AT TIME ZONE 'utc'
            WHERE id = @id
            """,
            new { id },
            cancellationToken: cancellationToken));
    }

    public async Task<long> CountPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM notifications WHERE status = 'pending'",
            cancellationToken: cancellationToken));
    }
}

public sealed record PendingNotification(long Id, string OrderId, string Channel);
