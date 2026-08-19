using Prometheus;

namespace KubeSage.Workload.Shared.Metrics;

// The metrics every demo service publishes.
//
// Label cardinality is kept deliberately low. Every distinct combination of
// label values becomes its own time series in Prometheus, so putting a
// correlation identifier, an order identifier or a raw URL path in a label
// would create an unbounded number of series and eventually take Prometheus
// down. Those high-cardinality details belong in logs, which is exactly where
// they are written.
//
// The detection rules in the AI platform read these series, so the names and
// labels here are a contract with the detection layer.
public static class WorkloadMetrics
{
    // Operation is a short logical name such as "CreateOrder", never a URL
    // containing identifiers.
    public static readonly Counter HttpRequests = Prometheus.Metrics.CreateCounter(
        "kubesage_http_requests_total",
        "HTTP requests handled, labelled by logical operation and status class.",
        new CounterConfiguration
        {
            LabelNames = ["service", "operation", "status_class"]
        });

    public static readonly Histogram HttpRequestDuration = Prometheus.Metrics.CreateHistogram(
        "kubesage_http_request_duration_seconds",
        "How long this service took to handle a request.",
        new HistogramConfiguration
        {
            LabelNames = ["service", "operation"],
            // Buckets span the range that matters for this workload: a healthy
            // request is tens of milliseconds, and the payment latency
            // scenario pushes responses past two seconds.
            Buckets = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0]
        });

    public static readonly Histogram DependencyDuration = Prometheus.Metrics.CreateHistogram(
        "kubesage_dependency_duration_seconds",
        "How long a call to a downstream dependency took.",
        new HistogramConfiguration
        {
            LabelNames = ["service", "dependency", "outcome"],
            Buckets = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0]
        });

    public static readonly Counter DependencyFailures = Prometheus.Metrics.CreateCounter(
        "kubesage_dependency_failures_total",
        "Failed calls to a downstream dependency, labelled by failure kind.",
        new CounterConfiguration
        {
            LabelNames = ["service", "dependency", "kind"]
        });

    // Business-level progress for the background worker. A worker that is
    // running but processing nothing looks healthy to Kubernetes, so this is
    // the only signal that shows the difference.
    public static readonly Counter NotificationsProcessed = Prometheus.Metrics.CreateCounter(
        "kubesage_notifications_processed_total",
        "Notifications the worker has finished processing.",
        new CounterConfiguration
        {
            LabelNames = ["service", "outcome"]
        });

    public static readonly Gauge PendingNotifications = Prometheus.Metrics.CreateGauge(
        "kubesage_notifications_pending",
        "Notifications waiting to be processed.",
        new GaugeConfiguration
        {
            LabelNames = ["service"]
        });

    // Groups 2xx/4xx/5xx rather than recording every status code, which keeps
    // the error-rate query simple and the series count small.
    public static string StatusClass(int statusCode) => statusCode switch
    {
        >= 500 => "5xx",
        >= 400 => "4xx",
        >= 300 => "3xx",
        >= 200 => "2xx",
        _ => "other"
    };
}
