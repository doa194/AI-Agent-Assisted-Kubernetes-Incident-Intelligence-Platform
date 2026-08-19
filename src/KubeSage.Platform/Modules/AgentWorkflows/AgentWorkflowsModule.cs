using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Reporting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Wiring for the AI layer.
internal static class AgentWorkflowsModule
{
    public static IServiceCollection AddAgentWorkflows(this IServiceCollection services)
    {
        // A generous timeout, not a responsiveness target. A 12B model on this
        // hardware legitimately takes minutes for one structured answer, and
        // cutting it off early would turn a slow success into a failure.
        services.AddHttpClient<OllamaChatClientAdapter>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value.Ollama;
            client.BaseAddress = options.Endpoint;
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        });

        // Registered by key so the agents receive exactly this client. The
        // function-invoking wrapper is what actually executes tool calls the
        // model requests and feeds the results back into the conversation.
        services.AddKeyedSingleton<IChatClient>(IncidentAgents.ChatClientKey, (provider, _) =>
            new ChatClientBuilder(provider.GetRequiredService<OllamaChatClientAdapter>())
                .UseFunctionInvocation(provider.GetRequiredService<ILoggerFactory>())
                .Build(provider));

        services.AddSingleton<IncidentAgents>();
        services.AddSingleton<AgentOutputValidator>();
        services.AddSingleton<InvestigationTools>();
        services.AddSingleton<InvestigationToolFactory>();
        services.AddSingleton<ReportRepository>();
        services.AddSingleton<InvestigationWorkflow>();
        services.AddSingleton<ClusterAnalysis>();

        services.AddHostedService<StartupRecoveryService>();
        services.AddHostedService<InvestigationDispatcher>();

        return services;
    }
}

// Reports whether the model server is reachable.
//
// Not tagged "ready" on purpose. With Ollama down the platform still detects
// incidents, still stores them, and still serves everything already known - it
// simply queues investigations until the model returns. Taking the platform
// out of rotation would lose far more than it protects.
internal sealed class ModelHealthCheck : IHealthCheck
{
    private readonly OllamaChatClientAdapter _chatClient;

    public ModelHealthCheck(OllamaChatClientAdapter chatClient) => _chatClient = chatClient;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var available = await _chatClient.IsAvailableAsync(cancellationToken);

        return available
            ? HealthCheckResult.Healthy("Ollama is reachable.")
            : HealthCheckResult.Degraded(
                "Ollama is unreachable. Detection continues and investigations are queued until it returns.");
    }
}
