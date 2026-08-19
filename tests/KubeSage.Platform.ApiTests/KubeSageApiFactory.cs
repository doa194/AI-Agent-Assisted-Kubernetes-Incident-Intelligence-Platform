using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace KubeSage.Platform.ApiTests;

// Starts the real platform in memory against a throwaway PostgreSQL.
//
// Two deliberate departures from production, both to make tests deterministic
// rather than to avoid testing something real:
//
//   * background loops are removed. A detection pass or an investigation
//     firing partway through a test would change the data being asserted on,
//     and the resulting flakiness would be blamed on the test rather than on
//     the timing. Those loops are covered by operational verification against
//     the running system instead.
//
//   * telemetry and model endpoints point at a port with nothing behind it.
//     That is not a limitation, it is the point: it lets these tests assert
//     how the API behaves when its dependencies are DOWN, which is one of the
//     behaviours the project explicitly requires.
public sealed class KubeSageApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18-trixie")
        .WithDatabase("kubesage_api")
        .WithUsername("kubesage_api")
        .WithPassword("kubesage_api")
        .WithEnvironment("PGDATA", "/var/lib/postgresql/18/docker")
        .Build();

    // A port deliberately left closed, so anything that tries to reach a
    // dependency fails fast and predictably.
    private const string UnreachableEndpoint = "http://127.0.0.1:59999";

    private string _kubeConfigPath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Kubernetes has to be pointed somewhere dead as well.
        //
        // Without an explicit kubeconfig the client falls back to the default
        // resolution order and picks up the DEVELOPER'S real cluster. The test
        // then reads live pods, and its result depends on whatever happens to
        // be running - which produced a genuine failure when a scenario check
        // was restarting pods in another terminal.
        _kubeConfigPath = WriteUnreachableKubeConfig();

        await _postgres.StartAsync();

        // Force the host to build now, so migrations run before the first test.
        _ = Services;
    }

    private static string WriteUnreachableKubeConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kubesage-apitests-{Guid.NewGuid():n}.yaml");

        File.WriteAllText(path,
            """
            apiVersion: v1
            kind: Config
            clusters:
              - name: unreachable
                cluster:
                  server: https://127.0.0.1:59999
                  insecure-skip-tls-verify: true
            contexts:
              - name: unreachable
                context:
                  cluster: unreachable
                  user: unreachable
                  namespace: kubesage-demo
            current-context: unreachable
            users:
              - name: unreachable
                user:
                  token: not-a-real-token
            """);

        return path;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();

        if (_kubeConfigPath.Length > 0 && File.Exists(_kubeConfigPath))
        {
            File.Delete(_kubeConfigPath);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("KubeSage:Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("KubeSage:Database:MigrationConnectionString", _postgres.GetConnectionString());

        builder.UseSetting("KubeSage:Telemetry:LokiEndpoint", UnreachableEndpoint);
        builder.UseSetting("KubeSage:Telemetry:PrometheusEndpoint", UnreachableEndpoint);
        builder.UseSetting("KubeSage:Ollama:Endpoint", UnreachableEndpoint);
        builder.UseSetting("KubeSage:Kubernetes:KubeConfigPath", _kubeConfigPath);

        // Detection stays enabled in configuration - the API exposes it - but
        // its background loop is removed below so it only runs when a test
        // asks for it explicitly.
        builder.UseSetting("KubeSage:Analysis:RunStartupAnalysis", "false");
        builder.UseSetting("KubeSage:Analysis:RunScheduledAnalysis", "false");
        builder.UseSetting("KubeSage:Retrieval:IndexRunbooksOnStartup", "false");

        builder.ConfigureServices(services =>
        {
            // Remove every background loop. Left in, they would race with the
            // assertions and make failures look random.
            services.RemoveAll<IHostedService>();
        });
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<KubeSageApiFactory>
{
    public const string Name = "kubesage-api";
}
