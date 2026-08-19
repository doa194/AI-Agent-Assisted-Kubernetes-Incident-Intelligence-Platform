using System.Globalization;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Kubernetes;
using KubeSage.Platform.Modules.Telemetry;

namespace KubeSage.Platform.Modules.Detection;

// The observations one evaluation pass gathers, before any rule looks at them.
//
// Collecting first and evaluating afterwards keeps the rules pure functions of
// their input, which is what makes them cheap and meaningful to unit test.
public sealed record DetectionSnapshot
{
    public required DateTimeOffset EvaluatedAtUtc { get; init; }
    public required TimeSpan Window { get; init; }
    public required string Namespace { get; init; }

    public IReadOnlyList<ServiceRate> ErrorRates { get; init; } = [];
    public IReadOnlyDictionary<string, double> LatencyP95 { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<DependencyFailure> DependencyFailures { get; init; } = [];
    public IReadOnlyList<DependencyLatency> DependencyLatencies { get; init; } = [];
    public IReadOnlyList<PodRestartInfo> Pods { get; init; } = [];
    public IReadOnlyList<Evidence> LogSignatures { get; init; } = [];

    // Restart counts from the previous pass, so a rule can tell "restarted
    // twice ever" from "restarted twice in the last five minutes". Without
    // this, a pod that crash-looped yesterday would keep raising incidents.
    public IReadOnlyDictionary<string, int> PreviousRestartCounts { get; init; }
        = new Dictionary<string, int>();
}

// A single deterministic rule.
//
// No rule may call a model, and no rule may perform I/O. They receive a
// snapshot and return candidates. That restriction is what lets detection keep
// working when Ollama is down, which is a stated requirement of the project.
public interface IDetectionRule
{
    string Name { get; }

    IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options);
}

// --------------------------------------------------------------------------

// Fires when too large a share of a service's requests fail with 5xx.
public sealed class HttpErrorRateRule : IDetectionRule
{
    public string Name => "http-error-rate";

    public IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options)
    {
        foreach (var rate in snapshot.ErrorRates)
        {
            // A small sample makes a ratio meaningless: one failure out of two
            // is a 50% error rate, and would page someone at three in the
            // morning over nothing.
            if (rate.TotalRequests < options.Thresholds.MinimumRequestSample)
            {
                continue;
            }

            if (rate.Ratio < options.Thresholds.HttpErrorRate)
            {
                continue;
            }

            var severity = rate.Ratio switch
            {
                >= 0.50 => IncidentSeverity.Critical,
                >= 0.25 => IncidentSeverity.High,
                _ => IncidentSeverity.Medium
            };

            yield return new IncidentCandidate
            {
                Fingerprint = IncidentFingerprint.Create(
                    IncidentCategory.HttpErrorRate, snapshot.Namespace, [rate.Service]),
                Category = IncidentCategory.HttpErrorRate,
                Severity = severity,
                Title = $"{rate.Service} is returning {rate.Ratio:P1} server errors",
                DetectionRule = Name,
                DetectedAtUtc = snapshot.EvaluatedAtUtc,
                Namespace = snapshot.Namespace,
                AffectedWorkloads = [rate.Service],
                Signals = new Dictionary<string, string>
                {
                    ["errorRatio"] = rate.Ratio.ToString("F4", CultureInfo.InvariantCulture),
                    ["threshold"] = options.Thresholds.HttpErrorRate.ToString("F4", CultureInfo.InvariantCulture),
                    ["totalRequests"] = rate.TotalRequests.ToString("F0", CultureInfo.InvariantCulture),
                    ["windowMinutes"] = snapshot.Window.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)
                }
            };
        }
    }
}

// Fires when a service's own responses become slow.
public sealed class LatencyRule : IDetectionRule
{
    public string Name => "request-latency";

    public IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options)
    {
        foreach (var (service, p95) in snapshot.LatencyP95)
        {
            if (double.IsNaN(p95) || p95 < options.Thresholds.LatencyP95Seconds)
            {
                continue;
            }

            // Only counted when the service actually served enough traffic for
            // the percentile to mean anything.
            var sample = snapshot.ErrorRates.FirstOrDefault(r => r.Service == service);
            if (sample is null || sample.TotalRequests < options.Thresholds.MinimumRequestSample)
            {
                continue;
            }

            yield return new IncidentCandidate
            {
                Fingerprint = IncidentFingerprint.Create(
                    IncidentCategory.DependencyLatency, snapshot.Namespace, [service]),
                Category = IncidentCategory.DependencyLatency,
                Severity = p95 >= options.Thresholds.LatencyP95Seconds * 3
                    ? IncidentSeverity.High
                    : IncidentSeverity.Medium,
                Title = $"{service} 95th percentile latency is {p95:F2}s",
                DetectionRule = Name,
                DetectedAtUtc = snapshot.EvaluatedAtUtc,
                Namespace = snapshot.Namespace,
                AffectedWorkloads = [service],
                Signals = new Dictionary<string, string>
                {
                    ["p95Seconds"] = p95.ToString("F3", CultureInfo.InvariantCulture),
                    ["thresholdSeconds"] = options.Thresholds.LatencyP95Seconds.ToString("F3", CultureInfo.InvariantCulture),
                    ["windowMinutes"] = snapshot.Window.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)
                }
            };
        }
    }
}

// Fires when calls to a downstream dependency start failing.
//
// This rule is separated from the plain error-rate rule on purpose: it names
// the dependency, which is the difference between "order-api is unhealthy"
// and "order-api cannot reach payment-simulator".
public sealed class DependencyFailureRule : IDetectionRule
{
    public string Name => "dependency-failure";

    public IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options)
    {
        var byDependency = snapshot.DependencyFailures
            .GroupBy(failure => (failure.Dependency, failure.Kind), StringTupleComparer.Instance);

        foreach (var group in byDependency)
        {
            var total = group.Sum(failure => failure.Count);

            if (total < options.Thresholds.DependencyFailureCount)
            {
                continue;
            }

            var callers = group.Select(f => f.Service).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();
            var (dependency, kind) = group.Key;

            // A dependency failing for several independent callers is much
            // stronger evidence that the dependency itself is the problem.
            var severity = callers.Count > 1 ? IncidentSeverity.High : IncidentSeverity.Medium;

            var category = kind is "timeout"
                ? IncidentCategory.DependencyLatency
                : IncidentCategory.DependencyUnavailable;

            yield return new IncidentCandidate
            {
                Fingerprint = IncidentFingerprint.Create(
                    category, snapshot.Namespace, callers, errorSignature: $"{dependency}:{kind}"),
                Category = category,
                Severity = severity,
                Title = $"{total:F0} '{kind}' failures calling {dependency} from {string.Join(", ", callers)}",
                DetectionRule = Name,
                DetectedAtUtc = snapshot.EvaluatedAtUtc,
                Namespace = snapshot.Namespace,
                AffectedWorkloads = callers,
                Signals = new Dictionary<string, string>
                {
                    ["dependency"] = dependency,
                    ["failureKind"] = kind,
                    ["failureCount"] = total.ToString("F0", CultureInfo.InvariantCulture),
                    ["threshold"] = options.Thresholds.DependencyFailureCount.ToString(CultureInfo.InvariantCulture),
                    ["callerCount"] = callers.Count.ToString(CultureInfo.InvariantCulture)
                }
            };
        }
    }
}

// Fires when pods restart more than expected within the window.
public sealed class PodRestartRule : IDetectionRule
{
    public string Name => "pod-restarts";

    public IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options)
    {
        var byWorkload = snapshot.Pods.GroupBy(pod => pod.Workload, StringComparer.Ordinal);

        foreach (var group in byWorkload)
        {
            // The INCREASE since the previous pass, not the absolute count.
            // A pod that restarted three times last week is not an incident
            // today, and using the raw count would raise one every minute.
            var increase = group.Sum(pod =>
            {
                var previous = snapshot.PreviousRestartCounts.GetValueOrDefault(pod.PodName, pod.RestartCount);
                return Math.Max(0, pod.RestartCount - previous);
            });

            var crashLooping = group.Any(pod => pod.WaitingReason == "CrashLoopBackOff");
            var oomKilled = group.Any(pod => pod.LastTerminationReason == "OOMKilled");

            if (increase < options.Thresholds.PodRestartIncrease && !crashLooping && !oomKilled)
            {
                continue;
            }

            // Out of memory is reported as its own category. It has a
            // completely different fix from an application crash, and merging
            // the two would send the investigation down the wrong path.
            var category = oomKilled ? IncidentCategory.OutOfMemory : IncidentCategory.PodRestartLoop;

            var severity = oomKilled || crashLooping
                ? IncidentSeverity.High
                : IncidentSeverity.Medium;

            var signals = new Dictionary<string, string>
            {
                ["restartIncrease"] = increase.ToString(CultureInfo.InvariantCulture),
                ["threshold"] = options.Thresholds.PodRestartIncrease.ToString(CultureInfo.InvariantCulture),
                ["crashLoopBackOff"] = crashLooping ? "true" : "false",
                ["oomKilled"] = oomKilled ? "true" : "false",
                ["podCount"] = group.Count().ToString(CultureInfo.InvariantCulture)
            };

            var reason = group
                .Select(p => p.LastTerminationReason ?? p.WaitingReason)
                .FirstOrDefault(r => !string.IsNullOrEmpty(r));

            if (reason is not null)
            {
                signals["reason"] = reason;
            }

            yield return new IncidentCandidate
            {
                Fingerprint = IncidentFingerprint.Create(
                    category, snapshot.Namespace, [group.Key], errorSignature: reason),
                Category = category,
                Severity = severity,
                Title = oomKilled
                    ? $"{group.Key} was terminated for exceeding its memory limit"
                    : $"{group.Key} restarted {increase} time(s){(crashLooping ? " and is in CrashLoopBackOff" : "")}",
                DetectionRule = Name,
                DetectedAtUtc = snapshot.EvaluatedAtUtc,
                Namespace = snapshot.Namespace,
                AffectedWorkloads = [group.Key],
                Signals = signals
            };
        }
    }
}

// Fires when pods are running but not ready.
//
// Deliberately requires the pod NOT to be restarting. A crash-looping pod is
// also unready, but reporting that as a readiness problem would describe the
// symptom instead of the cause; the restart rule owns that case.
public sealed class ReadinessRule : IDetectionRule
{
    public string Name => "readiness-failure";

    public IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options)
    {
        var byWorkload = snapshot.Pods.GroupBy(pod => pod.Workload, StringComparer.Ordinal);

        foreach (var group in byWorkload)
        {
            var unready = group
                .Where(pod => !pod.Ready
                              && pod.WaitingReason is not "CrashLoopBackOff"
                              && pod.LastTerminationReason is not "OOMKilled")
                .ToList();

            if (unready.Count < options.Thresholds.UnreadyPodCount)
            {
                continue;
            }

            // Every replica unready means the service has no endpoints at all.
            var allUnready = unready.Count == group.Count();

            yield return new IncidentCandidate
            {
                Fingerprint = IncidentFingerprint.Create(
                    IncidentCategory.ReadinessFailure, snapshot.Namespace, [group.Key]),
                Category = IncidentCategory.ReadinessFailure,
                Severity = allUnready ? IncidentSeverity.High : IncidentSeverity.Medium,
                Title = allUnready
                    ? $"every {group.Key} pod is failing its readiness probe"
                    : $"{unready.Count} of {group.Count()} {group.Key} pods are not ready",
                DetectionRule = Name,
                DetectedAtUtc = snapshot.EvaluatedAtUtc,
                Namespace = snapshot.Namespace,
                AffectedWorkloads = [group.Key],
                Signals = new Dictionary<string, string>
                {
                    ["unreadyPods"] = unready.Count.ToString(CultureInfo.InvariantCulture),
                    ["totalPods"] = group.Count().ToString(CultureInfo.InvariantCulture),
                    ["allReplicasUnready"] = allUnready ? "true" : "false",
                    // Explicitly recorded, because "unready but NOT restarting"
                    // is what distinguishes this from a crash loop.
                    ["restartsObserved"] = group.Sum(p => p.RestartCount).ToString(CultureInfo.InvariantCulture)
                }
            };
        }
    }
}

// Fires when one normalised error message repeats often enough to matter.
//
// Catches problems the metric rules miss entirely: an error that is handled
// and returns a 200 still shows up here.
public sealed class RepeatedErrorSignatureRule : IDetectionRule
{
    public string Name => "repeated-error-signature";

    public IEnumerable<IncidentCandidate> Evaluate(DetectionSnapshot snapshot, DetectionOptions options)
    {
        foreach (var signature in snapshot.LogSignatures)
        {
            if (!signature.Attributes.TryGetValue("occurrences", out var raw) ||
                !int.TryParse(raw, CultureInfo.InvariantCulture, out var occurrences))
            {
                continue;
            }

            if (occurrences < options.Thresholds.RepeatedErrorSignatureCount)
            {
                continue;
            }

            // Warnings are collected alongside errors, but only errors are
            // worth raising an incident for on count alone.
            if (signature.Attributes.GetValueOrDefault("level") != "error")
            {
                continue;
            }

            var workload = signature.Workload ?? "unknown";
            var signatureHash = signature.Attributes.GetValueOrDefault("signatureHash");

            yield return new IncidentCandidate
            {
                Fingerprint = IncidentFingerprint.Create(
                    IncidentCategory.RepeatedErrorSignature, snapshot.Namespace, [workload], signatureHash),
                Category = IncidentCategory.RepeatedErrorSignature,
                Severity = occurrences >= options.Thresholds.RepeatedErrorSignatureCount * 5
                    ? IncidentSeverity.High
                    : IncidentSeverity.Medium,
                Title = $"{workload} logged the same error {occurrences} times",
                DetectionRule = Name,
                DetectedAtUtc = snapshot.EvaluatedAtUtc,
                Namespace = snapshot.Namespace,
                AffectedWorkloads = [workload],
                Signals = new Dictionary<string, string>
                {
                    ["occurrences"] = occurrences.ToString(CultureInfo.InvariantCulture),
                    ["threshold"] = options.Thresholds.RepeatedErrorSignatureCount.ToString(CultureInfo.InvariantCulture),
                    ["signatureHash"] = signatureHash ?? "unknown",
                    ["sample"] = signature.Summary
                },
                Evidence = [signature]
            };
        }
    }
}

// Compares two-string tuples ordinally, so grouping by (dependency, kind) is
// culture-independent.
internal sealed class StringTupleComparer : IEqualityComparer<(string, string)>
{
    public static readonly StringTupleComparer Instance = new();

    public bool Equals((string, string) x, (string, string) y) =>
        string.Equals(x.Item1, y.Item1, StringComparison.Ordinal) &&
        string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

    public int GetHashCode((string, string) obj) =>
        HashCode.Combine(obj.Item1, obj.Item2);
}
