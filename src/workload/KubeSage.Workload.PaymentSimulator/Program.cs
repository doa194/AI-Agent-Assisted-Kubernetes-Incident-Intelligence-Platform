using KubeSage.Workload.Shared;
using KubeSage.Workload.Shared.Faults;
using KubeSage.Workload.Shared.Metrics;

// Payment Simulator.
//
// Stands in for a slow, occasionally failing third-party payment provider. It
// holds no state and has no database, so when this service misbehaves the
// cause is unambiguous - which makes it the right place to inject the
// "downstream dependency became slow" scenario.
//
// The business logic is intentionally trivial. The point of this service is
// the operational behaviour it produces, not what it computes.

var builder = WebApplication.CreateBuilder(args);
builder.AddWorkloadDefaults("payment-simulator");

var app = builder.Build();
app.UseWorkloadDefaults();

var faults = app.Services.GetRequiredService<FaultSettings>();

// Baseline behaviour when no fault is active: a small amount of natural
// variation so that latency percentiles look like a real service rather than
// a flat line. Without this, a p95 latency rule would have nothing sensible
// to measure during healthy operation.
const int baselineMinMs = 15;
const int baselineMaxMs = 90;

// A low, steady rate of genuinely declined payments. This is normal business
// behaviour, not a fault: it gives the detection rules a realistic background
// level of 4xx responses to distinguish from a real incident.
const double naturalDeclineRate = 0.03;

app.MapPost("/payments", (HttpContext context, PaymentRequest request) =>
    context.TrackOperationAsync("ProcessPayment", async () =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PaymentSimulator");

        var delay = Random.Shared.Next(baselineMinMs, baselineMaxMs) + faults.LatencyMilliseconds;

        if (faults.LatencyMilliseconds > 0)
        {
            logger.LogWarning(
                "Payment provider is responding slowly: {DelayMs}ms for amount {Amount}",
                delay, request.Amount);
        }

        await Task.Delay(delay);

        // Injected failures are reported as 500 because that is what a broken
        // provider looks like to a caller. A declined card is a 402 instead,
        // because that is a normal outcome and must not be counted as an
        // error by the detection rules.
        if (faults.ErrorRate > 0 && Random.Shared.NextDouble() < faults.ErrorRate)
        {
            logger.LogError(
                "Payment provider returned an internal error for amount {Amount}", request.Amount);
            return Results.Json(new { status = "provider_error" }, statusCode: 500);
        }

        if (Random.Shared.NextDouble() < naturalDeclineRate)
        {
            logger.LogInformation("Payment declined for amount {Amount}", request.Amount);
            return Results.Json(
                new PaymentResponse(request.OrderId, "declined", null), statusCode: 402);
        }

        var authorisation = $"auth_{Guid.NewGuid():n}"[..20];
        logger.LogInformation(
            "Payment approved for amount {Amount} with authorisation {AuthorisationId}",
            request.Amount, authorisation);

        return Results.Ok(new PaymentResponse(request.OrderId, "approved", authorisation));
    }));

app.Run();

public sealed record PaymentRequest(string OrderId, decimal Amount, string Currency);

public sealed record PaymentResponse(string OrderId, string Status, string? AuthorisationId);
