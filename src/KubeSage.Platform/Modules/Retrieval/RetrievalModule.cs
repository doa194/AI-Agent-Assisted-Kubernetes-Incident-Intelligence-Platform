using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Retrieval;

// Wiring for semantic incident memory.
internal static class RetrievalModule
{
    public static IServiceCollection AddRetrieval(this IServiceCollection services)
    {
        services.AddHttpClient<EmbeddingClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<KubeSageOptions>>().Value.Ollama;
            client.BaseAddress = options.Endpoint;
            // Generous, and it has to be.
            //
            // Ollama holds one model at a time here (see the compose file), and
            // serves one request at a time. An embed request issued while the
            // chat model is working therefore waits for that generation to
            // finish AND for a model swap - measured at ~58s to load the
            // embedding model cold, on top of a generation that can take 170s.
            //
            // A shorter timeout does not make anything faster, it just turns a
            // slow success into a spurious failure.
            client.Timeout = TimeSpan.FromSeconds(300);
        });

        services.AddSingleton<SemanticMemoryRepository>();
        services.AddSingleton<SemanticMemoryIndexer>();
        services.AddSingleton<MemoryRetriever>();

        services.AddHostedService<RunbookIndexingService>();

        return services;
    }
}

// Indexes the runbook corpus once, shortly after start-up.
//
// Runs in the background rather than blocking start-up: the platform should
// begin detecting incidents immediately, and retrieval becoming available a
// few seconds later costs nothing. Blocking would also mean an Ollama outage
// could prevent the platform starting at all, which is exactly the coupling
// the rest of the design avoids.
internal sealed class RunbookIndexingService : BackgroundService
{
    private readonly SemanticMemoryIndexer _indexer;
    private readonly EmbeddingClient _embeddings;
    private readonly SemanticMemoryRepository _memory;
    private readonly RetrievalOptions _options;
    private readonly ILogger<RunbookIndexingService> _logger;

    public RunbookIndexingService(
        SemanticMemoryIndexer indexer,
        EmbeddingClient embeddings,
        SemanticMemoryRepository memory,
        IOptions<KubeSageOptions> options,
        ILogger<RunbookIndexingService> logger)
    {
        _indexer = indexer;
        _embeddings = embeddings;
        _memory = memory;
        _options = options.Value.Retrieval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.IndexRunbooksOnStartup)
        {
            _logger.LogInformation("Runbook indexing is disabled by configuration");
            return;
        }

        // Wait for the embedding model to be reachable rather than failing
        // outright. On a cold start Ollama may still be loading.
        for (var attempt = 1; attempt <= 10 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (await _embeddings.IsAvailableAsync(stoppingToken))
            {
                break;
            }

            if (attempt == 10)
            {
                _logger.LogWarning(
                    "Embedding model never became available; runbooks are not indexed and " +
                    "investigations will run without historical context");
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        try
        {
            await _indexer.IndexRunbooksAsync(stoppingToken);

            var runbooks = await _memory.CountAsync(MemoryKind.Runbook, stoppingToken);
            var incidents = await _memory.CountAsync(MemoryKind.Incident, stoppingToken);

            _logger.LogInformation(
                "Semantic memory ready: {RunbookCount} runbook section(s), {IncidentCount} past incident(s)",
                runbooks, incidents);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runbook indexing failed; investigations will run without runbook guidance");
        }
    }
}
