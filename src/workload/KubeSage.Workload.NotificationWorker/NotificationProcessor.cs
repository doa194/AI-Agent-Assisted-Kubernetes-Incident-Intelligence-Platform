using System.Diagnostics;
using KubeSage.Workload.Shared.Logging;
using KubeSage.Workload.Shared.Metrics;
using Npgsql;

namespace KubeSage.Workload.NotificationWorker;

// Polls the database for queued notifications and marks them delivered.
//
// The "delivery" is simulated - there is no mail server. What matters is that
// the worker's progress depends on the database being reachable, so the
// database-unavailable scenario produces evidence here as well as in the
// order API. Two independent services reporting the same dependency failure
// is a much stronger signal than one.
public sealed class NotificationProcessor : BackgroundService
{
    private const string ServiceName = "notification-worker";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly NotificationRepository _repository;
    private readonly ILogger<NotificationProcessor> _logger;

    public NotificationProcessor(NotificationRepository repository, ILogger<NotificationProcessor> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification worker started, polling every {PollSeconds}s", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessBatchAsync(stoppingToken);

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Notification worker stopping");
    }

    private async Task ProcessBatchAsync(CancellationToken stoppingToken)
    {
        // Each batch gets a correlation identifier of its own. Without one,
        // the worker's log lines would be the only ones in the system with no
        // way to group them into a single unit of work.
        CorrelationContext.CurrentId = CorrelationContext.NewId();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var pending = await _repository.ClaimPendingAsync(batchSize: 20, stoppingToken);

            WorkloadMetrics.DependencyDuration
                .WithLabels(ServiceName, "workload-database", "success")
                .Observe(stopwatch.Elapsed.TotalSeconds);

            if (pending.Count == 0)
            {
                var depth = await _repository.CountPendingAsync(stoppingToken);
                WorkloadMetrics.PendingNotifications.WithLabels(ServiceName).Set(depth);
                return;
            }

            foreach (var notification in pending)
            {
                // Stands in for the work a real delivery would do.
                await Task.Delay(Random.Shared.Next(5, 25), stoppingToken);

                await _repository.MarkDeliveredAsync(notification.Id, stoppingToken);

                WorkloadMetrics.NotificationsProcessed.WithLabels(ServiceName, "delivered").Inc();

                _logger.LogInformation(
                    "Notification {NotificationId} for order {OrderId} delivered over {Channel}",
                    notification.Id, notification.OrderId, notification.Channel);
            }

            stopwatch.Stop();

            var remaining = await _repository.CountPendingAsync(stoppingToken);
            WorkloadMetrics.PendingNotifications.WithLabels(ServiceName).Set(remaining);

            _logger.LogInformation(
                "Processed {BatchSize} notifications in {DurationMs}ms, {PendingCount} still pending",
                pending.Count, stopwatch.Elapsed.TotalMilliseconds, remaining);
        }
        catch (NpgsqlException ex)
        {
            stopwatch.Stop();

            WorkloadMetrics.DependencyDuration
                .WithLabels(ServiceName, "workload-database", "failure")
                .Observe(stopwatch.Elapsed.TotalSeconds);
            WorkloadMetrics.DependencyFailures
                .WithLabels(ServiceName, "workload-database", "connection")
                .Inc();
            WorkloadMetrics.NotificationsProcessed.WithLabels(ServiceName, "failed").Inc();

            // The worker keeps running and keeps retrying. That is realistic
            // behaviour, and it is what makes the "healthy pod doing no work"
            // situation possible in the first place.
            _logger.LogError(
                ex,
                "Notification batch failed after {DurationMs}ms; dependency {Dependency} is unavailable",
                stopwatch.Elapsed.TotalMilliseconds, "workload-database");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            CorrelationContext.CurrentId = null;
        }
    }
}
