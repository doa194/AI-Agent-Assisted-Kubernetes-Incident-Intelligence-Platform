using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KubeSage.Platform.IntegrationTests.Persistence;

// The work queue is what makes autonomous processing survive a crash, and
// every one of its guarantees depends on PostgreSQL behaviour that cannot be
// meaningfully faked: SKIP LOCKED, partial unique indexes, transaction
// isolation. So these run against the real database.
//
// The properties under test are the ones that would cause visible, expensive
// failures if broken: a duplicate report for one incident, an investigation
// lost to a restart, or a poison item retried forever.
[Collection(PostgresCollection.Name)]
public sealed class WorkQueueTests
{
    private readonly PostgresFixture _postgres;

    public WorkQueueTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Queuing_the_same_work_twice_produces_one_item()
    {
        // The single most important guarantee here. Detection can observe the
        // same incident on consecutive passes, and each observation tries to
        // queue an investigation. Two items would mean two investigations and
        // two contradictory reports for one event.
        await using var context = await QueueContext.CreateAsync(_postgres);

        var first = await context.Queue.EnqueueAsync(
            WorkKind.Investigation, "incident-42", new { incidentId = 42 }, TestContext.Current.CancellationToken);

        var second = await context.Queue.EnqueueAsync(
            WorkKind.Investigation, "incident-42", new { incidentId = 42 }, TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldBeNull("the second enqueue must be suppressed as a duplicate");

        var depth = await context.Queue.GetDepthAsync(TestContext.Current.CancellationToken);
        depth["Pending"].ShouldBe(1);
    }

    [Fact]
    public async Task Claimed_work_is_not_handed_to_a_second_worker()
    {
        await using var context = await QueueContext.CreateAsync(_postgres);

        await context.Queue.EnqueueAsync(
            WorkKind.Investigation, "incident-1", new { }, TestContext.Current.CancellationToken);

        var firstClaim = await context.Queue.ClaimAsync(10, TestContext.Current.CancellationToken);
        var secondClaim = await context.Queue.ClaimAsync(10, TestContext.Current.CancellationToken);

        firstClaim.Count.ShouldBe(1);
        secondClaim.ShouldBeEmpty("work already claimed and within its lease must not be claimable again");
    }

    [Fact]
    public async Task Work_abandoned_by_a_dead_process_becomes_claimable_again()
    {
        // This is the restart-recovery guarantee. A process that dies
        // mid-investigation leaves a Claimed row; once its lease expires the
        // work must come back rather than being lost.
        await using var context = await QueueContext.CreateAsync(_postgres, workLeaseSeconds: 60);

        await context.Queue.EnqueueAsync(
            WorkKind.Investigation, "incident-lost", new { }, TestContext.Current.CancellationToken);

        var claimed = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        claimed.Count.ShouldBe(1);

        // Simulate the process dying: force the lease into the past rather
        // than waiting a real minute for it to expire.
        await ExpireLeaseAsync(context, claimed[0].Id);

        var reclaimed = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);

        reclaimed.Count.ShouldBe(1);
        reclaimed[0].Id.ShouldBe(claimed[0].Id);
        reclaimed[0].Attempt.ShouldBe(2, "a reclaim counts as another attempt");
    }

    [Fact]
    public async Task A_failing_item_is_retried_then_given_up_on()
    {
        // Retrying forever would keep a poison item cycling through a slow
        // model indefinitely. It must stop and stay visible for an operator.
        await using var context = await QueueContext.CreateAsync(_postgres, maxRetries: 1);

        await context.Queue.EnqueueAsync(
            WorkKind.Investigation, "incident-poison", new { }, TestContext.Current.CancellationToken);

        // Attempt 1 fails and is rescheduled.
        var first = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        await context.Queue.FailAsync(first[0], "model unavailable", TestContext.Current.CancellationToken);

        var afterFirst = await context.Queue.GetDepthAsync(TestContext.Current.CancellationToken);
        afterFirst.ShouldContainKey("Pending");

        // Make it due immediately instead of waiting out the backoff.
        await MakeAvailableNowAsync(context, first[0].Id);

        // Attempt 2 exhausts max_attempts (maxRetries 1 => 2 attempts).
        var second = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        second.Count.ShouldBe(1);
        await context.Queue.FailAsync(second[0], "model unavailable again", TestContext.Current.CancellationToken);

        var depth = await context.Queue.GetDepthAsync(TestContext.Current.CancellationToken);
        depth.ShouldContainKey("Failed");
        depth["Failed"].ShouldBe(1);

        await MakeAvailableNowAsync(context, first[0].Id);
        var third = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        third.ShouldBeEmpty("an item that exhausted its attempts must not be picked up again");
    }

    [Fact]
    public async Task Completed_work_frees_the_deduplication_key_for_a_recurrence()
    {
        // The unique index covers only unfinished work. That is deliberate:
        // the same incident recurring next week is new work, not a duplicate
        // of something finished long ago.
        await using var context = await QueueContext.CreateAsync(_postgres);

        var first = await context.Queue.EnqueueAsync(
            WorkKind.ScheduledAnalysis, "window-1", new { }, TestContext.Current.CancellationToken);

        var claimed = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        await context.Queue.CompleteAsync(claimed[0].Id, TestContext.Current.CancellationToken);

        var second = await context.Queue.EnqueueAsync(
            WorkKind.ScheduledAnalysis, "window-1", new { }, TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull("once the earlier item finished, the same key may be queued again");
    }

    [Fact]
    public async Task Renewing_a_lease_keeps_a_long_running_investigation_safe()
    {
        // An investigation on a slow local model can outlast its lease. Renewal
        // is what stops a healthy long run from being claimed a second time.
        await using var context = await QueueContext.CreateAsync(_postgres, workLeaseSeconds: 120);

        await context.Queue.EnqueueAsync(
            WorkKind.Investigation, "incident-slow", new { }, TestContext.Current.CancellationToken);

        var claimed = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        await ExpireLeaseAsync(context, claimed[0].Id);

        var renewed = await context.Queue.RenewLeaseAsync(claimed[0].Id, TestContext.Current.CancellationToken);
        renewed.ShouldBeTrue();

        var stolen = await context.Queue.ClaimAsync(1, TestContext.Current.CancellationToken);
        stolen.ShouldBeEmpty("a renewed lease must protect work that is still running");
    }

    [Fact]
    public async Task Claiming_respects_the_requested_limit()
    {
        // This is the backpressure control. With one slow model, claiming more
        // than one investigation at a time produces timeouts, not throughput.
        await using var context = await QueueContext.CreateAsync(_postgres);

        for (var index = 0; index < 5; index++)
        {
            await context.Queue.EnqueueAsync(
                WorkKind.Investigation, $"incident-{index}", new { }, TestContext.Current.CancellationToken);
        }

        var claimed = await context.Queue.ClaimAsync(2, TestContext.Current.CancellationToken);

        claimed.Count.ShouldBe(2);
    }

    private static async Task ExpireLeaseAsync(QueueContext context, Guid id)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE work_items SET leased_until_utc = (now() AT TIME ZONE 'utc') - interval '1 hour' WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task MakeAvailableNowAsync(QueueContext context, Guid id)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE work_items SET available_at_utc = (now() AT TIME ZONE 'utc') - interval '1 minute' WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    // A migrated database plus a WorkQueue wired to it.
    private sealed class QueueContext : IAsyncDisposable
    {
        private TestDatabase _database = null!;
        private NpgsqlDataSource _dataSource = null!;

        public WorkQueue Queue { get; private set; } = null!;

        public string ConnectionString => _database.ConnectionString;

        public static async Task<QueueContext> CreateAsync(
            PostgresFixture postgres,
            int workLeaseSeconds = 2400,
            int maxRetries = 3)
        {
            var context = new QueueContext { _database = await TestDatabase.CreateAsync(postgres) };

            var options = Options.Create(new KubeSageOptions
            {
                Database = new DatabaseOptions { ConnectionString = context._database.ConnectionString },
                Investigation = new InvestigationOptions
                {
                    WorkLeaseSeconds = workLeaseSeconds,
                    MaxRetries = maxRetries,
                    RetryBaseDelaySeconds = 1
                }
            });

            await new DatabaseMigrator(options, NullLogger<DatabaseMigrator>.Instance)
                .MigrateAsync(TestContext.Current.CancellationToken);

            context._dataSource = NpgsqlDataSource.Create(context._database.ConnectionString);
            context.Queue = new WorkQueue(context._dataSource, options, NullLogger<WorkQueue>.Instance);

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
