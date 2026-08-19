using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Persistence;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Detection;

// Wiring for detection and the background loop that drives it.
internal static class DetectionModule
{
    public static IServiceCollection AddDetection(this IServiceCollection services)
    {
        // Rules are registered individually so the engine receives them all
        // through IEnumerable<IDetectionRule>. Adding a rule is one line here
        // and no change to the engine.
        services.AddSingleton<IDetectionRule, HttpErrorRateRule>();
        services.AddSingleton<IDetectionRule, LatencyRule>();
        services.AddSingleton<IDetectionRule, DependencyFailureRule>();
        services.AddSingleton<IDetectionRule, PodRestartRule>();
        services.AddSingleton<IDetectionRule, ReadinessRule>();
        services.AddSingleton<IDetectionRule, RepeatedErrorSignatureRule>();

        services.AddSingleton<IncidentRepository>();
        services.AddSingleton<WorkQueue>();
        services.AddSingleton<DetectionEngine>();

        services.AddHostedService<DetectionLoop>();
        services.AddHostedService<AnalysisScheduler>();

        return services;
    }
}

// Runs a detection pass on a fixed interval, for as long as the platform is up.
//
// This is one of the three autonomous triggers the project requires. Nobody
// asks it to run.
internal sealed class DetectionLoop : BackgroundService
{
    private readonly DetectionEngine _engine;
    private readonly DetectionOptions _options;
    private readonly ILogger<DetectionLoop> _logger;

    public DetectionLoop(
        DetectionEngine engine,
        IOptions<KubeSageOptions> options,
        ILogger<DetectionLoop> logger)
    {
        _engine = engine;
        _options = options.Value.Detection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Detection is disabled by configuration; no incidents will be raised");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds);

        _logger.LogInformation(
            "Detection loop started: evaluating a {WindowMinutes}m window every {IntervalSeconds}s",
            _options.EvaluationWindowMinutes, _options.EvaluationIntervalSeconds);

        // A short initial delay lets the telemetry adapters and the cluster
        // settle, so the first pass is not evaluated against a half-warm
        // system and does not report a start-up blip as an incident.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await _engine.RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything. A detection pass that throws
                // is a bug worth fixing, but stopping detection entirely would
                // silently blind the platform.
                _logger.LogError(ex, "Detection pass failed; the loop will continue");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        _logger.LogInformation("Detection loop stopped");
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

// Queues the startup report and the periodic analysis.
//
// These are the other two autonomous triggers. They are queued as durable work
// items rather than executed inline, so the platform behaves the same whether
// the model is available at that instant or comes back an hour later.
internal sealed class AnalysisScheduler : BackgroundService
{
    private readonly WorkQueue _workQueue;
    private readonly AnalysisOptions _options;
    private readonly ILogger<AnalysisScheduler> _logger;

    public AnalysisScheduler(
        WorkQueue workQueue,
        IOptions<KubeSageOptions> options,
        ILogger<AnalysisScheduler> logger)
    {
        _workQueue = workQueue;
        _options = options.Value.Analysis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RunStartupAnalysis)
        {
            // The warm-up exists because Loki and Prometheus have almost no
            // data immediately after the cluster starts. Producing a report
            // straight away would describe an empty system and say nothing.
            _logger.LogInformation(
                "Startup analysis will run after a {WarmupSeconds}s telemetry warm-up",
                _options.StartupWarmupSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.StartupWarmupSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The dedup key includes the process start time so a restart
            // produces a fresh startup report, while a duplicate scheduling
            // within the same run does not.
            await _workQueue.EnqueueAsync(
                WorkKind.StartupAnalysis,
                $"startup-{Environment.TickCount64 / 1000}",
                new { trigger = "startup", requestedAtUtc = DateTimeOffset.UtcNow },
                stoppingToken);
        }

        if (!_options.RunScheduledAnalysis)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.ScheduledIntervalSeconds);
        _logger.LogInformation("Scheduled analysis will run every {IntervalSeconds}s", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                // The window start is the dedup key, so two schedulers - or a
                // restart mid-window - cannot queue the same analysis twice.
                var windowKey = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _options.ScheduledIntervalSeconds;

                await _workQueue.EnqueueAsync(
                    WorkKind.ScheduledAnalysis,
                    $"window-{windowKey}",
                    new { trigger = "scheduled", requestedAtUtc = DateTimeOffset.UtcNow },
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not queue scheduled analysis; will try again next interval");
            }
        }
    }
}
