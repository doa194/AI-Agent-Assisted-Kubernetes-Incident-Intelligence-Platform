using Dapper;
using Npgsql;

namespace KubeSage.Workload.OrderApi;

// Database access for orders.
//
// Creating an order also queues a notification row in the SAME transaction.
// That is deliberate: it means the notification worker has real work to do
// that is genuinely tied to order traffic, so "the worker stopped making
// progress" becomes a meaningful signal rather than an artificial one.
public sealed class OrderRepository
{
    private readonly string _connectionString;

    public OrderRepository(string connectionString) => _connectionString = connectionString;

    public async Task CreateOrderAsync(
        string orderId,
        string customerId,
        decimal amount,
        string currency,
        string authorisationId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO orders (order_id, customer_id, amount, currency, authorisation_id, status, created_at_utc)
            VALUES (@orderId, @customerId, @amount, @currency, @authorisationId, 'created', now() AT TIME ZONE 'utc')
            """,
            new { orderId, customerId, amount, currency, authorisationId },
            transaction);

        await connection.ExecuteAsync(
            """
            INSERT INTO notifications (order_id, channel, status, created_at_utc)
            VALUES (@orderId, 'email', 'pending', now() AT TIME ZONE 'utc')
            """,
            new { orderId },
            transaction);

        await transaction.CommitAsync();
    }

    public async Task<OrderRecord?> GetOrderAsync(string orderId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.QuerySingleOrDefaultAsync<OrderRecord>(
            """
            SELECT order_id AS OrderId, customer_id AS CustomerId, amount AS Amount,
                   currency AS Currency, status AS Status, created_at_utc AS CreatedAtUtc
            FROM orders
            WHERE order_id = @orderId
            """,
            new { orderId });
    }

    public async Task<IReadOnlyList<OrderRecord>> GetRecentOrdersAsync(int limit)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var rows = await connection.QueryAsync<OrderRecord>(
            """
            SELECT order_id AS OrderId, customer_id AS CustomerId, amount AS Amount,
                   currency AS Currency, status AS Status, created_at_utc AS CreatedAtUtc
            FROM orders
            ORDER BY created_at_utc DESC
            LIMIT @limit
            """,
            new { limit });

        return rows.AsList();
    }
}

public sealed record OrderRecord(
    string OrderId,
    string CustomerId,
    decimal Amount,
    string Currency,
    string Status,
    DateTime CreatedAtUtc);
