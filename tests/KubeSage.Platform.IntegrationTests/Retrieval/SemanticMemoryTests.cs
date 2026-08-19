using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Persistence;
using KubeSage.Platform.Modules.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;

namespace KubeSage.Platform.IntegrationTests.Retrieval;

// Storage and retrieval behaviour, tested against a real pgvector database.
//
// Deterministic vectors are used rather than the real embedding model, so
// these tests are fast, run without Ollama, and assert on ranking logic rather
// than on how a particular model happens to embed a sentence. Whether the real
// model retrieves sensibly is a separate question, answered by the gold-set
// evaluation that runs against live Ollama.
[Collection(PostgresCollection.Name)]
public sealed class SemanticMemoryTests
{
    private readonly PostgresFixture _postgres;

    public SemanticMemoryTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Re_indexing_the_same_source_updates_rather_than_duplicates()
    {
        // The property that keeps the corpus clean. Runbooks are re-indexed on
        // every start-up; without upsert semantics each restart would add
        // another copy, and those copies would then compete with each other
        // for the top-K slots in every search.
        await using var context = await MemoryContext.CreateAsync(_postgres);

        var record = new MemoryRecord
        {
            Kind = MemoryKind.Runbook,
            SourceRef = "dependency-latency#symptoms",
            Title = "Downstream dependency latency - Symptoms",
            Content = "original content"
        };

        await context.Memory.UpsertAsync(record, Vector(1.0f, 0.0f, 0.0f), TestContext.Current.CancellationToken);
        await context.Memory.UpsertAsync(
            record with { Content = "revised content" },
            Vector(0.9f, 0.1f, 0.0f),
            TestContext.Current.CancellationToken);

        var count = await context.Memory.CountAsync(MemoryKind.Runbook, TestContext.Current.CancellationToken);
        count.ShouldBe(1);

        var matches = await context.Memory.SearchAsync(
            Vector(1.0f, 0.0f, 0.0f),
            new MemorySearchFilter { Kind = MemoryKind.Runbook, TopK = 5, MaxDistance = 2.0 },
            TestContext.Current.CancellationToken);

        matches.Single().Content.ShouldBe("revised content");
    }

    [Fact]
    public async Task Search_returns_the_closest_match_first()
    {
        await using var context = await MemoryContext.CreateAsync(_postgres);

        await Store(context, "close", Vector(1.0f, 0.05f, 0.0f));
        await Store(context, "unrelated", Vector(0.0f, 1.0f, 0.0f));
        await Store(context, "opposite", Vector(-1.0f, 0.0f, 0.0f));

        var matches = await context.Memory.SearchAsync(
            Vector(1.0f, 0.0f, 0.0f),
            new MemorySearchFilter { TopK = 5, MaxDistance = 2.0 },
            TestContext.Current.CancellationToken);

        matches[0].Title.ShouldBe("close");
        matches[0].Distance.ShouldBeLessThan(matches[1].Distance);
    }

    [Fact]
    public async Task Weak_matches_are_excluded_rather_than_padding_the_results()
    {
        // Returning a weak match is worse than returning nothing. An agent
        // handed an unrelated past incident will try to make it fit, so the
        // distance cut-off matters more than filling the top-K.
        await using var context = await MemoryContext.CreateAsync(_postgres);

        await Store(context, "close", Vector(1.0f, 0.02f, 0.0f));
        await Store(context, "unrelated", Vector(0.0f, 1.0f, 0.0f));

        var matches = await context.Memory.SearchAsync(
            Vector(1.0f, 0.0f, 0.0f),
            new MemorySearchFilter { TopK = 5, MaxDistance = 0.3 },
            TestContext.Current.CancellationToken);

        matches.Count.ShouldBe(1);
        matches[0].Title.ShouldBe("close");
    }

    [Fact]
    public async Task Metadata_filters_are_applied_before_similarity()
    {
        // A textually similar memory from an unrelated service is a trap.
        // Filtering first means similarity only decides ordering among
        // memories that are already plausibly relevant.
        await using var context = await MemoryContext.CreateAsync(_postgres);

        await Store(context, "same-workload", Vector(0.6f, 0.8f, 0.0f), workload: "order-api");
        await Store(context, "other-workload", Vector(1.0f, 0.0f, 0.0f), workload: "payment-simulator");

        var matches = await context.Memory.SearchAsync(
            Vector(1.0f, 0.0f, 0.0f),
            new MemorySearchFilter { Workload = "order-api", TopK = 5, MaxDistance = 2.0 },
            TestContext.Current.CancellationToken);

        // The closer vector belongs to a different workload and is excluded,
        // even though pure similarity would have ranked it first.
        matches.ShouldAllBe(m => m.Title == "same-workload");
    }

    [Fact]
    public async Task An_incident_never_retrieves_itself()
    {
        // Without this, an investigation searching for "incidents like this
        // one" gets its own summary back, which reads as strong corroboration
        // of whatever it already believed.
        await using var context = await MemoryContext.CreateAsync(_postgres);

        var incidentId = await SeedIncidentAsync(context);

        await context.Memory.UpsertAsync(
            new MemoryRecord
            {
                Kind = MemoryKind.Incident,
                SourceRef = incidentId.ToString(),
                IncidentId = incidentId,
                Title = "the incident being investigated",
                Content = "payment latency"
            },
            Vector(1.0f, 0.0f, 0.0f),
            TestContext.Current.CancellationToken);

        var matches = await context.Memory.SearchAsync(
            Vector(1.0f, 0.0f, 0.0f),
            new MemorySearchFilter { ExcludeIncidentId = incidentId, TopK = 5, MaxDistance = 2.0 },
            TestContext.Current.CancellationToken);

        matches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retrieval_confidence_is_derived_from_distance_and_bounded()
    {
        // Retrieval confidence must stay separate from root-cause confidence.
        // This checks it is a plain function of text distance and nothing else.
        await using var context = await MemoryContext.CreateAsync(_postgres);

        await Store(context, "identical", Vector(1.0f, 0.0f, 0.0f));

        var matches = await context.Memory.SearchAsync(
            Vector(1.0f, 0.0f, 0.0f),
            new MemorySearchFilter { TopK = 1, MaxDistance = 2.0 },
            TestContext.Current.CancellationToken);

        matches[0].RetrievalConfidence.ShouldBeGreaterThan(0.99);
        matches[0].RetrievalConfidence.ShouldBeLessThanOrEqualTo(1.0);
    }

    // --- helpers ---------------------------------------------------------

    // The schema fixes the vector at 768 dimensions to match EmbeddingGemma,
    // so short test vectors are padded. Only the first few components carry
    // meaning, which keeps the expected ordering easy to read.
    private static float[] Vector(params float[] leading)
    {
        var full = new float[768];
        leading.CopyTo(full, 0);
        return full;
    }

    private static async Task Store(
        MemoryContext context,
        string title,
        float[] embedding,
        string? workload = null)
    {
        await context.Memory.UpsertAsync(
            new MemoryRecord
            {
                Kind = MemoryKind.Runbook,
                SourceRef = title,
                Title = title,
                Content = title,
                Workload = workload
            },
            embedding,
            TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> SeedIncidentAsync(MemoryContext context)
    {
        // semantic_memory.incident_id has a foreign key, so a real incident row
        // has to exist first.
        var incidentId = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO incidents (
                id, fingerprint, state, severity, category, title, detection_rule, namespace,
                first_detected_at_utc, last_detected_at_utc, updated_at_utc)
            VALUES (@id, 'fp', 'Investigating', 'High', 'dependency_latency', 'test', 'test', 'kubesage-demo',
                    now(), now(), now())
            """, connection);

        command.Parameters.AddWithValue("id", incidentId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return incidentId;
    }

    internal sealed class MemoryContext : IAsyncDisposable
    {
        private TestDatabase _database = null!;
        private NpgsqlDataSource _dataSource = null!;

        public SemanticMemoryRepository Memory { get; private set; } = null!;

        public string ConnectionString => _database.ConnectionString;

        public static async Task<MemoryContext> CreateAsync(PostgresFixture postgres)
        {
            var context = new MemoryContext { _database = await TestDatabase.CreateAsync(postgres) };

            var options = Options.Create(new KubeSageOptions
            {
                Database = new DatabaseOptions { ConnectionString = context._database.ConnectionString }
            });

            await new DatabaseMigrator(options, NullLogger<DatabaseMigrator>.Instance)
                .MigrateAsync(TestContext.Current.CancellationToken);

            // Built after migrations so Npgsql's cached type list already
            // includes the pgvector "vector" type.
            var builder = new NpgsqlDataSourceBuilder(context._database.ConnectionString);
            builder.UseVector();
            context._dataSource = builder.Build();

            context.Memory = new SemanticMemoryRepository(
                context._dataSource, NullLogger<SemanticMemoryRepository>.Instance);

            return context;
        }

        public async ValueTask DisposeAsync()
        {
            if (_dataSource is not null)
            {
                await _dataSource.DisposeAsync();
            }

            await _database.DisposeAsync();
        }
    }
}
