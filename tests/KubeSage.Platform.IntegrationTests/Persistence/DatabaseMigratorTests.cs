using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KubeSage.Platform.IntegrationTests.Persistence;

// The migrator is the only thing standing between a container restart and a
// corrupted or half-created schema, so its safety properties are verified
// against a real PostgreSQL rather than assumed.
[Collection(PostgresCollection.Name)]
public sealed class DatabaseMigratorTests
{
    private readonly PostgresFixture _postgres;

    public DatabaseMigratorTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Migrating_creates_the_schema_and_enables_pgvector()
    {
        // Arrange
        await using var database = await TestDatabase.CreateAsync(_postgres);
        var migrator = CreateMigrator(database.ConnectionString);

        // Act
        await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        // Assert
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var pgvectorVersion = await ScalarAsync<string>(
            connection, "SELECT extversion FROM pg_extension WHERE extname = 'vector'");
        pgvectorVersion.ShouldNotBeNullOrEmpty();

        var recorded = await ScalarAsync<long>(
            connection, "SELECT count(*) FROM kubesage_schema_history");
        recorded.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Migrating_twice_is_safe()
    {
        // A restart, a crash loop or two replicas starting together all cause
        // this. It must be a no-op the second time, not an error and not a
        // duplicate application of the script.
        await using var database = await TestDatabase.CreateAsync(_postgres);
        var migrator = CreateMigrator(database.ConnectionString);

        await migrator.MigrateAsync(TestContext.Current.CancellationToken);
        await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var applied = await ScalarAsync<long>(
            connection, "SELECT count(*) FROM kubesage_schema_history WHERE migration_name = '001_baseline.sql'");

        applied.ShouldBe(1, "a migration that has already run must not be recorded or applied twice");
    }

    [Fact]
    public async Task An_edited_migration_is_refused()
    {
        // Editing a script that has already run would leave two databases with
        // different shapes while both claim to be up to date. The migrator must
        // refuse loudly instead of continuing.
        await using var database = await TestDatabase.CreateAsync(_postgres);
        var migrator = CreateMigrator(database.ConnectionString);

        await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        // Simulate the edit by corrupting the recorded checksum.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "UPDATE kubesage_schema_history SET checksum = 'tampered' WHERE migration_name = '001_baseline.sql'",
                connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var act = async () => await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        var exception = await act.ShouldThrowAsync<InvalidOperationException>();
        exception.Message.ShouldContain("has changed since it was applied");
    }

    private static DatabaseMigrator CreateMigrator(string connectionString)
    {
        var options = Options.Create(new KubeSageOptions
        {
            Database = new DatabaseOptions { ConnectionString = connectionString }
        });

        return new DatabaseMigrator(options, NullLogger<DatabaseMigrator>.Instance);
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default : (T)value;
    }
}
