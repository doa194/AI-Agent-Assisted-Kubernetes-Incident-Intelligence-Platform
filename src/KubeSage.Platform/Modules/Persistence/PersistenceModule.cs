using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;

namespace KubeSage.Platform.Modules.Persistence;

// Wiring for everything that talks to the incident database.
//
// One shared NpgsqlDataSource is created for the whole process. That object
// owns the connection pool, so creating it once and handing out connections
// from it is what keeps connection use bounded.
internal static class PersistenceModule
{
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddSingleton<NpgsqlDataSource>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value.Database;

            var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);

            // Teaches Npgsql about the pgvector "vector" type so embeddings
            // can be read and written as ordinary parameters.
            builder.UseVector();

            builder.ConnectionStringBuilder.MaxPoolSize = options.MaxPoolSize;
            builder.ConnectionStringBuilder.CommandTimeout = options.CommandTimeoutSeconds;

            // Application name shows up in pg_stat_activity, which makes it
            // obvious which connections belong to the platform when
            // diagnosing database load.
            builder.ConnectionStringBuilder.ApplicationName = "kubesage-platform";

            builder.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());

            return builder.Build();
        });

        services.AddSingleton<DatabaseMigrator>();

        return services;
    }
}

// Reports whether the incident database is reachable and correctly set up.
//
// This is used by the readiness endpoint. The platform is not ready to work
// if it cannot record what it finds: detecting an incident and then failing
// to persist it is worse than not starting at all, because the evidence
// window will have passed by the time anyone notices.
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;

    public DatabaseHealthCheck(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Checks connectivity and that pgvector is actually installed in
            // this database, not just available on the server. A missing
            // extension only shows up much later, when the first embedding is
            // written, so it is worth catching here.
            await using var command = new NpgsqlCommand(
                "SELECT extversion FROM pg_extension WHERE extname = 'vector'", connection);

            var version = await command.ExecuteScalarAsync(cancellationToken) as string;

            if (string.IsNullOrEmpty(version))
            {
                return HealthCheckResult.Unhealthy(
                    "Connected to PostgreSQL but the pgvector extension is not installed in this database.");
            }

            return HealthCheckResult.Healthy($"PostgreSQL reachable, pgvector {version}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach the incident database.", ex);
        }
    }
}
