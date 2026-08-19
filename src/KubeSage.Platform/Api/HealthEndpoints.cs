using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KubeSage.Platform.Api;

// The two health endpoints an operator and a container runtime rely on.
//
// The split matters:
//   /health/live  - the process is running. Never checks dependencies, so a
//                   database outage does not cause a restart loop that would
//                   destroy in-flight work.
//   /health/ready - the platform can actually do its job right now. Checks
//                   are tagged "ready" when they represent something the
//                   platform cannot work without.
internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // Run no checks at all - reaching this handler is the answer.
            Predicate = _ => false,
            ResponseWriter = WriteResponseAsync
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteResponseAsync
        });

        // Every check, including the ones deliberately excluded from
        // readiness.
        //
        // This exists because "ready" and "fully working" are different
        // states, and without this endpoint the difference is invisible. With
        // Ollama down the platform is correctly Ready - it still detects and
        // records incidents - but investigations are only being queued, not
        // run. An operator needs to be able to see that, and readiness is the
        // wrong place to tell them: putting it there would take the platform
        // out of rotation over a degradation it is designed to survive.
        //
        // Always returns 200. It is a status report, not a probe; a
        // monitoring system reads the body.
        endpoints.MapHealthChecks("/health/detail", new HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = WriteResponseAsync,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status200OK
            }
        });

        return endpoints;
    }

    // Returns a small JSON body naming each check and why it failed, so a
    // failing readiness probe is self-explanatory without reading logs.
    private static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                error = entry.Value.Exception?.Message,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1)
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
