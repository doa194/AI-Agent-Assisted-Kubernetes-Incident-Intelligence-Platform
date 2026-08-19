using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KubeSage.Platform.Modules.Persistence;

// Applies the SQL schema scripts embedded in this assembly.
//
// Why a hand-written migrator instead of a migration library: the platform
// only ever moves forward, the scripts are plain SQL, and this keeps the
// schema story completely transparent - you can read every statement that
// will run against your database without learning a tool.
//
// Safety properties this provides:
//  * ordering       - scripts run sorted by their numeric prefix
//  * idempotency    - a script that has already been applied is skipped
//  * drift detection- a checksum catches a script that was edited after
//                     being applied, which would leave databases in
//                     different shapes without anyone noticing
//  * single runner  - a PostgreSQL advisory lock stops two starting
//                     instances from applying the same script at once
internal sealed class DatabaseMigrator
{
    // Arbitrary but fixed identifier for the advisory lock. Any process using
    // the same number cooperates on the same lock.
    private const long MigrationLockId = 5_318_008_001;

    private const string ResourcePrefix = "KubeSage.Platform.Modules.Persistence.Migrations.";

    private readonly DatabaseOptions _options;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(IOptions<KubeSageOptions> options, ILogger<DatabaseMigrator> logger)
    {
        _options = options.Value.Database;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var scripts = LoadEmbeddedScripts();

        if (scripts.Count == 0)
        {
            _logger.LogWarning("No migration scripts were found in the assembly; the schema will not be created.");
            return;
        }

        await using var connection = new NpgsqlConnection(_options.EffectiveMigrationConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Block until this instance owns the lock. The lock is released when
        // the connection closes, including if this process crashes.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@id)", connection))
        {
            lockCommand.Parameters.AddWithValue("id", MigrationLockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await EnsureHistoryTableAsync(connection, cancellationToken);
            var applied = await LoadAppliedAsync(connection, cancellationToken);

            foreach (var script in scripts)
            {
                if (applied.TryGetValue(script.Name, out var recordedChecksum))
                {
                    if (!string.Equals(recordedChecksum, script.Checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration '{script.Name}' has changed since it was applied to this database " +
                            $"(recorded checksum {recordedChecksum}, current {script.Checksum}). " +
                            "Applied migrations must never be edited - add a new migration instead.");
                    }

                    continue;
                }

                await ApplyAsync(connection, script, cancellationToken);
            }
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", connection);
            unlockCommand.Parameters.AddWithValue("id", MigrationLockId);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task ApplyAsync(NpgsqlConnection connection, MigrationScript script, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Applying database migration {Migration}", script.Name);

        // Each script runs in its own transaction, so a failure halfway
        // through leaves the database in the state it had before that script.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var command = new NpgsqlCommand(script.Sql, connection, transaction))
            {
                command.CommandTimeout = 300;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = new NpgsqlCommand(
                             """
                             INSERT INTO kubesage_schema_history (migration_name, checksum, applied_at_utc)
                             VALUES (@name, @checksum, now() AT TIME ZONE 'utc')
                             """, connection, transaction))
            {
                record.Parameters.AddWithValue("name", script.Name);
                record.Parameters.AddWithValue("checksum", script.Checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureHistoryTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            CREATE TABLE IF NOT EXISTS kubesage_schema_history (
                migration_name  text        PRIMARY KEY,
                checksum        text        NOT NULL,
                applied_at_utc  timestamptz NOT NULL
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> LoadAppliedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(
            "SELECT migration_name, checksum FROM kubesage_schema_history", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            applied[reader.GetString(0)] = reader.GetString(1);
        }

        return applied;
    }

    // Scripts are embedded resources named like
    // "KubeSage.Platform.Modules.Persistence.Migrations.001_baseline.sql".
    // Sorting by resource name gives numeric ordering because every script
    // uses a zero-padded three digit prefix.
    private static List<MigrationScript> LoadEmbeddedScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(resourceName =>
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)
                                   ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' could not be opened.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var sql = reader.ReadToEnd();

                return new MigrationScript(
                    Name: resourceName[ResourcePrefix.Length..],
                    Sql: sql,
                    Checksum: Checksum(sql));
            })
            .ToList();
    }

    // Line endings are normalised before hashing so that a Windows checkout
    // and a Linux checkout of the same script produce the same checksum.
    private static string Checksum(string sql)
    {
        var normalised = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash);
    }

    private sealed record MigrationScript(string Name, string Sql, string Checksum);
}
