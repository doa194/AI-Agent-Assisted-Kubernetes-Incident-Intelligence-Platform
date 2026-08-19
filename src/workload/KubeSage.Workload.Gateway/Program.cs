using KubeSage.Workload.Shared;
using KubeSage.Workload.Shared.Logging;
using KubeSage.Workload.Shared.Metrics;
using System.Diagnostics;

// Gateway.
//
// The single entry point to the demo application. It creates the correlation
// identifier for each incoming request and forwards to the order API.
//
// Having a gateway matters for the investigation story: it is where user
// impact is visible. An error rate measured here means customers are
// affected, whereas the same errors deeper in the stack might be retried and
// never surface.

var builder = WebApplication.CreateBuilder(args);
builder.AddWorkloadDefaults("gateway");

builder.Services.AddTransient<CorrelationPropagationHandler>();

builder.Services
    .AddHttpClient("orders", client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["Dependencies:OrderApi"] ?? "http://order-api:8080");
        // Longer than the order API's own payment timeout, so that a payment
        // problem surfaces here as the order API's 503 rather than as a
        // gateway timeout that hides which service is really at fault.
        client.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddHttpMessageHandler<CorrelationPropagationHandler>();

var app = builder.Build();
app.UseWorkloadDefaults();

app.MapPost("/api/orders", (HttpContext context, CreateOrderRequest request) =>
    context.TrackOperationAsync("SubmitOrder", async () =>
        await ForwardAsync(context, HttpMethod.Post, "/orders", request)));

app.MapGet("/api/orders/{orderId}", (HttpContext context, string orderId) =>
    context.TrackOperationAsync("FetchOrder", async () =>
        await ForwardAsync(context, HttpMethod.Get, $"/orders/{orderId}", null)));

// Cast to Func rather than left as a bare lambda: a lambda whose only
// parameter is HttpContext is otherwise treated as a RequestDelegate, and the
// IResult it returns would be silently discarded instead of written to the
// response.
app.MapGet("/api/orders", (Func<HttpContext, Task<IResult>>)(context =>
    context.TrackOperationAsync("ListOrders", async () =>
        await ForwardAsync(context, HttpMethod.Get, "/orders", null))));

app.Run();

// Forwards a request to the order API and turns the outcome into a response,
// recording dependency metrics and a log line that names the dependency.
static async Task<IResult> ForwardAsync(
    HttpContext context,
    HttpMethod method,
    string path,
    object? body)
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Gateway");
    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

    var stopwatch = Stopwatch.StartNew();

    try
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();

        var outcome = response.IsSuccessStatusCode ? "success"
            : (int)response.StatusCode >= 500 ? "failure"
            : "rejected";

        WorkloadMetrics.DependencyDuration
            .WithLabels("gateway", "order-api", outcome)
            .Observe(stopwatch.Elapsed.TotalSeconds);

        if ((int)response.StatusCode >= 500)
        {
            WorkloadMetrics.DependencyFailures.WithLabels("gateway", "order-api", "http_error").Inc();

            logger.LogError(
                "Dependency {Dependency} returned {StatusCode} in {DurationMs}ms for {Path}",
                "order-api", (int)response.StatusCode, stopwatch.Elapsed.TotalMilliseconds, path);
        }

        // The downstream status and body are passed through unchanged so the
        // caller sees the real outcome rather than a translated one.
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (TaskCanceledException ex)
    {
        stopwatch.Stop();
        WorkloadMetrics.DependencyFailures.WithLabels("gateway", "order-api", "timeout").Inc();

        logger.LogError(
            ex,
            "Dependency {Dependency} timed out after {DurationMs}ms for {Path}",
            "order-api", stopwatch.Elapsed.TotalMilliseconds, path);

        return Results.Json(
            new { error = "upstream_timeout", dependency = "order-api" }, statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        stopwatch.Stop();
        WorkloadMetrics.DependencyFailures.WithLabels("gateway", "order-api", "connection").Inc();

        logger.LogError(
            ex,
            "Dependency {Dependency} is unreachable after {DurationMs}ms for {Path}",
            "order-api", stopwatch.Elapsed.TotalMilliseconds, path);

        return Results.Json(
            new { error = "upstream_unavailable", dependency = "order-api" }, statusCode: 503);
    }
}

public sealed record CreateOrderRequest(string CustomerId, decimal Amount, string Currency);
