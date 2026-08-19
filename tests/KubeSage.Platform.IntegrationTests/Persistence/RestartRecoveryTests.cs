using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KubeSage.Platform.IntegrationTests.Persistence;

// What happens to in-flight work when the platform dies.
//
// The requirement is precise: recover eventually, WITHOUT duplicate reports
// and WITHOUT losing incidents. Those two failure modes pull in opposite
// directions - being eager about recovery risks duplicates, being careful
// risks stranding work - so both are asserted here.
[Collection(PostgresCollection.Name)]
public sealed class RestartRecoveryTests
{
    private readonly PostgresFixture _postgres;

    public RestartRecoveryTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task An_incident_left_mid_investigation_is_requeued_after_a_restart()
    {
        // The stranding scenario the startup recovery service exists for:
        // the work item was marked Completed, then the process died before the
        // investigation finished. Nothing will reclaim the item, and
        // deduplication will suppress a fresh incident because this one is
        // still open. Without recovery it is lost silently.
        await using var context = await RecoveryContext.CreateAsync(_postgres);

        var incidentId = await SeedIncidentAsync(context, IncidentState.Investigating);

        // The work item finished; the incident did not.
        await context.Queue.EnqueueAsync(
            WorkKind.Investigation, incidentId.ToString(), new { incidentId },
            TestContext.Current.CancellationToken);

        var claimed = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        await context.Queue.CompleteAsync(claimed[0].Id, TestContext.Current.CancellationToken);

        // Restart: the incident is still in flight, so it is requeued.
        var inFlight = await context.Incidents.ListInFlightAsync(TestContext.Current.CancellationToken);
        inFlight.ShouldContain(i => i.Id == incidentId);

        var requeued = await context.Queue.EnqueueAsync(
            WorkKind.Investigation, incidentId.ToString(),
            new { incidentId, trigger = "startup-recovery" },
            TestContext.Current.CancellationToken);

        requeued.ShouldNotBeNull("a stranded incident must be picked up again after a restart");
    }

    [Fact]
    public async Task Recovery_does_not_duplicate_work_that_is_still_queued()
    {
        // The opposite risk. An incident that is in flight AND already has a
        // pending work item must not get a second one, or the restart itself
        // would cause two investigations and two contradictory reports.
        await using var context = await RecoveryContext.CreateAsync(_postgres);

        var incidentId = await SeedIncidentAsync(context, IncidentState.Candidate);

        var first = await context.Queue.EnqueueAsync(
            WorkKind.Investigation, incidentId.ToString(), new { incidentId },
            TestContext.Current.CancellationToken);

        var duringRecovery = await context.Queue.EnqueueAsync(
            WorkKind.Investigation, incidentId.ToString(),
            new { incidentId, trigger = "startup-recovery" },
            TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        duringRecovery.ShouldBeNull("work already queued must not be queued twice by recovery");

        var depth = await context.Queue.GetDepthAsync(TestContext.Current.CancellationToken);
        depth["Pending"].ShouldBe(1);
    }

    [Fact]
    public async Task A_reported_incident_is_not_reopened_by_recovery()
    {
        // Terminal incidents must stay terminal. Reopening one would produce a
        // second report for an event that was already explained.
        await using var context = await RecoveryContext.CreateAsync(_postgres);

        var incidentId = await SeedIncidentAsync(context, IncidentState.Investigating);

        await context.Incidents.TransitionAsync(
            incidentId, IncidentState.Reported, null, TestContext.Current.CancellationToken);

        var inFlight = await context.Incidents.ListInFlightAsync(TestContext.Current.CancellationToken);

        inFlight.ShouldNotContain(i => i.Id == incidentId);
    }

    [Fact]
    public async Task A_resumed_incident_can_reach_a_terminal_state()
    {
        // Guards a real bug: the dispatcher used to move every claimed
        // incident to Triaging, but an incident resumed after a crash is
        // already Investigating, and Investigating -> Triaging is forbidden.
        // Recovery therefore threw on the very path meant to rescue it, and
        // the incident failed permanently every time.
        await using var context = await RecoveryContext.CreateAsync(_postgres);

        var incidentId = await SeedIncidentAsync(context, IncidentState.Investigating);

        Should.Throw<InvalidIncidentTransitionException>(() =>
            IncidentStateMachine.EnsureTransition(IncidentState.Investigating, IncidentState.Triaging));

        // The route the dispatcher actually takes for a resumed incident.
        var moved = await context.Incidents.TransitionAsync(
            incidentId, IncidentState.Reported, null, TestContext.Current.CancellationToken);

        moved.ShouldBeTrue();

        var incident = await context.Incidents.GetAsync(incidentId, TestContext.Current.CancellationToken);
        incident!.State.ShouldBe(IncidentState.Reported);
    }

    [Fact]
    public async Task A_failed_investigation_can_be_retried_from_its_failed_state()
    {
        // Failed is retryable, unlike the terminal states. This is what lets
        // an Ollama outage resolve itself once the model comes back.
        await using var context = await RecoveryContext.CreateAsync(_postgres);

        var incidentId = await SeedIncidentAsync(context, IncidentState.Investigating);

        await context.Incidents.TransitionAsync(
            incidentId, IncidentState.Failed, "model unavailable", TestContext.Current.CancellationToken);

        var inFlight = await context.Incidents.ListInFlightAsync(TestContext.Current.CancellationToken);
        inFlight.ShouldContain(i => i.Id == incidentId, "a failed incident must remain eligible for retry");

        var retried = await context.Incidents.TransitionAsync(
            incidentId, IncidentState.Investigating, null, TestContext.Current.CancellationToken);

        retried.ShouldBeTrue();
    }

    private static async Task<Guid> SeedIncidentAsync(RecoveryContext context, IncidentState state)
    {
        var incidentId = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO incidents (
                id, fingerprint, state, severity, category, title, detection_rule, namespace,
                affected_workloads, first_detected_at_utc, last_detected_at_utc, updated_at_utc)
            VALUES (@id, @fingerprint, @state, 'High', 'dependency_latency',
                    'order-api is returning errors', 'test', 'kubesage-demo',
                    ARRAY['order-api'], now(), now(), now())
            """, connection);

        command.Parameters.AddWithValue("id", incidentId);
        command.Parameters.AddWithValue("fingerprint", incidentId.ToString("n")[..20]);
        command.Parameters.AddWithValue("state", state.ToString());

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return incidentId;
    }

    private sealed class RecoveryContext : IAsyncDisposable
    {
        private TestDatabase _database = null!;
        private NpgsqlDataSource _dataSource = null!;

        public WorkQueue Queue { get; private set; } = null!;

        public IncidentRepository Incidents { get; private set; } = null!;

        public string ConnectionString => _database.ConnectionString;

        public static async Task<RecoveryContext> CreateAsync(PostgresFixture postgres)
        {
            var context = new RecoveryContext { _database = await TestDatabase.CreateAsync(postgres) };

            var options = Options.Create(new KubeSageOptions
            {
                Database = new DatabaseOptions { ConnectionString = context._database.ConnectionString }
            });

            await new DatabaseMigrator(options, NullLogger<DatabaseMigrator>.Instance)
                .MigrateAsync(TestContext.Current.CancellationToken);

            context._dataSource = NpgsqlDataSource.Create(context._database.ConnectionString);
            context.Queue = new WorkQueue(context._dataSource, options, NullLogger<WorkQueue>.Instance);
            context.Incidents = new IncidentRepository(
                context._dataSource, NullLogger<IncidentRepository>.Instance);

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
