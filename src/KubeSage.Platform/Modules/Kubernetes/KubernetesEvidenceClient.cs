using System.Globalization;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Telemetry;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Kubernetes;

// Reads authoritative cluster state and turns it into normalised Evidence.
//
// Kubernetes answers the questions logs and metrics cannot: was the container
// killed, and why; how many times has it restarted; is the pod in the Service
// endpoints; what did the scheduler and kubelet say about it.
//
// Every method here is a read. There is no code path in this class that
// creates, updates, patches or deletes anything, and the service account it
// authenticates with has no permission to do so either. Those are two
// independent guarantees on purpose: the code could be changed by mistake,
// but the cluster would still refuse.
public sealed class KubernetesEvidenceClient
{
    private readonly IKubernetes _client;
    private readonly TelemetryQuery _guard;
    private readonly SensitiveDataRedactor _redactor;
    private readonly KubernetesOptions _options;
    private readonly ILogger<KubernetesEvidenceClient> _logger;

    public KubernetesEvidenceClient(
        IKubernetes client,
        TelemetryQuery guard,
        SensitiveDataRedactor redactor,
        IOptions<KubeSageOptions> options,
        ILogger<KubernetesEvidenceClient> logger)
    {
        _client = client;
        _guard = guard;
        _redactor = redactor;
        _options = options.Value.Kubernetes;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.CoreV1.ListNamespaceAsync(limit: 1, cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kubernetes API availability check failed");
            return false;
        }
    }

    // Current state of every pod belonging to a workload: phase, readiness,
    // restart count, and - most importantly - why a container is waiting or
    // why it last terminated.
    public async Task<IReadOnlyList<Evidence>> GetPodStatusAsync(
        string? namespaceName,
        string? workload,
        CancellationToken cancellationToken)
    {
        var resolvedNamespace = _guard.RequireNamespace(namespaceName);
        var selector = BuildLabelSelector(workload);

        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            resolvedNamespace,
            labelSelector: selector,
            limit: _options.MaxItemsPerQuery,
            cancellationToken: cancellationToken);

        var evidence = new List<Evidence>();

        foreach (var pod in pods.Items)
        {
            var name = pod.Metadata.Name;
            var phase = pod.Status?.Phase ?? "Unknown";
            var containers = pod.Status?.ContainerStatuses ?? [];

            var restarts = containers.Sum(c => c.RestartCount);
            var ready = containers.Count > 0 && containers.All(c => c.Ready);

            // These two reasons are what actually identify the failure mode.
            // "CrashLoopBackOff" and "OOMKilled" are the difference between a
            // bug and a memory limit, and no other source reports them.
            var waitingReason = containers
                .Select(c => c.State?.Waiting?.Reason)
                .FirstOrDefault(reason => !string.IsNullOrEmpty(reason));

            var terminatedReason = containers
                .Select(c => c.LastState?.Terminated?.Reason)
                .FirstOrDefault(reason => !string.IsNullOrEmpty(reason));

            var exitCode = containers
                .Select(c => c.LastState?.Terminated?.ExitCode)
                .FirstOrDefault(code => code is not null);

            // WHEN the last restart happened, not just how many there have
            // been. A cumulative restart count cannot distinguish "crashing
            // right now" from "restarted during a deployment an hour ago", and
            // without this a cluster health summary reads old restarts as a
            // current problem.
            var lastTerminatedAt = containers
                .Select(c => c.LastState?.Terminated?.FinishedAt)
                .Where(finished => finished is not null)
                .OrderByDescending(finished => finished)
                .FirstOrDefault();

            var attributes = new Dictionary<string, string>
            {
                ["pod"] = name,
                ["phase"] = phase,
                ["ready"] = ready ? "true" : "false",
                ["restartCount"] = restarts.ToString(CultureInfo.InvariantCulture),
                ["node"] = pod.Spec?.NodeName ?? "unassigned"
            };

            if (!string.IsNullOrEmpty(waitingReason))
            {
                attributes["waitingReason"] = waitingReason;
            }

            if (!string.IsNullOrEmpty(terminatedReason))
            {
                attributes["lastTerminationReason"] = terminatedReason;
            }

            if (exitCode is not null)
            {
                attributes["lastExitCode"] = exitCode.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (lastTerminatedAt is not null)
            {
                var age = DateTime.UtcNow - lastTerminatedAt.Value;

                attributes["lastRestartAtUtc"] = lastTerminatedAt.Value.ToString("O", CultureInfo.InvariantCulture);
                attributes["minutesSinceLastRestart"] =
                    Math.Round(age.TotalMinutes).ToString("F0", CultureInfo.InvariantCulture);

                // Stated in words as well as numbers, because this is the
                // distinction most often got wrong when reading pod state.
                attributes["restartIsRecent"] = age < TimeSpan.FromMinutes(15) ? "true" : "false";
            }

            // Memory limits belong with pod state because "OOMKilled" only
            // means something alongside the limit that was exceeded.
            var limits = pod.Spec?.Containers?.FirstOrDefault()?.Resources?.Limits;
            if (limits is not null && limits.TryGetValue("memory", out var memoryLimit))
            {
                attributes["memoryLimit"] = memoryLimit.ToString();
            }

            var summary = new List<string> { $"pod {name} phase={phase} ready={ready} restarts={restarts}" };
            if (!string.IsNullOrEmpty(waitingReason)) summary.Add($"waiting={waitingReason}");
            if (!string.IsNullOrEmpty(terminatedReason)) summary.Add($"lastTermination={terminatedReason}");
            if (exitCode is not null) summary.Add($"exitCode={exitCode}");

            if (lastTerminatedAt is not null)
            {
                var minutes = Math.Round((DateTime.UtcNow - lastTerminatedAt.Value).TotalMinutes);
                summary.Add(minutes < 15
                    ? $"last restart {minutes:F0}m ago (RECENT)"
                    : $"last restart {minutes:F0}m ago (not recent - probably unrelated to any current problem)");
            }

            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(
                    EvidenceKind.KubernetesState, "kubernetes",
                    resolvedNamespace, name, phase, restarts.ToString(CultureInfo.InvariantCulture),
                    waitingReason, terminatedReason),
                Kind = EvidenceKind.KubernetesState,
                Source = "kubernetes",
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Workload = WorkloadOf(pod),
                Namespace = resolvedNamespace,
                Summary = string.Join(", ", summary),
                Query = $"GET /api/v1/namespaces/{resolvedNamespace}/pods?labelSelector={selector}",
                Attributes = attributes
            });
        }

        return evidence;
    }

    // Restart counts on their own, which is what a detection rule compares
    // between evaluation windows.
    public async Task<IReadOnlyList<PodRestartInfo>> GetRestartCountsAsync(
        string? namespaceName,
        CancellationToken cancellationToken)
    {
        var resolvedNamespace = _guard.RequireNamespace(namespaceName);

        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            resolvedNamespace,
            limit: _options.MaxItemsPerQuery,
            cancellationToken: cancellationToken);

        return pods.Items
            .Select(pod => new PodRestartInfo(
                PodName: pod.Metadata.Name,
                Workload: WorkloadOf(pod) ?? pod.Metadata.Name,
                RestartCount: (pod.Status?.ContainerStatuses ?? []).Sum(c => c.RestartCount),
                Ready: (pod.Status?.ContainerStatuses ?? []).Count > 0 &&
                       (pod.Status?.ContainerStatuses ?? []).All(c => c.Ready),
                WaitingReason: (pod.Status?.ContainerStatuses ?? [])
                    .Select(c => c.State?.Waiting?.Reason)
                    .FirstOrDefault(r => !string.IsNullOrEmpty(r)),
                LastTerminationReason: (pod.Status?.ContainerStatuses ?? [])
                    .Select(c => c.LastState?.Terminated?.Reason)
                    .FirstOrDefault(r => !string.IsNullOrEmpty(r))))
            .ToList();
    }

    // Recent Kubernetes events. Warnings are what matter: BackOff, Unhealthy,
    // Killing, FailedScheduling.
    public async Task<IReadOnlyList<Evidence>> GetEventsAsync(
        string? namespaceName,
        string? workload,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var resolvedNamespace = _guard.RequireNamespace(namespaceName);

        var events = await _client.CoreV1.ListNamespacedEventAsync(
            resolvedNamespace,
            limit: _options.MaxItemsPerQuery,
            cancellationToken: cancellationToken);

        var evidence = new List<Evidence>();

        foreach (var item in events.Items)
        {
            var occurred = item.LastTimestamp ?? item.EventTime ?? item.FirstTimestamp;

            if (occurred is null || occurred < since.UtcDateTime)
            {
                continue;
            }

            var involved = item.InvolvedObject?.Name ?? string.Empty;

            // Deployments create ReplicaSets whose pod names start with the
            // workload name, so a prefix match relates an event to its
            // workload without another API call.
            if (!string.IsNullOrWhiteSpace(workload) &&
                !involved.StartsWith(workload, StringComparison.Ordinal))
            {
                continue;
            }

            var message = _redactor.Redact(item.Message);

            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(
                    EvidenceKind.KubernetesEvent, "kubernetes",
                    resolvedNamespace, involved, item.Reason,
                    occurred.Value.ToString("O", CultureInfo.InvariantCulture)),
                Kind = EvidenceKind.KubernetesEvent,
                Source = "kubernetes",
                ObservedAtUtc = new DateTimeOffset(occurred.Value, TimeSpan.Zero),
                Workload = workload,
                Namespace = resolvedNamespace,
                Summary = $"[{item.Type}] {item.Reason} on {item.InvolvedObject?.Kind} {involved}: {message.Text}",
                RedactedValueCount = message.RedactionCount,
                Query = $"GET /api/v1/namespaces/{resolvedNamespace}/events",
                Attributes = new Dictionary<string, string>
                {
                    ["type"] = item.Type ?? "Normal",
                    ["reason"] = item.Reason ?? "Unknown",
                    ["object"] = $"{item.InvolvedObject?.Kind}/{involved}",
                    ["count"] = (item.Count ?? 1).ToString(CultureInfo.InvariantCulture),
                    ["occurredAtUtc"] = occurred.Value.ToString("O", CultureInfo.InvariantCulture)
                }
            });
        }

        return evidence
            .OrderByDescending(e => e.ObservedAtUtc)
            .Take(_options.MaxItemsPerQuery)
            .ToList();
    }

    // Desired versus actual replicas. This is how "the database was scaled to
    // zero" becomes visible as a fact rather than an inference.
    public async Task<IReadOnlyList<Evidence>> GetDeploymentStatusAsync(
        string? namespaceName,
        string? workload,
        CancellationToken cancellationToken)
    {
        var resolvedNamespace = _guard.RequireNamespace(namespaceName);

        var deployments = await _client.AppsV1.ListNamespacedDeploymentAsync(
            resolvedNamespace,
            labelSelector: BuildLabelSelector(workload),
            limit: _options.MaxItemsPerQuery,
            cancellationToken: cancellationToken);

        var evidence = new List<Evidence>();

        foreach (var deployment in deployments.Items)
        {
            var name = deployment.Metadata.Name;
            var desired = deployment.Spec?.Replicas ?? 0;
            var ready = deployment.Status?.ReadyReplicas ?? 0;
            var available = deployment.Status?.AvailableReplicas ?? 0;
            var updated = deployment.Status?.UpdatedReplicas ?? 0;

            var unavailableCondition = deployment.Status?.Conditions?
                .FirstOrDefault(c => c.Type == "Available" && c.Status != "True");

            var summary =
                $"deployment {name}: desired={desired} ready={ready} available={available} updated={updated}";

            if (unavailableCondition is not null)
            {
                summary += $" - {unavailableCondition.Reason}: {unavailableCondition.Message}";
            }

            evidence.Add(new Evidence
            {
                Id = Evidence.CreateId(
                    EvidenceKind.KubernetesState, "kubernetes",
                    resolvedNamespace, name, "deployment",
                    $"{desired}/{ready}/{available}"),
                Kind = EvidenceKind.KubernetesState,
                Source = "kubernetes",
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Workload = name,
                Namespace = resolvedNamespace,
                Summary = summary,
                Query = $"GET /apis/apps/v1/namespaces/{resolvedNamespace}/deployments/{name}",
                Attributes = new Dictionary<string, string>
                {
                    ["desiredReplicas"] = desired.ToString(CultureInfo.InvariantCulture),
                    ["readyReplicas"] = ready.ToString(CultureInfo.InvariantCulture),
                    ["availableReplicas"] = available.ToString(CultureInfo.InvariantCulture),
                    ["updatedReplicas"] = updated.ToString(CultureInfo.InvariantCulture)
                }
            });
        }

        return evidence;
    }

    // The workloads present in a namespace, used by detection to know what to
    // evaluate without hard-coding service names.
    public async Task<IReadOnlyList<string>> GetWorkloadNamesAsync(
        string? namespaceName,
        CancellationToken cancellationToken)
    {
        var resolvedNamespace = _guard.RequireNamespace(namespaceName);

        var deployments = await _client.AppsV1.ListNamespacedDeploymentAsync(
            resolvedNamespace,
            limit: _options.MaxItemsPerQuery,
            cancellationToken: cancellationToken);

        return deployments.Items
            .Select(d => d.Metadata.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static string? BuildLabelSelector(string? workload) =>
        string.IsNullOrWhiteSpace(workload) ? null : $"app.kubernetes.io/name={workload}";

    // The label every KubeSage workload carries. Reading it is how a pod name
    // like "order-api-b884cf64c-5fthb" is related back to the workload
    // "order-api" without parsing the generated suffixes.
    private static string? WorkloadOf(V1Pod pod)
    {
        var labels = pod.Metadata?.Labels;

        return labels is not null && labels.TryGetValue("app.kubernetes.io/name", out var name)
            ? name
            : null;
    }
}

public sealed record PodRestartInfo(
    string PodName,
    string Workload,
    int RestartCount,
    bool Ready,
    string? WaitingReason,
    string? LastTerminationReason);
