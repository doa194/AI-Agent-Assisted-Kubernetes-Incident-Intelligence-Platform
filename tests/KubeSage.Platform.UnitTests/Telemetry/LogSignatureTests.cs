using KubeSage.Platform.Modules.Telemetry;

namespace KubeSage.Platform.UnitTests.Telemetry;

// Log signatures decide two things that shape the whole system:
//   * whether repeated failures collapse into one counted piece of evidence,
//     rather than flooding the model's context with near-duplicates;
//   * whether the same recurring condition keeps raising duplicate incidents.
//
// The interesting cases are the boundaries: messages that SHOULD collapse
// together, and messages that must stay apart.
public sealed class LogSignatureTests
{
    [Fact]
    public void Messages_differing_only_by_identifier_and_duration_share_a_signature()
    {
        // Arrange - two real lines from the payment-latency scenario.
        const string first = "Dependency payment-simulator timed out after 2001.29ms while processing ord_07c5a5bfeed3";
        const string second = "Dependency payment-simulator timed out after 1998.44ms while processing ord_09a8c69e5c87";

        // Act & Assert
        LogSignature.Hash(first).ShouldBe(LogSignature.Hash(second));
    }

    [Fact]
    public void Genuinely_different_failures_keep_different_signatures()
    {
        // These must NOT collapse. Merging a payment timeout with a database
        // failure would hide the very distinction root-cause analysis needs.
        const string payment = "Dependency payment-simulator timed out after 2001.29ms while processing ord_07c5a5bfeed3";
        const string database = "Order ord_07c5a5bfeed3 could not be persisted after 12.4ms; dependency workload-database is unavailable";

        LogSignature.Hash(payment).ShouldNotBe(LogSignature.Hash(database));
    }

    [Theory]
    [InlineData("Request 7f3a2b1c-4d5e-6f70-8a9b-0c1d2e3f4a5b failed", "<guid>")]
    [InlineData("Connection from 10.244.1.37:54912 dropped", "<ip>")]
    [InlineData("Retry scheduled at 2026-08-15T12:04:11.512Z", "<timestamp>")]
    [InlineData("Completed in 148.22ms", "<duration>")]
    [InlineData("Pod 'order-api-7bd5fb555b-8vktq' evicted", "'<str>'")]
    public void Volatile_values_are_replaced_with_placeholders(string message, string expectedPlaceholder)
    {
        LogSignature.Normalise(message).ShouldContain(expectedPlaceholder);
    }

    [Fact]
    public void Normalisation_is_stable_across_whitespace_differences()
    {
        LogSignature.Hash("Order   failed    badly")
            .ShouldBe(LogSignature.Hash("Order failed badly"));
    }

    [Fact]
    public void Very_long_messages_are_truncated_so_signatures_stay_comparable()
    {
        // A stack trace differing only in its deepest frames would otherwise
        // produce a new "unique" signature on every occurrence.
        var stackTrace = "Unhandled exception: " + string.Join(" ", Enumerable.Repeat("at Some.Frame.Method()", 100));

        LogSignature.Normalise(stackTrace).Length.ShouldBeLessThanOrEqualTo(300);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_messages_normalise_to_empty(string? message)
    {
        LogSignature.Normalise(message).ShouldBe(string.Empty);
    }

    [Fact]
    public void The_same_message_always_produces_the_same_hash()
    {
        // Signature stability is what makes incident deduplication work
        // across process restarts, so it must not depend on run-time state.
        const string message = "CreateOrder failed with status 503 in 2001.02ms";

        LogSignature.Hash(message).ShouldBe(LogSignature.Hash(message));
        LogSignature.Hash(message).Length.ShouldBe(16);
    }
}
