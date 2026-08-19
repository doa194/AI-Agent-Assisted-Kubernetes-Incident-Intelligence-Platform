using System.Text.Json;
using System.Text.Json.Serialization;
using KubeSage.Platform.Api;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.AgentWorkflows;
using KubeSage.Platform.Modules.Detection;
using KubeSage.Platform.Modules.Persistence;
using KubeSage.Platform.Modules.Retrieval;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

// Entry point of the KubeSage modular monolith.
//
// This file does three things and nothing else:
//   1. builds the host and registers every module,
//   2. brings the database schema up to date before serving traffic,
//   3. maps the operator API.
//
// Module-specific wiring lives with the module it belongs to, so this file
// stays readable as the platform grows.

var builder = WebApplication.CreateBuilder(args);

// --- Logging ------------------------------------------------------------
// Logs are emitted as JSON because they are collected and read by machines as
// well as people, and because the platform's own incident reports are written
// to this same stream (see section 14 of the requirements).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

// --- Configuration ------------------------------------------------------
builder.Services
    .AddOptions<KubeSageOptions>()
    .Bind(builder.Configuration.GetSection(KubeSageOptions.SectionName))
    // ValidateOnStart turns a configuration mistake into a failure to start,
    // which is far easier to diagnose than a null reference twenty minutes
    // into an unattended run.
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<KubeSageOptions>, KubeSageOptionsValidator>();

// --- Modules ------------------------------------------------------------
builder.Services.AddPersistence();
builder.Services.AddTelemetry();
builder.Services.AddDetection();
builder.Services.AddRetrieval();
builder.Services.AddAgentWorkflows();

// --- Health -------------------------------------------------------------
// Liveness answers "is this process alive"; readiness answers "can it do its
// job". They are kept separate so a temporary database outage restarts
// nothing - it only takes the platform out of rotation until it recovers.
//
// Note which check carries the "ready" tag. The database does, because the
// platform cannot record what it finds without it. Telemetry does NOT: with
// Loki or Prometheus down the platform still serves stored incidents and
// still detects what it can, and investigations correctly report reduced
// confidence rather than disappearing.
builder.Services
    .AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<TelemetryHealthCheck>("telemetry", tags: ["telemetry"])
    .AddCheck<ModelHealthCheck>("model", tags: ["model"]);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// --- Schema -------------------------------------------------------------
// Migrations run before the first request is served. If the schema cannot be
// created the process exits, because a platform that cannot persist incidents
// has nothing useful to offer.
var databaseOptions = app.Services.GetRequiredService<IOptions<KubeSageOptions>>().Value.Database;

if (databaseOptions.RunMigrationsOnStartup)
{
    await app.Services.GetRequiredService<DatabaseMigrator>()
        .MigrateAsync(app.Lifetime.ApplicationStopping);
}

// Npgsql reads the database's list of types once and caches it. The baseline
// migration is what creates the pgvector extension, so on a brand new database
// the type list is loaded here, after migrations, to be certain the "vector"
// type is known. Skipping this produces an unhelpful "data type name 'vector'
// could not be found" the first time an embedding is written.
await app.Services.GetRequiredService<NpgsqlDataSource>()
    .ReloadTypesAsync(app.Lifetime.ApplicationStopping);

app.MapHealthEndpoints();
app.MapEvidenceEndpoints();
app.MapIncidentEndpoints();
app.MapReportEndpoints();

await app.RunAsync();

// Exposed so the API test project can spin the real application up in memory.
public partial class Program;
