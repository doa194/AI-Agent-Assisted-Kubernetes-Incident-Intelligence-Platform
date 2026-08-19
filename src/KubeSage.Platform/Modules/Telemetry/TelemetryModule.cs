using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Kubernetes;
using k8s;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Telemetry;

// Wiring for everything that reads observed state: Loki, Prometheus and the
// Kubernetes API.
//
// All three are registered as ordinary typed clients with a timeout and a
// bounded retry. They are deliberately NOT registered as anything an agent
// can reach directly - agents call tools, and tools call these.
internal static class TelemetryModule
{
    public static IServiceCollection AddTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<SensitiveDataRedactor>();

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value;
            return new TelemetryQuery(options.Telemetry, options.Kubernetes);
        });

        services.AddHttpClient<LokiClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value.Telemetry;
                client.BaseAddress = options.LokiEndpoint;
                client.Timeout = TimeSpan.FromSeconds(options.QueryTimeoutSeconds);
            })
            .AddStandardResilienceHandler(ConfigureResilience);

        services.AddHttpClient<PrometheusClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value.Telemetry;
                client.BaseAddress = options.PrometheusEndpoint;
                client.Timeout = TimeSpan.FromSeconds(options.QueryTimeoutSeconds);
            })
            .AddStandardResilienceHandler(ConfigureResilience);

        services.AddSingleton<IKubernetes>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value.Kubernetes;
            var logger = provider.GetRequiredService<ILogger<KubernetesEvidenceClient>>();

            // A kubeconfig path is supplied when running in a container, where
            // the read-only service account credential is mounted. Without one
            // the client falls back to the normal resolution order, which is
            // what a developer running the platform on their own machine gets.
            var configuration = string.IsNullOrWhiteSpace(options.KubeConfigPath)
                ? KubernetesClientConfiguration.BuildDefaultConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile(options.KubeConfigPath);

            configuration.HttpClientTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

            logger.LogInformation(
                "Kubernetes client configured for {Host} using {Source}",
                configuration.Host,
                string.IsNullOrWhiteSpace(options.KubeConfigPath) ? "the default kubeconfig" : options.KubeConfigPath);

            return new k8s.Kubernetes(configuration);
        });

        services.AddSingleton<KubernetesEvidenceClient>();
        services.AddSingleton<EvidenceCollector>();

        return services;
    }

    // Retries are short and few on purpose. A telemetry system that is down
    // should be reported as down quickly so the investigation can record
    // reduced confidence, rather than spending an agent's whole time budget
    // retrying a request that will not succeed.
    private static void ConfigureResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(20);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(75);
    }
}

// Reports whether the observed systems can be reached.
//
// Registered WITHOUT the "ready" tag: the platform is still useful when a
// telemetry source is down. It can serve stored incidents and reports, and
// investigations correctly degrade to partial or inconclusive results. Taking
// the whole platform out of rotation because Loki restarted would lose more
// than it protects.
internal sealed class TelemetryHealthCheck : IHealthCheck
{
    private readonly LokiClient _loki;
    private readonly PrometheusClient _prometheus;
    private readonly KubernetesEvidenceClient _kubernetes;

    public TelemetryHealthCheck(
        LokiClient loki,
        PrometheusClient prometheus,
        KubernetesEvidenceClient kubernetes)
    {
        _loki = loki;
        _prometheus = prometheus;
        _kubernetes = kubernetes;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var lokiTask = _loki.IsAvailableAsync(cancellationToken);
        var prometheusTask = _prometheus.IsAvailableAsync(cancellationToken);
        var kubernetesTask = _kubernetes.IsAvailableAsync(cancellationToken);

        await Task.WhenAll(lokiTask, prometheusTask, kubernetesTask);

        var status = new Dictionary<string, object>
        {
            ["loki"] = lokiTask.Result,
            ["prometheus"] = prometheusTask.Result,
            ["kubernetes"] = kubernetesTask.Result
        };

        var unavailable = status.Where(kv => kv.Value is false).Select(kv => kv.Key).ToList();

        if (unavailable.Count == 0)
        {
            return HealthCheckResult.Healthy("All telemetry sources reachable.", status);
        }

        return HealthCheckResult.Degraded(
            $"Telemetry sources unavailable: {string.Join(", ", unavailable)}. " +
            "Detection and investigation will produce reduced-confidence results.",
            data: status);
    }
}
