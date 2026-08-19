using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Retrieval;

// Turns text into vectors using EmbeddingGemma.
//
// A separate, much smaller model from the reasoning one. That matters on this
// hardware: embedding a runbook corpus with the 12B model would take many
// minutes, whereas the 300M embedding model handles the whole corpus in
// seconds and stays resident alongside the chat model without memory trouble.
//
// The dimension is checked on every call. A silent mismatch between what the
// model returns and what the database column expects would only surface much
// later as an unhelpful insert error, so it is caught immediately with a
// message that says what to do about it.
public sealed class EmbeddingClient
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<EmbeddingClient> _logger;

    public EmbeddingClient(
        HttpClient http,
        IOptions<KubeSageOptions> options,
        ILogger<EmbeddingClient> logger)
    {
        _http = http;
        _options = options.Value.Ollama;
        _logger = logger;
    }

    public int Dimensions => _options.EmbeddingDimensions;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding model availability check failed");
            return false;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var results = await EmbedBatchAsync([text], cancellationToken);
        return results[0];
    }

    // Embeds several texts in one request.
    //
    // Batching matters for the runbook indexer: one request for twenty
    // sections is far faster than twenty requests, because the per-request
    // overhead dominates for a model this small.
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var request = new
        {
            model = _options.EmbeddingModel,
            input = texts
        };

        using var response = await _http.PostAsJsonAsync("/api/embed", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new EmbeddingUnavailableException(
                $"Ollama returned {(int)response.StatusCode} when embedding: " +
                $"{body[..Math.Min(body.Length, 300)]}");
        }

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken);

        if (payload?.Embeddings is null || payload.Embeddings.Count != texts.Count)
        {
            throw new EmbeddingUnavailableException(
                $"Expected {texts.Count} embedding(s) but received {payload?.Embeddings?.Count ?? 0}.");
        }

        foreach (var embedding in payload.Embeddings)
        {
            if (embedding.Length != _options.EmbeddingDimensions)
            {
                throw new EmbeddingUnavailableException(
                    $"The embedding model returned {embedding.Length} dimensions but the platform and " +
                    $"database are configured for {_options.EmbeddingDimensions}. " +
                    "Changing embedding model requires a schema migration and a full re-index, " +
                    "because vectors from different models are not comparable.");
            }
        }

        return payload.Embeddings;
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }
}

// The embedding model could not be reached or returned something unusable.
//
// Kept distinct from a chat-model failure because the consequences differ:
// without embeddings the platform loses historical context but every other
// part of an investigation still works.
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string message) : base(message) { }

    public EmbeddingUnavailableException(string message, Exception inner) : base(message, inner) { }
}
