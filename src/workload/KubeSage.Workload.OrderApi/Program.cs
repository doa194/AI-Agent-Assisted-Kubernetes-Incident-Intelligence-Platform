using System.Data;
using System.Diagnostics;
using Dapper;
using KubeSage.Workload.OrderApi;
using KubeSage.Workload.Shared;
using KubeSage.Workload.Shared.Logging;
using KubeSage.Workload.Shared.Metrics;
using Npgsql;

// Order API.
//
// The centre of the demo workload. It is the only service with two
// dependencies - the payment simulator and the workload database - which is
// what makes it useful: when it starts failing, the interesting question is
// WHICH dependency caused it, and that is exactly the question the
// investigation agent has to answer.

var builder = WebApplication.CreateBuilder(args);
builder.AddWorkloadDefaults("order-api");

var connectionString = builder.Configuration.GetConnectionString("WorkloadDatabase")
                       ?? throw new InvalidOperationException(
                           "ConnectionStrings__WorkloadDatabase must be configured.");

builder.Services.AddSingleton(new OrderRepository(connectionString));

// Timeout is short on purpose. When the payment simulator slows down, the
// order API must fail fast and surface the problem as its own 5xx responses
// rather than hanging and quietly exhausting its own thread pool. This is
// what makes the payment-latency scenario visible in the metrics.
builder.Services
    .AddHttpClient("payments", client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["Dependencies:PaymentSimulator"] ?? "http://payment-simulator:8080");
        client.Timeout = TimeSpan.FromSeconds(2);
    })
    .AddHttpMessageHandler<CorrelationPropagationHandler>();

builder.Services.AddTransient<CorrelationPropagationHandler>();

var app = builder.Build();
app.UseWorkloadDefaults();

var repository = app.Services.GetRequiredService<OrderRepository>();

app.MapPost("/orders", (HttpContext context, CreateOrderRequest request) =>
    context.TrackOperationAsync("CreateOrder", async () =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OrderApi");
        var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();

        if (request.Amount <= 0)
        {
            // A client mistake, not a service problem. Returned as 400 so it
            // never inflates the 5xx error rate the detection rules watch.
            return Results.BadRequest(new { error = "amount_must_be_positive" });
        }

        var orderId = $"ord_{Guid.NewGuid():n}"[..16];

        // --- Dependency 1: the payment provider ---
        var paymentOutcome = await CallPaymentAsync(
            httpClientFactory, logger, orderId, request.Amount, request.Currency);

        if (paymentOutcome.Kind == PaymentOutcomeKind.Declined)
        {
            return Results.Json(new { orderId, status = "declined" }, statusCode: 402);
        }

        if (paymentOutcome.Kind != PaymentOutcomeKind.Approved)
        {
            // The failure is attributed to the dependency by name in the log
            // line. That attribution is the single most valuable piece of
            // evidence for root-cause analysis of this workload.
            return Results.Json(
                new { orderId, status = "payment_unavailable", dependency = "payment-simulator" },
                statusCode: 503);
        }

        // --- Dependency 2: the workload database ---
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await repository.CreateOrderAsync(
                orderId, request.CustomerId, request.Amount, request.Currency, paymentOutcome.AuthorisationId!);

            stopwatch.Stop();
            WorkloadMetrics.DependencyDuration
                .WithLabels("order-api", "workload-database", "success")
                .Observe(stopwatch.Elapsed.TotalSeconds);

            logger.LogInformation(
                "Order {OrderId} persisted for customer {CustomerId} in {DurationMs}ms via {Dependency}",
                orderId, request.CustomerId, stopwatch.Elapsed.TotalMilliseconds, "workload-database");

            return Results.Ok(new { orderId, status = "created", authorisationId = paymentOutcome.AuthorisationId });
        }
        catch (NpgsqlException ex)
        {
            stopwatch.Stop();
            WorkloadMetrics.DependencyDuration
                .WithLabels("order-api", "workload-database", "failure")
                .Observe(stopwatch.Elapsed.TotalSeconds);
            WorkloadMetrics.DependencyFailures
                .WithLabels("order-api", "workload-database", "connection")
                .Inc();

            logger.LogError(
                ex,
                "Order {OrderId} could not be persisted after {DurationMs}ms; dependency {Dependency} is unavailable",
                orderId, stopwatch.Elapsed.TotalMilliseconds, "workload-database");

            return Results.Json(
                new { orderId, status = "storage_unavailable", dependency = "workload-database" },
                statusCode: 503);
        }
    }));

app.MapGet("/orders/{orderId}", (HttpContext context, string orderId) =>
    context.TrackOperationAsync("GetOrder", async () =>
    {
        var order = await repository.GetOrderAsync(orderId);
        return order is null ? Results.NotFound(new { orderId }) : Results.Ok(order);
    }));

// Cast to Func rather than left as a bare lambda: a lambda whose only
// parameter is HttpContext is otherwise treated as a RequestDelegate, and the
// IResult it returns would be silently discarded instead of written to the
// response.
app.MapGet("/orders", (Func<HttpContext, Task<IResult>>)(context =>
    context.TrackOperationAsync("ListRecentOrders", async () =>
        Results.Ok(await repository.GetRecentOrdersAsync(20)))));

app.Run();

// Calls the payment simulator and turns every possible outcome into a single
// typed result, recording the dependency metrics along the way.
static async Task<PaymentOutcome> CallPaymentAsync(
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    string orderId,
    decimal amount,
    string currency)
{
    var client = httpClientFactory.CreateClient("payments");
    var stopwatch = Stopwatch.StartNew();

    try
    {
        var response = await client.PostAsJsonAsync(
            "/payments", new { orderId, amount, currency });

        stopwatch.Stop();

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<PaymentSimulatorResponse>();

            WorkloadMetrics.DependencyDuration
                .WithLabels("order-api", "payment-simulator", "success")
                .Observe(stopwatch.Elapsed.TotalSeconds);

            logger.LogInformation(
                "Payment for {OrderId} approved by {Dependency} in {DurationMs}ms",
                orderId, "payment-simulator", stopwatch.Elapsed.TotalMilliseconds);

            return new PaymentOutcome(PaymentOutcomeKind.Approved, body?.AuthorisationId);
        }

        if ((int)response.StatusCode == StatusCodes.Status402PaymentRequired)
        {
            WorkloadMetrics.DependencyDuration
                .WithLabels("order-api", "payment-simulator", "declined")
                .Observe(stopwatch.Elapsed.TotalSeconds);

            logger.LogInformation("Payment for {OrderId} was declined by {Dependency}", orderId, "payment-simulator");
            return new PaymentOutcome(PaymentOutcomeKind.Declined, null);
        }

        WorkloadMetrics.DependencyDuration
            .WithLabels("order-api", "payment-simulator", "failure")
            .Observe(stopwatch.Elapsed.TotalSeconds);
        WorkloadMetrics.DependencyFailures
            .WithLabels("order-api", "payment-simulator", "http_error")
            .Inc();

        logger.LogError(
            "Dependency {Dependency} returned {StatusCode} for {OrderId} after {DurationMs}ms",
            "payment-simulator", (int)response.StatusCode, orderId, stopwatch.Elapsed.TotalMilliseconds);

        return new PaymentOutcome(PaymentOutcomeKind.Error, null);
    }
    catch (TaskCanceledException ex)
    {
        // The HttpClient timeout fired. This is the signature of the
        // payment-latency scenario, so it is logged distinctly from a
        // connection refusal.
        stopwatch.Stop();

        WorkloadMetrics.DependencyDuration
            .WithLabels("order-api", "payment-simulator", "timeout")
            .Observe(stopwatch.Elapsed.TotalSeconds);
        WorkloadMetrics.DependencyFailures
            .WithLabels("order-api", "payment-simulator", "timeout")
            .Inc();

        logger.LogError(
            ex,
            "Dependency {Dependency} timed out after {DurationMs}ms while processing {OrderId}",
            "payment-simulator", stopwatch.Elapsed.TotalMilliseconds, orderId);

        return new PaymentOutcome(PaymentOutcomeKind.Timeout, null);
    }
    catch (HttpRequestException ex)
    {
        stopwatch.Stop();

        WorkloadMetrics.DependencyFailures
            .WithLabels("order-api", "payment-simulator", "connection")
            .Inc();

        logger.LogError(
            ex,
            "Dependency {Dependency} is unreachable ({DurationMs}ms) while processing {OrderId}",
            "payment-simulator", stopwatch.Elapsed.TotalMilliseconds, orderId);

        return new PaymentOutcome(PaymentOutcomeKind.Unreachable, null);
    }
}

public sealed record CreateOrderRequest(string CustomerId, decimal Amount, string Currency);

public sealed record PaymentSimulatorResponse(string OrderId, string Status, string? AuthorisationId);

public enum PaymentOutcomeKind
{
    Approved,
    Declined,
    Error,
    Timeout,
    Unreachable
}

public sealed record PaymentOutcome(PaymentOutcomeKind Kind, string? AuthorisationId);
