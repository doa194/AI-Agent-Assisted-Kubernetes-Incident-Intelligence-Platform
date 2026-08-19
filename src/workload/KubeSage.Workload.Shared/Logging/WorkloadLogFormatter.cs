using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KubeSage.Workload.Shared.Logging;

// Writes every log line as one flat JSON object with a fixed, predictable
// shape.
//
// Why a custom formatter instead of the built-in AddJsonConsole: the built-in
// one nests the message template arguments inside a "State" object and keeps
// the original template around. That is fine for a human, but the detection
// rules in the AI platform parse these lines with LogQL, and a nested,
// variable shape makes those queries fragile.
//
// The shape produced here is the contract between the demo workload and the
// detection layer. Changing a field name here means changing detection rules,
// so the names are deliberately short and stable.
public sealed class WorkloadLogFormatter : ConsoleFormatter
{
    public const string FormatterName = "kubesage-json";

    private readonly WorkloadLogContext _context;
    private readonly IOptionsMonitor<ConsoleFormatterOptions> _options;

    public WorkloadLogFormatter(WorkloadLogContext context, IOptionsMonitor<ConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _context = context;
        _options = options;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        // A log entry with neither text nor an exception carries no
        // information, so it is dropped rather than emitting an empty object.
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, JsonWriterOptions))
        {
            writer.WriteStartObject();

            writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
            writer.WriteString("level", LevelName(logEntry.LogLevel));
            writer.WriteString("service", _context.ServiceName);

            // Correlation identifier for the request being handled, if any.
            // This is what lets the platform follow one user request across
            // the gateway, the order API and the payment simulator.
            var correlationId = CorrelationContext.CurrentId;
            if (!string.IsNullOrEmpty(correlationId))
            {
                writer.WriteString("correlationId", correlationId);
            }

            writer.WriteString("msg", message);

            if (!string.IsNullOrEmpty(_context.PodName))
            {
                writer.WriteString("pod", _context.PodName);
            }

            // Structured arguments from the message template become top-level
            // fields, so "{DurationMs}" in a template turns into a queryable
            // "durationMs" field rather than being buried in the text.
            WriteStateFields(writer, logEntry.State);

            // Scopes are off by default. ASP.NET Core adds trace, span,
            // connection and request identifiers as scopes, and writing them
            // roughly doubles the size of every line with values this project
            // has no use for - it deliberately does not do distributed
            // tracing, and correlation is handled by correlationId above.
            if (_options.CurrentValue.IncludeScopes)
            {
                WriteScopeFields(writer, scopeProvider);
            }

            if (logEntry.Exception is not null)
            {
                writer.WriteString("errorType", logEntry.Exception.GetType().FullName);
                writer.WriteString("errorMessage", logEntry.Exception.Message);
            }

            writer.WriteEndObject();
        }

        textWriter.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteStateFields<TState>(Utf8JsonWriter writer, TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            return;
        }

        foreach (var field in fields)
        {
            // The formatter already wrote the rendered message; repeating the
            // raw template would double the size of every line for no gain.
            if (field.Key == "{OriginalFormat}")
            {
                continue;
            }

            WriteField(writer, field.Key, field.Value);
        }
    }

    private static void WriteScopeFields(Utf8JsonWriter writer, IExternalScopeProvider? scopeProvider)
    {
        scopeProvider?.ForEachScope(
            static (scope, state) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        if (pair.Key == "{OriginalFormat}")
                        {
                            continue;
                        }

                        WriteField(state, pair.Key, pair.Value);
                    }
                }
            },
            writer);
    }

    private static void WriteField(Utf8JsonWriter writer, string key, object? value)
    {
        var name = ToCamelCase(key);

        switch (value)
        {
            case null:
                break;
            case string text:
                writer.WriteString(name, text);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            case int number:
                writer.WriteNumber(name, number);
                break;
            case long number:
                writer.WriteNumber(name, number);
                break;
            case double number:
                writer.WriteNumber(name, number);
                break;
            case decimal number:
                writer.WriteNumber(name, number);
                break;
            default:
                writer.WriteString(name, value.ToString());
                break;
        }
    }

    // "DurationMs" -> "durationMs". Message templates conventionally use
    // PascalCase; JSON log fields conventionally use camelCase.
    private static string ToCamelCase(string value)
    {
        if (value.Length == 0 || char.IsLower(value[0]))
        {
            return value;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            source.AsSpan().CopyTo(span);
            span[0] = char.ToLowerInvariant(span[0]);
        });
    }

    // Short, fixed severity names. Detection rules match on these exact
    // strings, so they must not follow .NET's enum names changing.
    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "fatal",
        _ => "none"
    };

    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Indented = false,
        SkipValidation = true
    };
}

// Values that are the same for every log line this process writes.
public sealed class WorkloadLogContext
{
    public required string ServiceName { get; init; }

    // Supplied by Kubernetes through the downward API. Knowing which pod
    // produced a line is what makes "this one replica is broken" visible.
    public string? PodName { get; init; }
}
