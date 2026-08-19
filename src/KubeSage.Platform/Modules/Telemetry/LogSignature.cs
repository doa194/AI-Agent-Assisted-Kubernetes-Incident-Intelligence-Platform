using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KubeSage.Platform.Modules.Telemetry;

// Turns a log message into a stable "signature" by replacing the parts that
// change on every occurrence.
//
// Why this exists: during an incident the same failure can be logged
// thousands of times, identical except for an order identifier and a
// duration. Sending those lines to a model would waste the entire context
// window on repetition, and counting them as distinct errors would make
// deduplication impossible.
//
// After normalisation:
//
//   "Order ord_9f2a11 could not be persisted after 5012.4ms"
//   "Order ord_44bc03 could not be persisted after 4981.1ms"
//
// both become the same signature, which can then be reported once with a
// count of two. That count is far more useful evidence than either line.
public static partial class LogSignature
{
    // Order matters: the more specific patterns run first so that, for
    // example, a GUID is not first mangled by the hex-string rule.
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (Guid(), "<guid>"),
        (Timestamp(), "<timestamp>"),
        (IpAddress(), "<ip>"),
        (Duration(), "<duration>"),
        (QuotedString(), "'<str>'"),
        (PrefixedIdentifier(), "<id>"),
        (LongHex(), "<hex>"),
        (Number(), "<n>")
    ];

    public static string Normalise(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var working = message.Trim();

        foreach (var (pattern, replacement) in Rules)
        {
            working = pattern.Replace(working, replacement);
        }

        // Collapse runs of whitespace so that formatting differences do not
        // split one signature into several.
        working = Whitespace().Replace(working, " ").Trim();

        // A very long normalised message is almost always a stack trace or a
        // dumped payload. Truncating keeps signatures comparable and bounded.
        return working.Length > 300 ? working[..300] : working;
    }

    // Short, stable identifier for a normalised message, used as a
    // deduplication key and as part of an incident fingerprint.
    public static string Hash(string? message)
    {
        var normalised = Normalise(message);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash)[..16];
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled)]
    private static partial Regex Guid();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?", RegexOptions.Compiled)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b", RegexOptions.Compiled)]
    private static partial Regex IpAddress();

    // "512.4ms", "3s", "1.5 seconds" - the measured part of a message.
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:ms|s|sec|secs|seconds|milliseconds)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    [GeneratedRegex(@"'[^']{0,200}'", RegexOptions.Compiled)]
    private static partial Regex QuotedString();

    // Identifiers the demo workload generates, such as ord_9f2a11 or
    // auth_2b7c. Matching the shape rather than the exact prefix keeps this
    // useful for other applications too.
    [GeneratedRegex(@"\b[a-z]{2,12}_[A-Za-z0-9]{4,}\b", RegexOptions.Compiled)]
    private static partial Regex PrefixedIdentifier();

    [GeneratedRegex(@"\b[0-9a-fA-F]{12,}\b", RegexOptions.Compiled)]
    private static partial Regex LongHex();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();
}
