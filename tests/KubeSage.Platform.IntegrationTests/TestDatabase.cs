using Npgsql;

namespace KubeSage.Platform.IntegrationTests;

// Creates a fresh, empty database inside the shared PostgreSQL container and
// drops it again afterwards.
//
// Why per-test databases rather than per-test containers: starting a container
// takes seconds, creating a database takes milliseconds, and a dedicated
// database gives the same isolation. Tests that apply migrations or take
// advisory locks genuinely need that isolation, because they would otherwise
// see each other's schema.
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;

    private TestDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        Name = databaseName;
        ConnectionString = connectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres)
    {
        var name = $"t_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync();
            // The database name is generated here, never supplied by a caller,
            // so interpolating it is safe. Identifiers cannot be parameterised.
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name };

        return new TestDatabase(postgres.ConnectionString, name, builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        // Pooled connections to the test database would block DROP DATABASE.
        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Clean-up failure must not turn a passing test red. The container
            // is thrown away at the end of the run regardless.
        }
    }
}
