using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;

namespace KubeSage.Platform.UnitTests.Configuration;

// These tests protect the start-up contract of the platform.
//
// Every case below is a configuration mistake that would otherwise produce a
// confusing runtime symptom hours later: duplicate incidents, flapping
// recovery, an investigation claimed twice, or evidence silently truncated.
// Catching them at start-up is the whole reason the validator exists, so the
// tests are written around those failure modes rather than around fields.
public sealed class KubeSageOptionsValidatorTests
{
    private readonly KubeSageOptionsValidator _validator = new();

    [Fact]
    public void Default_configuration_is_valid()
    {
        // Arrange - the shipped appsettings.json values.
        var options = Valid();

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        result.Succeeded.ShouldBeTrue(
            $"the default configuration must start cleanly, but: {string.Join("; ", result.Failures ?? [])}");
    }

    [Fact]
    public void Recovery_confirmation_shorter_than_the_evaluation_window_is_rejected()
    {
        // Confirming recovery over less than one full window means the
        // detector decides an incident is over using data it has not finished
        // looking at, which makes incidents flap open and closed.
        var options = Valid() with
        {
            Detection = new DetectionOptions
            {
                EvaluationWindowMinutes = 10,
                RecoveryConfirmationMinutes = 5,
                DeduplicationCooldownMinutes = 15
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("RecoveryConfirmationMinutes"));
    }

    [Fact]
    public void Deduplication_cooldown_shorter_than_the_evaluation_window_is_rejected()
    {
        // A cooldown shorter than the window lets the same ongoing condition
        // raise a fresh incident on every pass of the detection loop.
        var options = Valid() with
        {
            Detection = new DetectionOptions
            {
                EvaluationWindowMinutes = 10,
                RecoveryConfirmationMinutes = 15,
                DeduplicationCooldownMinutes = 2
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("DeduplicationCooldownMinutes"));
    }

    [Fact]
    public void Evaluation_window_longer_than_the_maximum_query_range_is_rejected()
    {
        // The detector would ask for more data than the telemetry adapters are
        // allowed to return, then evaluate the truncated result as if it were
        // complete - under-reporting real problems.
        var options = Valid() with
        {
            Detection = new DetectionOptions { EvaluationWindowMinutes = 60 },
            Telemetry = ValidTelemetry() with { MaxQueryRangeMinutes = 30 }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("MaxQueryRangeMinutes"));
    }

    [Fact]
    public void Work_lease_shorter_than_the_investigation_timeout_is_rejected()
    {
        // If the lease expires while an investigation is still running, a
        // second worker claims the same incident and two reports are written
        // for one event.
        var options = Valid() with
        {
            Investigation = new InvestigationOptions
            {
                TimeoutSeconds = 1800,
                WorkLeaseSeconds = 600
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("WorkLeaseSeconds"));
    }

    [Fact]
    public void Investigation_budget_smaller_than_a_single_model_call_is_rejected()
    {
        // Three agents share the investigation budget. If the budget is
        // smaller than one model timeout, no investigation can ever finish.
        var options = Valid() with
        {
            Ollama = ValidOllama() with { RequestTimeoutSeconds = 600 },
            Investigation = new InvestigationOptions
            {
                TimeoutSeconds = 300,
                WorkLeaseSeconds = 3600
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("Investigation.TimeoutSeconds"));
    }

    [Fact]
    public void Workload_namespace_outside_the_allow_list_is_rejected()
    {
        // Kubernetes evidence collection would be blocked for the very
        // workload the platform exists to observe.
        var options = Valid() with
        {
            Telemetry = ValidTelemetry() with { WorkloadNamespace = "somewhere-else" }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("AllowedNamespaces"));
    }

    [Fact]
    public void Nested_section_attributes_are_actually_enforced()
    {
        // This is the specific bug the hand-written validator exists to
        // prevent: the built-in ValidateDataAnnotations only inspects the top
        // level object, so an impossible threshold two levels down would be
        // accepted silently.
        var options = Valid() with
        {
            Detection = new DetectionOptions
            {
                Thresholds = new DetectionThresholds { HttpErrorRate = 5.0 }
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("HttpErrorRate"));
    }

    [Fact]
    public void Non_http_endpoints_are_rejected()
    {
        var options = Valid() with
        {
            Telemetry = ValidTelemetry() with { LokiEndpoint = new Uri("ftp://loki:3100") }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("LokiEndpoint"));
    }

    [Fact]
    public void Migration_connection_string_falls_back_to_the_main_one_when_absent()
    {
        // Tests and throwaway databases use a single connection string. The
        // fallback keeps that simple case working without special casing.
        var database = new DatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=kubesage",
            MigrationConnectionString = null
        };

        database.EffectiveMigrationConnectionString.ShouldBe(database.ConnectionString);
    }

    // --- helpers ---------------------------------------------------------
    // A known-good options tree that each test mutates in exactly one way, so
    // a failure points at the rule under test rather than at setup noise.

    private static KubeSageOptions Valid() => new()
    {
        Database = new DatabaseOptions { ConnectionString = "Host=localhost;Database=kubesage" },
        Ollama = ValidOllama(),
        Telemetry = ValidTelemetry(),
        // Stated explicitly rather than relying on a default: there is no
        // built-in allow-list, because a default one could never be narrowed.
        Kubernetes = new KubernetesOptions
        {
            AllowedNamespaces = ["kubesage-demo", "kubesage-observability"]
        },
        Detection = new DetectionOptions(),
        Analysis = new AnalysisOptions(),
        Investigation = new InvestigationOptions(),
        Retrieval = new RetrievalOptions()
    };

    private static OllamaOptions ValidOllama() => new();

    private static TelemetryOptions ValidTelemetry() => new();
}
