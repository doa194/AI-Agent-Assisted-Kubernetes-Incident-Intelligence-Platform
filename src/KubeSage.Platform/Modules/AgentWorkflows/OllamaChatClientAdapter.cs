using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KubeSage.Platform.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Presents Ollama as an IChatClient so the Microsoft Agent Framework can drive
// it.
//
// The Agent Framework builds agents on top of IChatClient, and there is no
// supported Ollama package for it - the Microsoft.Extensions.AI.Ollama preview
// is deprecated. This adapter is the bridge, and it exists as its own class
// rather than being hidden inside an agent because it encodes two behaviours
// specific to Gemma 4 that a generic client would get wrong:
//
//   * "think" is sent explicitly on every request. Gemma 4 is a reasoning
//     model, and its reasoning tokens are generated at the same slow rate as
//     everything else. Leaving it enabled turns a short structured answer into
//     a multi-minute one on this hardware.
//
//   * the reasoning content is read and DISCARDED. It never enters a
//     ChatResponse, so it can never be persisted or shown as if it were
//     evidence.
internal sealed class OllamaChatClientAdapter : IChatClient
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaChatClientAdapter> _logger;

    public OllamaChatClientAdapter(
        HttpClient http,
        IOptions<KubeSageOptions> options,
        ILogger<OllamaChatClientAdapter> logger)
    {
        _http = http;
        _options = options.Value.Ollama;
        _logger = logger;

        Metadata = new ChatClientMetadata("ollama", _options.Endpoint, _options.ChatModel);
    }

    public ChatClientMetadata Metadata { get; }

    // Cheap reachability probe. The dispatcher calls this before claiming
    // work, so that an Ollama outage releases the work back to the queue
    // instead of burning retry attempts against a server that is down.
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama availability check failed");
            return false;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options);

        using var response = await _http.PostAsJsonAsync("/api/chat", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ModelUnavailableException(
                $"Ollama returned {(int)response.StatusCode}: {Truncate(body, 400)}");
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));

        var root = document.RootElement;
        var message = root.TryGetProperty("message", out var m) ? m : default;

        var content = message.ValueKind == JsonValueKind.Object &&
                      message.TryGetProperty("content", out var c)
            ? c.GetString() ?? string.Empty
            : string.Empty;

        // Measured, never surfaced. Knowing that a slow call spent its time
        // reasoning is useful; keeping the text is not.
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("thinking", out var thinking) &&
            thinking.ValueKind == JsonValueKind.String &&
            thinking.GetString() is { Length: > 0 } reasoning)
        {
            _logger.LogDebug(
                "Discarded {ReasoningLength} characters of model reasoning", reasoning.Length);
        }

        var chatMessage = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, content);

        // Tool calls, when the model asked for one.
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                if (!toolCall.TryGetProperty("function", out var function))
                {
                    continue;
                }

                var name = function.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);

                if (function.TryGetProperty("arguments", out var args) &&
                    args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in args.EnumerateObject())
                    {
                        arguments[property.Name] = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => property.Value.ToString()
                        };
                    }
                }

                chatMessage.Contents.Add(new FunctionCallContent(
                    callId: Guid.NewGuid().ToString("n")[..8],
                    name: name,
                    arguments: arguments));
            }
        }

        return new ChatResponse(chatMessage)
        {
            ModelId = _options.ChatModel,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = chatMessage.Contents.OfType<FunctionCallContent>().Any()
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = root.TryGetProperty("prompt_eval_count", out var pe) ? pe.GetInt64() : null,
                OutputTokenCount = root.TryGetProperty("eval_count", out var ec) ? ec.GetInt64() : null
            }
        };
    }

    // Streaming is not used by this platform - every agent call asks for one
    // complete structured answer - so it is implemented by delegating to the
    // non-streaming path rather than left to throw.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents)
            {
                ModelId = response.ModelId,
                FinishReason = response.FinishReason
            };
        }
    }

    private object BuildRequest(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = options?.ModelId ?? _options.ChatModel,
            ["stream"] = false,
            // Default OFF. Any agent that genuinely benefits from reasoning
            // opts in through AdditionalProperties.
            ["think"] = options?.AdditionalProperties?.TryGetValue("think", out var think) == true
                        && think is true,
            ["messages"] = messages.Select(message => new
            {
                role = message.Role.Value switch
                {
                    "system" => "system",
                    "tool" => "tool",
                    "assistant" => "assistant",
                    _ => "user"
                },
                content = message.Text
            }).ToList(),
            ["options"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["temperature"] = options?.Temperature ?? _options.Temperature,
                ["num_ctx"] = _options.ContextTokens,
                ["num_predict"] = options?.MaxOutputTokens ?? -1
            }
        };

        // Structured output. Passing the schema to Ollama constrains
        // generation itself, which is far more reliable than asking politely
        // for JSON and parsing whatever comes back.
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema })
        {
            payload["format"] = schema;
        }

        if (options?.Tools is { Count: > 0 } tools)
        {
            payload["tools"] = tools.OfType<AIFunction>().Select(function => new
            {
                type = "function",
                function = new
                {
                    name = function.Name,
                    description = function.Description,
                    parameters = function.JsonSchema
                }
            }).ToList();
        }

        return payload;
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "...";

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    // The HttpClient is owned by the DI container's typed-client factory, so
    // nothing is disposed here.
    public void Dispose() { }
}
