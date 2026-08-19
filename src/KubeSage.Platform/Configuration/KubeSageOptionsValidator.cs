using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Configuration;

// Validates the whole configuration tree once, at start-up.
//
// Why this is written by hand rather than using the built-in
// ValidateDataAnnotations: that helper only checks the top-level object and
// would silently ignore every attribute on the nested sections, which is
// where all the real settings live.
//
// Beyond per-field attributes this also checks relationships BETWEEN
// settings. Those are the mistakes that actually hurt: each value looks
// reasonable on its own but the combination makes the platform misbehave in a
// way that is hard to spot later.
internal sealed class KubeSageOptionsValidator : IValidateOptions<KubeSageOptions>
{
    public ValidateOptionsResult Validate(string? name, KubeSageOptions options)
    {
        var failures = new List<string>();

        ValidateSection(options.Database, nameof(options.Database), failures);
        ValidateSection(options.Ollama, nameof(options.Ollama), failures);
        ValidateSection(options.Telemetry, nameof(options.Telemetry), failures);
        ValidateSection(options.Kubernetes, nameof(options.Kubernetes), failures);
        ValidateSection(options.Detection, nameof(options.Detection), failures);
        ValidateSection(options.Detection.Thresholds, "Detection.Thresholds", failures);
        ValidateSection(options.Analysis, nameof(options.Analysis), failures);
        ValidateSection(options.Investigation, nameof(options.Investigation), failures);
        ValidateSection(options.Retrieval, nameof(options.Retrieval), failures);

        ValidateRelationships(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSection(object section, string path, List<string> failures)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(section);

        if (Validator.TryValidateObject(section, context, results, validateAllProperties: true))
        {
            return;
        }

        foreach (var result in results)
        {
            var members = result.MemberNames.Any()
                ? string.Join(", ", result.MemberNames.Select(m => $"{path}.{m}"))
                : path;

            failures.Add($"{members}: {result.ErrorMessage}");
        }
    }

    private static void ValidateRelationships(KubeSageOptions options, List<string> failures)
    {
        RequireHttpEndpoint(options.Ollama.Endpoint, "Ollama.Endpoint", failures);
        RequireHttpEndpoint(options.Telemetry.LokiEndpoint, "Telemetry.LokiEndpoint", failures);
        RequireHttpEndpoint(options.Telemetry.PrometheusEndpoint, "Telemetry.PrometheusEndpoint", failures);

        // A detection rule cannot look at a window longer than the telemetry
        // adapters are permitted to fetch, or it would silently evaluate
        // truncated data and under-report problems.
        if (options.Detection.EvaluationWindowMinutes > options.Telemetry.MaxQueryRangeMinutes)
        {
            failures.Add(
                $"Detection.EvaluationWindowMinutes ({options.Detection.EvaluationWindowMinutes}) must not exceed " +
                $"Telemetry.MaxQueryRangeMinutes ({options.Telemetry.MaxQueryRangeMinutes}); " +
                "the detector would otherwise evaluate an incomplete window.");
        }

        // Recovery must be confirmed over at least one full evaluation window.
        // A shorter confirmation would mark an incident recovered based on
        // data the detector has not finished looking at, producing flapping.
        if (options.Detection.RecoveryConfirmationMinutes < options.Detection.EvaluationWindowMinutes)
        {
            failures.Add(
                $"Detection.RecoveryConfirmationMinutes ({options.Detection.RecoveryConfirmationMinutes}) must be at least " +
                $"Detection.EvaluationWindowMinutes ({options.Detection.EvaluationWindowMinutes}) to avoid flapping incidents.");
        }

        // The cooldown exists to stop the same condition raising an incident
        // on every pass of the detection loop.
        if (options.Detection.DeduplicationCooldownMinutes < options.Detection.EvaluationWindowMinutes)
        {
            failures.Add(
                $"Detection.DeduplicationCooldownMinutes ({options.Detection.DeduplicationCooldownMinutes}) must be at least " +
                $"Detection.EvaluationWindowMinutes ({options.Detection.EvaluationWindowMinutes}) or duplicate incidents will be raised.");
        }

        // Three agents run inside one investigation budget, and any single one
        // of them may take a full model timeout to answer.
        if (options.Investigation.TimeoutSeconds < options.Ollama.RequestTimeoutSeconds)
        {
            failures.Add(
                $"Investigation.TimeoutSeconds ({options.Investigation.TimeoutSeconds}) must be at least " +
                $"Ollama.RequestTimeoutSeconds ({options.Ollama.RequestTimeoutSeconds}); " +
                "otherwise the investigation budget expires before a single model call can finish.");
        }

        // Work must stay leased for longer than an investigation can run, or a
        // second worker would pick up work that is still being processed and
        // produce a duplicate report.
        if (options.Investigation.WorkLeaseSeconds < options.Investigation.TimeoutSeconds)
        {
            failures.Add(
                $"Investigation.WorkLeaseSeconds ({options.Investigation.WorkLeaseSeconds}) must be at least " +
                $"Investigation.TimeoutSeconds ({options.Investigation.TimeoutSeconds}); " +
                "a shorter lease allows a running investigation to be claimed twice.");
        }

        // Every namespace the telemetry layer targets must also be readable.
        if (!options.Kubernetes.AllowedNamespaces.Contains(options.Telemetry.WorkloadNamespace, StringComparer.Ordinal))
        {
            failures.Add(
                $"Kubernetes.AllowedNamespaces must include Telemetry.WorkloadNamespace ('{options.Telemetry.WorkloadNamespace}'), " +
                "otherwise Kubernetes evidence collection is blocked for the workload being observed.");
        }

        if (string.IsNullOrWhiteSpace(options.Database.ConnectionString))
        {
            failures.Add("Database.ConnectionString must be provided.");
        }
    }

    private static void RequireHttpEndpoint(Uri uri, string path, List<string> failures)
    {
        if (!uri.IsAbsoluteUri)
        {
            failures.Add($"{path} must be an absolute URI (got '{uri}').");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{path} must use http or https (got '{uri.Scheme}').");
        }
    }
}
