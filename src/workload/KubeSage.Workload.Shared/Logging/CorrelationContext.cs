namespace KubeSage.Workload.Shared.Logging;

// Carries the correlation identifier for the request currently being handled.
//
// Why this exists: the platform needs to follow one user request as it moves
// from the gateway to the order API and on to the payment simulator. Full
// distributed tracing is deliberately out of scope for this project, so a
// single identifier passed in a header and written into every log line does
// the same job for far less machinery.
//
// AsyncLocal is what makes the value follow an async call chain, including
// across awaits, without every method having to accept it as a parameter.
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public const string HeaderName = "X-Correlation-Id";

    public static string? CurrentId
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    // Identifiers arrive from another service over HTTP, so they are treated
    // as untrusted input. An over-long or oddly formatted value would end up
    // in log lines and later in evidence shown to a model, so it is bounded
    // and stripped of anything that is not a plain identifier character.
    public static string Sanitise(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return NewId();
        }

        Span<char> buffer = stackalloc char[MaxLength];
        var length = 0;

        foreach (var character in candidate)
        {
            if (length == MaxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                buffer[length++] = character;
            }
        }

        return length == 0 ? NewId() : new string(buffer[..length]);
    }

    public static string NewId() => Guid.NewGuid().ToString("n")[..16];

    private const int MaxLength = 64;
}
