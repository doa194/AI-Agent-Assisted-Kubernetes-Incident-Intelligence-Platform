using System.Text;
using System.Text.RegularExpressions;

namespace KubeSage.Platform.Modules.Telemetry;

// Removes secrets and dangerous characters from telemetry before it is
// allowed anywhere near the model.
//
// Two separate jobs, both mandatory:
//
//  1. REDACTION. Application logs leak credentials more often than anyone
//     expects - a connection string in an error message, a bearer token in a
//     failed request dump. Once such a value is sent to a model it is in a
//     prompt, and in this project it could also end up written into a stored
//     incident report. Removing it here is the last chance.
//
//  2. NEUTRALISING CONTROL CHARACTERS. Log content is untrusted input that
//     happens to be written by whoever could reach the application. Control
//     characters and escape sequences are stripped so that log text cannot
//     forge structure in the prompt it is embedded in.
//
// Note what this deliberately does NOT do: it does not try to detect and
// delete "instructions" hidden in log text. That would quietly destroy real
// evidence, and it does not work. Prompt injection is handled where it should
// be - the agent prompts state that log content is untrusted data, and every
// piece of evidence is presented inside an explicit data block rather than
// pasted into the instructions.
public sealed partial class SensitiveDataRedactor
{
    public const string Placeholder = "[REDACTED]";

    // Each pattern captures the secret itself in a group named "secret", so
    // the surrounding context ("password=") survives redaction. Keeping the
    // context is important: a report that says "the connection string
    // contained password=[REDACTED]" is far more useful for diagnosis than
    // one where the whole line vanished.
    private static readonly Regex[] Patterns =
    [
        ConnectionStringSecret(),
        BearerToken(),
        JsonWebToken(),
        AuthorizationHeader(),
        ApiKeyAssignment(),
        PrivateKeyBlock(),
        AwsAccessKey()
    ];

    public RedactionResult Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new RedactionResult(string.Empty, 0);
        }

        var redactions = 0;
        var working = input;

        foreach (var pattern in Patterns)
        {
            working = pattern.Replace(working, match =>
            {
                var secret = match.Groups["secret"];

                if (!secret.Success)
                {
                    redactions++;
                    return Placeholder;
                }

                redactions++;

                // Rebuild the match with only the captured secret replaced.
                var prefixLength = secret.Index - match.Index;
                return string.Concat(
                    match.Value.AsSpan(0, prefixLength),
                    Placeholder,
                    match.Value.AsSpan(prefixLength + secret.Length));
            });
        }

        return new RedactionResult(StripControlCharacters(working), redactions);
    }

    // Removes characters that have no place in evidence text: ANSI escape
    // sequences, null bytes and other C0 control codes. Tab, carriage return
    // and newline are kept because they carry real structure in a stack
    // trace.
    private static string StripControlCharacters(string input)
    {
        if (!input.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            return input;
        }

        var builder = new StringBuilder(input.Length);

        foreach (var character in input)
        {
            if (!char.IsControl(character) || character is '\n' or '\r' or '\t')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    // Password or user id inside a connection string, in either the
    // "Password=x;" or "postgres://user:pass@host" form.
    [GeneratedRegex(
        @"(?i)\b(?:password|pwd|user\s*id|uid)\s*=\s*(?<secret>[^;\s""']{1,200})",
        RegexOptions.Compiled)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(
        @"(?i)\bbearer\s+(?<secret>[A-Za-z0-9\-._~+/]{16,}=*)",
        RegexOptions.Compiled)]
    private static partial Regex BearerToken();

    // Three base64url segments separated by dots is the JWT shape.
    [GeneratedRegex(
        @"(?<secret>eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})",
        RegexOptions.Compiled)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(
        @"(?i)\b(?:authorization|x-api-key|proxy-authorization)\s*[:=]\s*(?<secret>[^\s,;""']{8,})",
        RegexOptions.Compiled)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(
        @"(?i)\b(?:api[_-]?key|secret|token|access[_-]?key|client[_-]?secret)\s*[:=]\s*[""']?(?<secret>[A-Za-z0-9\-._~+/]{12,})",
        RegexOptions.Compiled)]
    private static partial Regex ApiKeyAssignment();

    [GeneratedRegex(
        @"-----BEGIN[^-]{0,40}PRIVATE KEY-----(?<secret>[\s\S]*?)-----END[^-]{0,40}PRIVATE KEY-----",
        RegexOptions.Compiled)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\b(?<secret>AKIA[0-9A-Z]{16})\b", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKey();
}

public readonly record struct RedactionResult(string Text, int RedactionCount);
