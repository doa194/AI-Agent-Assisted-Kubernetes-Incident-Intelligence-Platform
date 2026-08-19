using System.Diagnostics;
using System.Net.Http.Json;
using KubeSage.Workload.Shared.Logging;
using Microsoft.Extensions.Options;

namespace KubeSage.Workload.TrafficGenerator;

// Drives a steady stream of requests through the gateway.
//
// The mix is intentional. Most requests are ordinary order submissions, but a
// small share deliberately exercise error paths (an invalid amount, a lookup
// for an order that does not exist). That background of harmless 4xx activity
// is what forces the detection rules to distinguish "some requests are being
// rejected, which is normal" from "the service is failing", instead of
// firing on the first non-200 response.
public sealed class TrafficLoop : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrafficOptions _options;
    private readonly ILogger<TrafficLoop> _logger;

    private static readonly string[] Customers =
        ["cust_anna", "cust_bo", "cust_chen", "cust_dara", "cust_evan", "cust_farah"];

    public TrafficLoop(
        IHttpClientFactory httpClientFactory,
        IOptions<TrafficOptions> options,
        ILogger<TrafficLoop> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the rest of the workload to become ready. Hammering
        // services that are still starting would fill the logs with
        // connection errors that look like a real incident on every deploy.
        await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);

        _logger.LogInformation(
            "Traffic generator started at {RequestsPerMinute} requests per minute",
            _options.RequestsPerMinute);

        var interval = TimeSpan.FromMilliseconds(60_000.0 / Math.Max(1, _options.RequestsPerMinute));

        while (!stoppingToken.IsCancellationRequested)
        {
            await SendOneAsync(stoppingToken);

            try
            {
                // Jitter stops every request landing on an exact interval,
                // which would produce unnaturally regular metric shapes.
                var jitter = Random.Shared.Next(-150, 150);
                var wait = interval + TimeSpan.FromMilliseconds(jitter);
                await Task.Delay(wait > TimeSpan.Zero ? wait : interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendOneAsync(CancellationToken stoppingToken)
    {
        // The generator starts each request's correlation identifier. The
        // gateway accepts it and passes it on, so one identifier covers the
        // whole path through the system.
        CorrelationContext.CurrentId = CorrelationContext.NewId();

        var client = _httpClientFactory.CreateClient("gateway");
        var stopwatch = Stopwatch.StartNew();
        var roll = Random.Shared.NextDouble();

        try
        {
            HttpResponseMessage response;
            string operation;

            if (roll < _options.InvalidRequestShare)
            {
                // A malformed request. Should be answered with 400 and must
                // never count towards the server error rate.
                operation = "InvalidOrder";
                response = await client.PostAsJsonAsync(
                    "/api/orders",
                    new { customerId = PickCustomer(), amount = -5m, currency = "EUR" },
                    stoppingToken);
            }
            else if (roll < _options.InvalidRequestShare + _options.LookupShare)
            {
                operation = "LookupOrder";
                response = await client.GetAsync("/api/orders", stoppingToken);
            }
            else
            {
                operation = "SubmitOrder";
                response = await client.PostAsJsonAsync(
                    "/api/orders",
                    new
                    {
                        customerId = PickCustomer(),
                        amount = Math.Round((decimal)(Random.Shared.NextDouble() * 240 + 10), 2),
                        currency = "EUR"
                    },
                    stoppingToken);
            }

            stopwatch.Stop();
            var status = (int)response.StatusCode;
            response.Dispose();

            if (status >= 500)
            {
                _logger.LogWarning(
                    "{Operation} received {StatusCode} from {Dependency} after {DurationMs}ms",
                    operation, status, "gateway", stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation(
                    "{Operation} received {StatusCode} from {Dependency} after {DurationMs}ms",
                    operation, status, "gateway", stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Traffic request to {Dependency} failed after {DurationMs}ms",
                "gateway", stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            CorrelationContext.CurrentId = null;
        }
    }

    private static string PickCustomer() => Customers[Random.Shared.Next(Customers.Length)];
}

public sealed class TrafficOptions
{
    // Roughly one request every two seconds by default. Enough to give the
    // detection rules a meaningful sample within a five minute window without
    // putting real load on a laptop.
    public int RequestsPerMinute { get; set; } = 30;

    public int StartupDelaySeconds { get; set; } = 20;

    // Share of requests that are deliberately invalid (answered 400).
    public double InvalidRequestShare { get; set; } = 0.05;

    // Share of requests that are read-only lookups.
    public double LookupShare { get; set; } = 0.20;
}
