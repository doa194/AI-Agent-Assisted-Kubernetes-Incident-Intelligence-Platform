using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Incidents;
using KubeSage.Platform.Modules.Kubernetes;
using KubeSage.Platform.Modules.Persistence;
using KubeSage.Platform.Modules.Reporting;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Produces the cluster-level reports: the one written shortly after start-up,
// and the periodic health summary.
//
// These answer a different question from an incident report. An incident
// report explains one failure; this describes the state of the whole system,
// including the case where nothing is wrong. That case matters - "I looked at
// everything and it is healthy, here is what I checked" is a useful thing for
// an operator to be able to read, and it is evidence the platform is actually
// watching rather than merely running.
//
// It uses the same grounding rules as everything else: evidence is collected
// deterministically first, and the summary may only cite what was collected.
public sealed class ClusterAnalysis
{
    private readonly EvidenceCollector _evidenceCollector;
    private readonly KubernetesEvidenceClient _kubernetes;
    private readonly IncidentRepository _incidents;
    private readonly ReportRepository _reports;
    private readonly IncidentAgents _agents;
    private readonly AgentOutputValidator _validator;
    private readonly KubeSageOptions _options;
    private readonly ILogger<ClusterAnalysis> _logger;

    public ClusterAnalysis(
        EvidenceCollector evidenceCollector,
        KubernetesEvidenceClient kubernetes,
        IncidentRepository incidents,
        ReportRepository reports,
        IncidentAgents agents,
        AgentOutputValidator validator,
        IOptions<KubeSageOptions> options,
        ILogger<ClusterAnalysis> logger)
    {
        _evidenceCollector = evidenceCollector;
        _kubernetes = kubernetes;
        _incidents = incidents;
        _reports = reports;
        _agents = agents;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Guid?> RunAsync(string kind, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running {Kind} cluster analysis", kind);

        var evidence = await CollectClusterEvidenceAsync(cancellationToken);
        var openIncidents = await _incidents.ListInFlightAsync(cancellationToken);

        if (evidence.Count == 0)
        {
            // No evidence means the telemetry layer could not be reached at
            // all. Producing a report saying "everything is fine" from no data
            // would be actively misleading.
            _logger.LogWarning(
                "{Kind} analysis collected no evidence; no report was written because there is nothing to base one on",
                kind);
            return null;
        }

        var prompt = BuildPrompt(evidence, openIncidents, kind);

        ClusterHealthResult? result;

        try
        {
            var response = await _agents.CreateClusterAnalysisAgent()
                .RunAsync(prompt, cancellationToken: cancellationToken);

            result = JsonSerializer.Deserialize<ClusterHealthResult>(
                response.Text, SerializerOptions);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "{Kind} analysis failed while generating its summary", kind);
            return null;
        }

        if (result is null)
        {
            _logger.LogWarning("{Kind} analysis produced no usable summary", kind);
            return null;
        }

        // Same grounding rule as an incident report: a citation that does not
        // resolve is removed rather than trusted.
        var validated = _validator.ValidateClusterReport(result, evidence);

        var reportId = await _reports.SaveClusterReportAsync(
            kind, validated.Value, evidence, openIncidents.Count, cancellationToken);

        _logger.LogInformation(
            "{Kind} report generated: {Headline} | status {Status} | {OpenIncidents} open incident(s), " +
            "{EvidenceCount} evidence item(s) examined",
            kind, validated.Value.Headline, validated.Value.OverallStatus,
            openIncidents.Count, evidence.Count);

        return reportId;
    }

    // Cluster-wide evidence: every workload, not one.
    private async Task<IReadOnlyList<Evidence>> CollectClusterEvidenceAsync(CancellationToken cancellationToken)
    {
        var collected = new List<Evidence>();

        try
        {
            var workloads = await _kubernetes.GetWorkloadNamesAsync(null, cancellationToken);

            // A bundle per workload, then trimmed. Collecting per workload is
            // what makes the metrics workload-specific rather than an
            // uninterpretable cluster-wide average.
            foreach (var workload in workloads)
            {
                var bundle = await _evidenceCollector.CollectAsync(
                    new EvidenceRequest
                    {
                        Moment = DateTimeOffset.UtcNow,
                        Window = TimeSpan.FromMinutes(_options.Detection.EvaluationWindowMinutes),
                        Workload = workload,
                        MaxLogLines = 10
                    },
                    cancellationToken);

                collected.AddRange(bundle.Items);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cluster evidence collection was incomplete");
        }

        // Cluster state and metrics carry the most meaning per item for a
        // whole-system view; individual log lines carry the least.
        return EvidenceSelector.Select(collected, _options.Investigation.MaxEvidenceItems);
    }

    private static string BuildPrompt(
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<Incident> openIncidents,
        string kind)
    {
        var builder = new StringBuilder();

        builder.AppendLine(kind == WorkKind.StartupAnalysis
            ? "## Cluster startup review"
            : "## Periodic cluster health review");

        builder.AppendLine();
        builder.AppendLine($"Open incidents: {openIncidents.Count}");

        foreach (var incident in openIncidents.Take(10))
        {
            builder.AppendLine($"- [{incident.Severity}] {incident.Category}: {incident.Title}");
        }

        builder.AppendLine();
        builder.AppendLine("## Evidence");
        PromptBuilder.AppendEvidence(builder, evidence);

        builder.AppendLine();
        builder.AppendLine("## Task");
        builder.AppendLine(
            "Summarise the current health of this cluster for an operator. State plainly whether it is " +
            "healthy, degraded or unhealthy, and say what you checked. If everything is fine, say so - " +
            "do not manufacture concerns. Cite the evidence id behind every observation you make.");

        return builder.ToString();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

// A whole-cluster health summary.
public sealed record ClusterHealthResult
{
    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; init; }

    [JsonPropertyName("headline")]
    public required string Headline { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    // What the analysis actually looked at. Included so a clean bill of health
    // can be judged: "healthy" means much more when it names what was checked.
    [JsonPropertyName("workloadsReviewed")]
    public string[] WorkloadsReviewed { get; init; } = [];

    [JsonPropertyName("concerns")]
    public string[] Concerns { get; init; } = [];

    [JsonPropertyName("evidenceIds")]
    public required string[] EvidenceIds { get; init; }

    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "overallStatus": { "type": "string", "enum": ["healthy", "degraded", "unhealthy"] },
            "headline": { "type": "string" },
            "summary": { "type": "string" },
            "workloadsReviewed": { "type": "array", "items": { "type": "string" } },
            "concerns": { "type": "array", "items": { "type": "string" } },
            "evidenceIds": { "type": "array", "items": { "type": "string" }, "maxItems": 10 }
          },
          "required": ["overallStatus", "headline", "summary", "evidenceIds"]
        }
        """);
}
