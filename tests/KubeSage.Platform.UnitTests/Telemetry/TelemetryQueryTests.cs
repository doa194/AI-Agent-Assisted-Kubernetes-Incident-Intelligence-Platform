using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Telemetry;

namespace KubeSage.Platform.UnitTests.Telemetry;

// Guards the boundary every agent tool call has to cross.
//
// Two behaviours are being protected, and they are deliberately different:
//   * an over-large but honest request is CLAMPED, so the caller still gets
//     useful data instead of an error;
//   * a malformed or out-of-scope identifier is REFUSED, because silently
//     sanitising it would hide the attempt.
public sealed class TelemetryQueryTests
{
    private static TelemetryQuery Create(
        int maxQueryRangeMinutes = 120,
        int maxLogLines = 500,
        string[]? allowedNamespaces = null)
    {
        var telemetry = new TelemetryOptions
        {
            MaxQueryRangeMinutes = maxQueryRangeMinutes,
            MaxLogLinesPerQuery = maxLogLines,
            WorkloadNamespace = "kubesage-demo"
        };

        var kubernetes = new KubernetesOptions
        {
            AllowedNamespaces = allowedNamespaces ?? ["kubesage-demo", "kubesage-observability"]
        };

        return new TelemetryQuery(telemetry, kubernetes);
    }

    [Theory]
    // A workload name carrying LogQL syntax would otherwise be interpolated
    // straight into the stream selector.
    [InlineData("order-api\"} |= \"password")]
    [InlineData("order-api\", container=\"gateway")]
    [InlineData("../../etc/passwd")]
    [InlineData("Order-API")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_workload_names_are_refused(string? workload)
    {
        var guard = Create();

        Should.Throw<TelemetryQueryRejectedException>(() => guard.RequireWorkload(workload));
    }

    [Fact]
    public void Valid_workload_names_are_accepted()
    {
        var guard = Create();

        guard.RequireWorkload("order-api").ShouldBe("order-api");
        guard.RequireWorkload("workload-db").ShouldBe("workload-db");
    }

    [Fact]
    public void Namespaces_outside_the_allow_list_are_refused()
    {
        // The allow-list is the real containment boundary: even a perfectly
        // well-formed namespace name must be refused if the platform was not
        // configured to observe it.
        var guard = Create(allowedNamespaces: ["kubesage-demo"]);

        var exception = Should.Throw<TelemetryQueryRejectedException>(
            () => guard.RequireNamespace("kube-system"));

        exception.Message.ShouldContain("not in the allowed list");
    }

    [Fact]
    public void An_absent_namespace_falls_back_to_the_configured_workload_namespace()
    {
        var guard = Create();

        guard.RequireNamespace(null).ShouldBe("kubesage-demo");
    }

    [Fact]
    public void An_over_long_window_is_trimmed_from_the_start_not_the_end()
    {
        // During an incident the most recent data matters most, so when a
        // window has to be shortened the OLDEST part is dropped. Trimming the
        // end instead would discard exactly the minutes being investigated.
        var guard = Create(maxQueryRangeMinutes: 30);
        var end = DateTimeOffset.UtcNow;
        var start = end.AddHours(-6);

        var window = guard.ClampWindow(start, end);

        window.WasClamped.ShouldBeTrue();
        window.Duration.ShouldBe(TimeSpan.FromMinutes(30));
        window.End.ShouldBe(end);
        window.Start.ShouldBe(end.AddMinutes(-30));
    }

    [Fact]
    public void A_window_within_the_limit_is_left_alone()
    {
        var guard = Create(maxQueryRangeMinutes: 120);
        var end = DateTimeOffset.UtcNow;
        var start = end.AddMinutes(-15);

        var window = guard.ClampWindow(start, end);

        window.WasClamped.ShouldBeFalse();
        window.Start.ShouldBe(start);
        window.End.ShouldBe(end);
    }

    [Fact]
    public void A_backwards_window_is_refused()
    {
        var guard = Create();
        var now = DateTimeOffset.UtcNow;

        Should.Throw<TelemetryQueryRejectedException>(
            () => guard.ClampWindow(now, now.AddMinutes(-5)));
    }

    [Theory]
    [InlineData(10_000, 500)]
    [InlineData(0, 500)]
    [InlineData(-5, 500)]
    [InlineData(50, 50)]
    public void Line_limits_are_clamped_to_the_configured_ceiling(int requested, int expected)
    {
        var guard = Create(maxLogLines: 500);

        guard.ClampLineLimit(requested).ShouldBe(expected);
    }

    [Fact]
    public void Free_text_search_terms_cannot_break_out_of_a_logql_literal()
    {
        // Without escaping, this term would close the string and append its
        // own filter clauses to the query.
        const string hostile = "boom\" } |= \"secret";

        var escaped = TelemetryQuery.EscapeLogQlLiteral(hostile);

        // The invariant that actually matters is that no quote is left
        // unescaped. Checking for the absence of the substring `" }` would be
        // wrong: `\" }` legitimately still contains it, and the backslash is
        // exactly what makes it harmless.
        CountUnescapedQuotes(escaped).ShouldBe(0);
        escaped.ShouldBe("boom\\\" } |= \\\"secret");
    }

    // Counts double quotes that are not preceded by an odd number of
    // backslashes, i.e. the ones that would still terminate a LogQL literal.
    private static int CountUnescapedQuotes(string value)
    {
        var unescaped = 0;

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '"')
            {
                continue;
            }

            var backslashes = 0;
            for (var back = index - 1; back >= 0 && value[back] == '\\'; back--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 0)
            {
                unescaped++;
            }
        }

        return unescaped;
    }

    [Fact]
    public void Search_terms_are_bounded_and_stripped_of_newlines()
    {
        var escaped = TelemetryQuery.EscapeLogQlLiteral(new string('x', 500) + "\nsecond line");

        escaped.Length.ShouldBeLessThanOrEqualTo(200);
        escaped.ShouldNotContain("\n");
    }
}
