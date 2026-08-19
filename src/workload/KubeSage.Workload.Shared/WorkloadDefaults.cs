using System.Diagnostics;
using KubeSage.Workload.Shared.Faults;
using KubeSage.Workload.Shared.Logging;
using KubeSage.Workload.Shared.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Prometheus;

namespace KubeSage.Workload.Shared;

// One place that configures everything every demo service needs: identical
// log format, correlation handling, fault injection, metrics and health
// endpoints.
//
// Sharing this matters more than it might look. The detection rules in the AI
// platform parse one log shape and query one set of metric names; if each
// service wired its own slightly different version, detection would work for
// some services and silently miss others.
public static class WorkloadDefaults
{
    public static WebApplicationBuilder AddWorkloadDefaults(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        var logContext = new WorkloadLogContext
        {
            ServiceName = serviceName,
            // Supplied by the Kubernetes downward API in the deployment
            // manifests, so a log line can be traced back to one replica.
            PodName = Environment.GetEnvironmentVariable("POD_NAME")
        };

        builder.Services.AddSingleton(logContext);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = WorkloadLogFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<WorkloadLogFormatter, ConsoleFormatterOptions>(
            options => options.IncludeScopes = false);

        // Quieten the framework.
        //
        // This is not cosmetic. Left at Information, ASP.NET Core writes
        // several lines per request ("Executed endpoint", "Writing value of
        // type ... as Json") that carry no operational meaning. Those lines
        // would be stored in Loki, counted by detection rules looking for
        // repeated signatures, and eventually shown to a model as evidence -
        // crowding out the lines that actually describe what happened.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        // Fault configuration is read once at start-up. Changing a scenario
        // means patching the deployment, which restarts the pod.
        var faults = FaultSettings.FromEnvironment();
        builder.Services.AddSingleton(faults);
        builder.Services.AddSingleton<ReadinessState>(_ => new ReadinessState(faults));
        builder.Services.AddHostedService<FaultRunner>();

        builder.Services.AddSingleton(new ServiceIdentity(serviceName));

        return builder;
    }

    public static WebApplication UseWorkloadDefaults(this WebApplication app)
    {
        app.UseMiddleware<CorrelationMiddleware>();

        // Kubernetes probes. Liveness stays healthy even when a readiness
        // fault is active: the readiness-failure scenario is about the pod
        // being removed from the Service, not about it being restarted.
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
            .ExcludeFromDescription();

        app.MapGet("/health/ready", (ReadinessState readiness, ILoggerFactory loggers) =>
            {
                if (readiness.IsReady)
                {
                    return Results.Ok(new { status = "ready" });
                }

                // Say WHY in the logs, not only in the probe response.
                //
                // Kubernetes shows a pod as NotReady, but nothing explains the
                // cause anywhere the platform can see: it reads logs, metrics
                // and cluster state, never an HTTP body. Without this line a
                // readiness failure produces no log evidence at all, and an
                // investigation is left with "not ready" and no reason.
                //
                // Throttled, because the probe runs every few seconds and an
                // unready pod would otherwise fill Loki with one message.
                if (readiness.ShouldLogUnready())
                {
                    loggers.CreateLogger("Workload.Readiness").LogWarning(
                        "Readiness probe is failing: {Reason}. The process is running but reports it " +
                        "cannot serve traffic, so Kubernetes has removed it from the Service endpoints.",
                        readiness.Reason);
                }

                return Results.Json(new { status = "not-ready", reason = readiness.Reason }, statusCode: 503);
            })
            .ExcludeFromDescription();

        // Prometheus scrapes this. The path is referenced by the scrape
        // annotations in the deployment manifests.
        app.MapMetrics("/metrics");

        return app;
    }

    // Times an operation, records the two HTTP metrics and writes one
    // structured log line describing the outcome.
    //
    // Every service uses this for its request handling so that a single
    // LogQL or PromQL query works across the whole workload.
    public static async Task<IResult> TrackOperationAsync(
        this HttpContext context,
        string operation,
        Func<Task<IResult>> handler)
    {
        var identity = context.RequestServices.GetRequiredService<ServiceIdentity>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger($"Workload.{operation}");

        var stopwatch = Stopwatch.StartNew();
        var statusCode = StatusCodes.Status200OK;

        try
        {
            var result = await handler();
            statusCode = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;

            stopwatch.Stop();
            Record(identity.Name, operation, statusCode, stopwatch.Elapsed);

            if (statusCode >= 500)
            {
                logger.LogError(
                    "{Operation} failed with status {StatusCode} in {DurationMs}ms",
                    operation, statusCode, stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                logger.LogInformation(
                    "{Operation} completed with status {StatusCode} in {DurationMs}ms",
                    operation, statusCode, stopwatch.Elapsed.TotalMilliseconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Record(identity.Name, operation, StatusCodes.Status500InternalServerError, stopwatch.Elapsed);

            logger.LogError(
                ex,
                "{Operation} threw {ErrorKind} after {DurationMs}ms",
                operation, ex.GetType().Name, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(new { error = "internal_error", operation }, statusCode: 500);
        }
    }

    private static void Record(string service, string operation, int statusCode, TimeSpan elapsed)
    {
        WorkloadMetrics.HttpRequests
            .WithLabels(service, operation, WorkloadMetrics.StatusClass(statusCode))
            .Inc();

        WorkloadMetrics.HttpRequestDuration
            .WithLabels(service, operation)
            .Observe(elapsed.TotalSeconds);
    }
}

// The name this service reports in logs and metric labels.
public sealed record ServiceIdentity(string Name);

// Whether the readiness probe should report success.
//
// Kept as its own object rather than a bare boolean because the reason is
// reported in the probe response, which is what makes the readiness-failure
// scenario diagnosable from the cluster alone.
public sealed class ReadinessState
{
    private volatile bool _ready;

    public ReadinessState(FaultSettings faults)
    {
        _ready = !faults.Unready;
        Reason = faults.Unready ? "readiness fault injected" : string.Empty;
    }

    public bool IsReady => _ready;

    public string Reason { get; private set; }

    public void MarkUnready(string reason)
    {
        Reason = reason;
        _ready = false;
    }

    public void MarkReady()
    {
        Reason = string.Empty;
        _ready = true;
    }

    // True at most once a minute, so an unready pod explains itself often
    // enough to land inside any detection window without flooding the logs.
    // The probe runs every few seconds; logging every failure would produce
    // hundreds of identical lines an hour.
    public bool ShouldLogUnready()
    {
        var now = DateTimeOffset.UtcNow;

        lock (_logGate)
        {
            if (now - _lastUnreadyLog < TimeSpan.FromMinutes(1))
            {
                return false;
            }

            _lastUnreadyLog = now;
            return true;
        }
    }

    private readonly Lock _logGate = new();
    private DateTimeOffset _lastUnreadyLog = DateTimeOffset.MinValue;
}
