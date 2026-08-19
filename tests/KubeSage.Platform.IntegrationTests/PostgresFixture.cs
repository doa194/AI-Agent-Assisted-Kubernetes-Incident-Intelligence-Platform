using Testcontainers.PostgreSql;

namespace KubeSage.Platform.IntegrationTests;

// Starts a throwaway PostgreSQL container for the integration tests.
//
// The image is the same pgvector/pgvector:pg18 build the platform ships with,
// so these tests exercise the real database engine and the real extension
// rather than an approximation of them. That matters here because most of what
// is being tested - vector distance ordering, advisory locks, SKIP LOCKED
// behaviour - has no meaningful in-memory equivalent.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg18-trixie")
        .WithDatabase("kubesage_test")
        .WithUsername("kubesage_test")
        .WithPassword("kubesage_test")
        // PostgreSQL 18 images keep data in a version-specific subdirectory.
        // Testcontainers still defaults to the pre-18 path, so it is set here
        // to match what the image expects.
        .WithEnvironment("PGDATA", "/var/lib/postgresql/18/docker")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

// One container is shared by every test class in this collection. Starting a
// fresh PostgreSQL per test class would add tens of seconds for no extra
// confidence; tests isolate themselves with distinct table or schema names
// instead.
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
