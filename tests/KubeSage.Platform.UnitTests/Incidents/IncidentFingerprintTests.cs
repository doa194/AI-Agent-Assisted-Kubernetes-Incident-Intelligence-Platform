using KubeSage.Platform.Modules.Incidents;

namespace KubeSage.Platform.UnitTests.Incidents;

// The fingerprint decides whether an observation is "the same problem again"
// or "a new problem". Both mistakes are expensive:
//
//   too coarse -> a genuinely different incident is swallowed as a duplicate
//                 and never investigated;
//   too fine   -> one ongoing outage raises a new incident every minute, and
//                 each one queues its own multi-minute investigation.
public sealed class IncidentFingerprintTests
{
    [Fact]
    public void The_same_condition_produces_the_same_fingerprint()
    {
        // The detection loop re-evaluates every minute. A persisting condition
        // must keep producing one fingerprint, or deduplication cannot work.
        var first = IncidentFingerprint.Create(
            IncidentCategory.DependencyUnavailable, "kubesage-demo", ["order-api"]);

        var second = IncidentFingerprint.Create(
            IncidentCategory.DependencyUnavailable, "kubesage-demo", ["order-api"]);

        first.ShouldBe(second);
    }

    [Fact]
    public void Workload_order_does_not_change_the_fingerprint()
    {
        // Prometheus and Kubernetes return workloads in whatever order they
        // like. Without sorting, the same incident would alternate between two
        // fingerprints and duplicate itself.
        var forwards = IncidentFingerprint.Create(
            IncidentCategory.DependencyUnavailable, "kubesage-demo", ["order-api", "notification-worker"]);

        var backwards = IncidentFingerprint.Create(
            IncidentCategory.DependencyUnavailable, "kubesage-demo", ["notification-worker", "order-api"]);

        forwards.ShouldBe(backwards);
    }

    [Fact]
    public void Duplicate_workload_entries_are_ignored()
    {
        var withDuplicate = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["gateway", "gateway"]);

        var without = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["gateway"]);

        withDuplicate.ShouldBe(without);
    }

    [Fact]
    public void Different_categories_are_different_incidents()
    {
        // A pod that is out of memory and a pod that is failing readiness need
        // completely different responses, even on the same workload.
        var oom = IncidentFingerprint.Create(
            IncidentCategory.OutOfMemory, "kubesage-demo", ["payment-simulator"]);

        var readiness = IncidentFingerprint.Create(
            IncidentCategory.ReadinessFailure, "kubesage-demo", ["payment-simulator"]);

        oom.ShouldNotBe(readiness);
    }

    [Fact]
    public void Different_workloads_are_different_incidents()
    {
        var orderApi = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["order-api"]);

        var gateway = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["gateway"]);

        orderApi.ShouldNotBe(gateway);
    }

    [Fact]
    public void Different_error_signatures_are_different_incidents()
    {
        // Two unrelated errors from one service are two problems. Merging them
        // would mean only the first is ever investigated.
        var timeout = IncidentFingerprint.Create(
            IncidentCategory.RepeatedErrorSignature, "kubesage-demo", ["order-api"], "sig-timeout");

        var database = IncidentFingerprint.Create(
            IncidentCategory.RepeatedErrorSignature, "kubesage-demo", ["order-api"], "sig-database");

        timeout.ShouldNotBe(database);
    }

    [Fact]
    public void Measured_values_do_not_affect_the_fingerprint()
    {
        // Error rate and latency change on every evaluation. If they were part
        // of the identity, a persisting outage would produce a brand new
        // incident every single pass - the exact flood this exists to stop.
        //
        // The fingerprint takes no measurement as input, so this is verified by
        // showing that two calls describing the same condition agree.
        var atOnePercent = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["gateway"]);

        var atNinetyPercent = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["gateway"]);

        atOnePercent.ShouldBe(atNinetyPercent);
    }

    [Fact]
    public void Namespaces_are_isolated_from_each_other()
    {
        var demo = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-demo", ["gateway"]);

        var other = IncidentFingerprint.Create(
            IncidentCategory.HttpErrorRate, "kubesage-observability", ["gateway"]);

        demo.ShouldNotBe(other);
    }

    [Fact]
    public void Fingerprints_are_short_and_stable()
    {
        // Stored on every incident row and compared on every detection pass,
        // so it stays short. Stability across runs is what makes deduplication
        // survive a restart.
        var fingerprint = IncidentFingerprint.Create(
            IncidentCategory.PodRestartLoop, "kubesage-demo", ["order-api"], "CrashLoopBackOff");

        fingerprint.Length.ShouldBe(20);
        fingerprint.ShouldBe(IncidentFingerprint.Create(
            IncidentCategory.PodRestartLoop, "kubesage-demo", ["order-api"], "CrashLoopBackOff"));
    }
}
