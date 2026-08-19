using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

namespace KubeSage.Platform.IntegrationTests.Persistence;

// Semantic incident memory depends on three separate pieces agreeing with each
// other: the pgvector extension, Npgsql's type mapping, and the Pgvector .NET
// type. A mismatch in any of them fails at runtime with an obscure error, so
// the combination is proven here against the real database.
[Collection(PostgresCollection.Name)]
public sealed class PgvectorTests
{
    private readonly PostgresFixture _postgres;

    public PgvectorTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Cosine_distance_ranks_the_most_similar_vector_first()
    {
        // Arrange: three vectors, one of which points in almost the same
        // direction as the query.
        await using var database = await TestDatabase.CreateAsync(_postgres);
        // The extension has to exist BEFORE the vector-aware data source first
        // connects. Npgsql reads the database's type catalogue once and caches
        // it, so a data source built against a database without pgvector never
        // learns about the "vector" type afterwards. In the running platform
        // the extension is created by the database init script and by the
        // baseline migration, both of which happen before any query.
        await CreateVectorExtensionAsync(database.ConnectionString);

        await using var dataSource = BuildDataSource(database.ConnectionString);
        await ExecuteAsync(dataSource,
            "CREATE TABLE memory (id text PRIMARY KEY, embedding vector(3))");

        await InsertAsync(dataSource, "almost-identical", [1.0f, 0.0f, 0.05f]);
        await InsertAsync(dataSource, "orthogonal", [0.0f, 1.0f, 0.0f]);
        await InsertAsync(dataSource, "opposite", [-1.0f, 0.0f, 0.0f]);

        var query = new Vector(new float[] { 1.0f, 0.0f, 0.0f });

        // Act: <=> is pgvector's cosine distance operator, so ascending order
        // means most similar first.
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, embedding <=> @query AS distance FROM memory ORDER BY distance ASC", connection);
        command.Parameters.Add(VectorParameter("query", query));

        var ranked = new List<(string Id, double Distance)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ranked.Add((reader.GetString(0), reader.GetDouble(1)));
        }

        // Assert
        ranked.Count.ShouldBe(3);
        ranked[0].Id.ShouldBe("almost-identical");
        ranked[2].Id.ShouldBe("opposite");
        ranked[0].Distance.ShouldBeLessThan(ranked[1].Distance);
    }

    [Fact]
    public async Task A_vector_survives_a_write_then_read_round_trip()
    {
        // Guards the Npgsql type mapping specifically: a silent truncation or
        // precision loss here would quietly degrade every similarity search.
        await using var database = await TestDatabase.CreateAsync(_postgres);
        // The extension has to exist BEFORE the vector-aware data source first
        // connects. Npgsql reads the database's type catalogue once and caches
        // it, so a data source built against a database without pgvector never
        // learns about the "vector" type afterwards. In the running platform
        // the extension is created by the database init script and by the
        // baseline migration, both of which happen before any query.
        await CreateVectorExtensionAsync(database.ConnectionString);

        await using var dataSource = BuildDataSource(database.ConnectionString);
        await ExecuteAsync(dataSource, "CREATE TABLE roundtrip (id text PRIMARY KEY, embedding vector(4))");

        var original = new float[] { 0.125f, -0.5f, 0.75f, 1.0f };
        await InsertAsync(dataSource, "sample", original, table: "roundtrip");

        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT embedding FROM roundtrip WHERE id = 'sample'", connection);
        var stored = (Vector?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        stored.ShouldNotBeNull();
        stored.ToArray().ShouldBe(original);
    }

    // Npgsql cannot infer the PostgreSQL type of a Pgvector.Vector on its own,
    // so the type name has to be stated explicitly on the parameter. Without
    // this the write fails at runtime with an InvalidCastException that does
    // not mention vectors at all.
    private static NpgsqlParameter VectorParameter(string name, Vector value) => new()
    {
        ParameterName = name,
        DataTypeName = "vector",
        Value = value
    };

    private static NpgsqlDataSource BuildDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    private static async Task CreateVectorExtensionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertAsync(
        NpgsqlDataSource dataSource,
        string id,
        float[] values,
        string table = "memory")
    {
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {table} (id, embedding) VALUES (@id, @embedding)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(VectorParameter("embedding", new Vector(values)));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
