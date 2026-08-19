using System.Text.Json;
using Dapper;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Kubernetes;
using KubeSage.Platform.Modules.Persistence;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KubeSage.Platform.Modules.Detection;

// Runs one detection pass: gather a snapshot, evaluate every rule, record the
// candidates, and queue investigations for the ones that are genuinely new.
//
// Nothing in this pipeline calls a model. That is a hard requirement of the
// project, and it is what allows the platform to keep detecting and recording
// incidents while Ollama is down or too slow. The AI layer sits strictly on
// top of a system that already works without it.
public sealed class DetectionEngine
{
    private const string RestartStateKey = "detection.previous-restart-counts";

    private readonly PrometheusClient _prometheus;
    private readonly LokiClient _loki;
    private readonly KubernetesEvidenceClient _kubernetes;
    private readonly EvidenceCollector _evidenceCollector;
    private readonly IncidentRepository _incidents;
    private readonly WorkQueue _workQueue;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<IDetectionRule> _rules;
    private readonly KubeSageOptions _options;
    private readonly ILogger<DetectionEngine> _logger;

    public DetectionEngine(
        PrometheusClient prometheus,
        LokiClient loki,
        KubernetesEvidenceClient kubernetes,
        EvidenceCollector evidenceCollector,
        IncidentRepository incidents,
        WorkQueue workQueue,
        NpgsqlDataSource dataSource,
        IEnumerable<IDetectionRule> rules,
        IOptions<KubeSageOptions> options,
        ILogger<DetectionEngine> logger)
    {
        _prometheus = prometheus;
        _loki = loki;
        _kubernetes = kubernetes;
        _evidenceCollector = evidenceCollector;
        _incidents = incidents;
        _workQueue = workQueue;
        _dataSource = dataSource;
        _rules = rules.ToList();
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DetectionResult> RunPassAsync(CancellationToken cancellationToken)
    {
        var detection = _options.Detection;
        var window = TimeSpan.FromMinutes(detection.EvaluationWindowMinutes);
        var namespaceName = _options.Telemetry.WorkloadNamespace;

        var snapshot = await BuildSnapshotAsync(namespaceName, window, cancellationToken);

        var candidates = new List<IncidentCandidate>();

        foreach (var rule in _rules)
        {
            try
            {
                candidates.AddRange(rule.Evaluate(snapshot, detection));
            }
            catch (Exception ex)
            {
                // One broken rule must not stop the others from running.
                _logger.LogError(ex, "Detection rule {Rule} threw and was skipped", rule.Name);
            }
        }

        // Collapse the pass down to what is actually worth investigating.
        // A single outage legitimately trips several rules at once; without
        // this step one incident would become a dozen investigations, each
        // costing minutes of a slow local model.
        var raw = candidates.Count;
        candidates = CandidateSuppression.Apply(candidates).ToList();

        if (raw != candidates.Count)
        {
            _logger.LogInformation(
                "Suppressed {SuppressedCount} redundant candidate(s) of {RawCount}; " +
                "a more explanatory rule already covered the same workloads",
                raw - candidates.Count, raw);
        }

        var created = 0;
        var deduplicated = 0;

        foreach (var candidate in candidates)
        {
            var outcome = await _incidents.RecordCandidateAsync(
                candidate,
                TimeSpan.FromMinutes(detection.DeduplicationCooldownMinutes),
                cancellationToken);

            if (outcome.Disposition == CandidateDisposition.Deduplicated)
            {
                deduplicated++;
                continue;
            }

            created++;
            await AttachEvidenceAndQueueAsync(outcome.IncidentId, candidate, cancellationToken);
        }

        await SaveRestartCountsAsync(snapshot, cancellationToken);
        await ConfirmRecoveriesAsync(cancellationToken);

        if (created > 0 || deduplicated > 0)
        {
            _logger.LogInformation(
                "Detection pass evaluated {RuleCount} rules over {WindowMinutes}m: {Created} new incident(s), {Deduplicated} repeat observation(s)",
                _rules.Count, window.TotalMinutes, created, deduplicated);
        }

        return new DetectionResult(candidates.Count, created, deduplicated, snapshot.EvaluatedAtUtc);
    }

    // Collects the whole evidence bundle for a newly created incident and
    // queues the investigation.
    //
    // Evidence is captured NOW, at detection time, rather than when the
    // investigation eventually runs. On a slow local model an investigation
    // may start many minutes later, by which time the interesting log lines
    // could have aged out of the query window.
    private async Task AttachEvidenceAndQueueAsync(
        Guid incidentId,
        IncidentCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await _evidenceCollector.CollectAsync(
                new EvidenceRequest
                {
                    Moment = candidate.DetectedAtUtc,
                    Window = TimeSpan.FromMinutes(_options.Detection.EvaluationWindowMinutes),
                    Workload = candidate.AffectedWorkloads.FirstOrDefault(),
                    Namespace = candidate.Namespace
                },
                cancellationToken);

            await _incidents.SaveEvidenceAsync(incidentId, bundle.Items, cancellationToken);
        }
        catch (Exception ex)
        {
            // The incident is already recorded, which is the important part.
            // An investigation can still collect evidence itself later.
            _logger.LogWarning(ex, "Could not attach evidence to incident {IncidentId} at detection time", incidentId);
        }

        // The incident id is the deduplication key, so an investigation can
        // only ever be queued once per incident.
        await _workQueue.EnqueueAsync(
            WorkKind.Investigation,
            incidentId.ToString(),
            new { incidentId, trigger = "detection", candidate.Category },
            cancellationToken);
    }

    private async Task<DetectionSnapshot> BuildSnapshotAsync(
        string namespaceName,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var errorRates = new List<ServiceRate>();
        var latencies = new Dictionary<string, double>(StringComparer.Ordinal);
        var dependencyFailures = new List<DependencyFailure>();
        var dependencyLatencies = new List<DependencyLatency>();
        var pods = new List<PodRestartInfo>();
        var signatures = new List<Evidence>();

        // Each source is optional. A detection pass with only Kubernetes data
        // still catches crash loops and OOM kills, which is much better than
        // no detection at all while Prometheus restarts.
        try
        {
            var services = await _prometheus.GetKnownServicesAsync(cancellationToken);

            foreach (var service in services)
            {
                var rate = await _prometheus.GetHttpErrorRateAsync(service, window, cancellationToken);
                if (rate is not null)
                {
                    errorRates.Add(rate);
                }

                var p95 = await _prometheus.GetLatencyP95Async(service, window, cancellationToken);
                if (p95 is not null && !double.IsNaN(p95.Value))
                {
                    latencies[service] = p95.Value;
                }
            }

            dependencyFailures.AddRange(await _prometheus.GetDependencyFailuresAsync(null, window, cancellationToken));
            dependencyLatencies.AddRange(await _prometheus.GetDependencyLatencyAsync(null, window, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus was unavailable during this detection pass; metric rules will not fire");
        }

        try
        {
            pods.AddRange(await _kubernetes.GetRestartCountsAsync(namespaceName, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kubernetes was unavailable during this detection pass; pod rules will not fire");
        }

        try
        {
            signatures.AddRange(await _loki.GetErrorSignaturesAsync(
                namespaceName, null, now - window, now, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loki was unavailable during this detection pass; log rules will not fire");
        }

        return new DetectionSnapshot
        {
            EvaluatedAtUtc = now,
            Window = window,
            Namespace = namespaceName,
            ErrorRates = errorRates,
            LatencyP95 = latencies,
            DependencyFailures = dependencyFailures,
            DependencyLatencies = dependencyLatencies,
            Pods = pods,
            LogSignatures = signatures,
            PreviousRestartCounts = await LoadRestartCountsAsync(cancellationToken)
        };
    }

    // Restart counts are persisted so the comparison survives a restart of the
    // platform itself. Holding them only in memory would make every pod look
    // freshly restarted after any redeploy of this process.
    private async Task<IReadOnlyDictionary<string, int>> LoadRestartCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value::text FROM detection_state WHERE key = @key",
            new { key = RestartStateKey }, cancellationToken: cancellationToken));

        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    private async Task SaveRestartCountsAsync(DetectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Pods.Count == 0)
        {
            // Kubernetes was unreachable. Keeping the previous counts is
            // correct: overwriting them with nothing would make every pod
            // appear to have restarted on the next successful pass.
            return;
        }

        var counts = snapshot.Pods.ToDictionary(pod => pod.PodName, pod => pod.RestartCount, StringComparer.Ordinal);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO detection_state (key, value, updated_at_utc)
            VALUES (@key, @value::jsonb, now() AT TIME ZONE 'utc')
            ON CONFLICT (key) DO UPDATE
                SET value = EXCLUDED.value, updated_at_utc = EXCLUDED.updated_at_utc
            """,
            new { key = RestartStateKey, value = JsonSerializer.Serialize(counts) },
            cancellationToken: cancellationToken));
    }

    // Closes incidents whose condition has not been seen for long enough.
    //
    // Recovery is deterministic and time-based, not something an agent
    // decides. An incident that stops recurring simply stops being current.
    private async Task ConfirmRecoveriesAsync(CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromMinutes(_options.Detection.RecoveryConfirmationMinutes);
        var recovered = await _incidents.ListRecoveredCandidatesAsync(window, cancellationToken);

        foreach (var incident in recovered)
        {
            try
            {
                await _incidents.TransitionAsync(
                    incident.Id,
                    IncidentState.Recovered,
                    $"condition not observed for {window.TotalMinutes:F0} minutes",
                    cancellationToken);
            }
            catch (InvalidIncidentTransitionException ex)
            {
                _logger.LogWarning(ex, "Could not mark incident {IncidentId} recovered", incident.Id);
            }
        }
    }
}

public sealed record DetectionResult(
    int CandidatesEvaluated,
    int IncidentsCreated,
    int Deduplicated,
    DateTimeOffset EvaluatedAtUtc);
