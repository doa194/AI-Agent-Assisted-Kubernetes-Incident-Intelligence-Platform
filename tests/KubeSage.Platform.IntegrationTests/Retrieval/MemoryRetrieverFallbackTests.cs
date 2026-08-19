using KubeSage.Platform.Modules.Retrieval;

namespace KubeSage.Platform.IntegrationTests.Retrieval;

// Covers the metadata-filter fallback.
//
// This guards a real regression. Runbooks are categorised by the problem they
// describe (dependency_latency, out_of_memory), but an incident can be
// categorised differently (http_error_rate) and still be about exactly that
// problem. A strict category filter therefore excluded every runbook and
// retrieval silently returned nothing - the worst kind of failure, because it
// looks identical to "there was nothing relevant".
[Collection(PostgresCollection.Name)]
public sealed class MemoryRetrieverFallbackTests
{
    private readonly PostgresFixture _postgres;

    public MemoryRetrieverFallbackTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_filter_that_matches_nothing_still_returns_the_nearest_match()
    {
        // Arrange: a runbook categorised for dependency latency, and a query
        // filtered on a category no runbook carries.
        await using var context = await SemanticMemoryTests.MemoryContext.CreateAsync(_postgres);

        await context.Memory.UpsertAsync(
            new MemoryRecord
            {
                Kind = MemoryKind.Runbook,
                SourceRef = "dependency-latency#symptoms",
                Title = "Downstream dependency latency - Symptoms",
                Content = "A service returns 503 while its own pods stay healthy.",
                Category = "dependency_latency"
            },
            Padded(1.0f, 0.0f),
            TestContext.Current.CancellationToken);

        // Act: strict filter first - this is what returned nothing in production.
        var strict = await context.Memory.SearchAsync(
            Padded(1.0f, 0.0f),
            new MemorySearchFilter
            {
                Kind = MemoryKind.Runbook,
                Category = "http_error_rate",
                TopK = 5,
                MaxDistance = 0.65
            },
            TestContext.Current.CancellationToken);

        // Then the relaxed retry the retriever performs.
        var relaxed = await context.Memory.SearchAsync(
            Padded(1.0f, 0.0f),
            new MemorySearchFilter { Kind = MemoryKind.Runbook, TopK = 5, MaxDistance = 0.65 },
            TestContext.Current.CancellationToken);

        // Assert
        strict.ShouldBeEmpty("a category no runbook carries excludes everything");
        relaxed.Count.ShouldBe(1, "dropping the facet must recover the nearest match");
        relaxed[0].SourceRef.ShouldBe("dependency-latency#symptoms");
    }

    [Fact]
    public async Task The_distance_cut_off_still_applies_after_the_filter_is_relaxed()
    {
        // Relaxing the facets must not turn retrieval into "always return
        // something". Similarity remains the real gate.
        await using var context = await SemanticMemoryTests.MemoryContext.CreateAsync(_postgres);

        await context.Memory.UpsertAsync(
            new MemoryRecord
            {
                Kind = MemoryKind.Runbook,
                SourceRef = "unrelated#section",
                Title = "Something else entirely",
                Content = "unrelated guidance",
                Category = "out_of_memory"
            },
            Padded(0.0f, 1.0f),
            TestContext.Current.CancellationToken);

        var relaxed = await context.Memory.SearchAsync(
            Padded(1.0f, 0.0f),
            new MemorySearchFilter { Kind = MemoryKind.Runbook, TopK = 5, MaxDistance = 0.3 },
            TestContext.Current.CancellationToken);

        relaxed.ShouldBeEmpty("an unrelated document must stay excluded on distance");
    }

    private static float[] Padded(params float[] leading)
    {
        var full = new float[768];
        leading.CopyTo(full, 0);
        return full;
    }
}
