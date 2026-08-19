using System.Globalization;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.Telemetry;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Retrieval;

// Retrieves historical incidents and runbook guidance for an investigation.
//
// Results come back as Evidence, exactly like a log line or a metric, so the
// agent cites them the same way and the validator checks them the same way.
//
// But they are marked as a DIFFERENT KIND of evidence on purpose, and the
// prompt labels them clearly. A past incident is not evidence about the
// current one - it is a hint about where to look. Blurring that distinction is
// how a system starts confidently reporting last month's root cause for this
// month's outage, and it is the main risk retrieval introduces.
public sealed class MemoryRetriever
{
    private readonly EmbeddingClient _embeddings;
    private readonly SemanticMemoryRepository _memory;
    private readonly RetrievalOptions _options;
    private readonly ILogger<MemoryRetriever> _logger;

    public MemoryRetriever(
        EmbeddingClient embeddings,
        SemanticMemoryRepository memory,
        IOptions<KubeSageOptions> options,
        ILogger<MemoryRetriever> logger)
    {
        _embeddings = embeddings;
        _memory = memory;
        _options = options.Value.Retrieval;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    // Past incidents that resemble this description.
    public async Task<IReadOnlyList<Evidence>> SearchSimilarIncidentsAsync(
        string query,
        string? workload,
        Guid? excludeIncidentId,
        CancellationToken cancellationToken)
    {
        return await SearchAsync(
            query,
            new MemorySearchFilter
            {
                Kind = MemoryKind.Incident,
                Workload = workload,
                ExcludeIncidentId = excludeIncidentId,
                TopK = _options.TopK,
                MaxDistance = _options.MaxDistance
            },
            EvidenceKind.HistoricalIncident,
            cancellationToken);
    }

    // Runbook guidance relevant to this description.
    public async Task<IReadOnlyList<Evidence>> SearchRunbooksAsync(
        string query,
        string? category,
        CancellationToken cancellationToken)
    {
        return await SearchAsync(
            query,
            new MemorySearchFilter
            {
                Kind = MemoryKind.Runbook,
                Category = category,
                TopK = _options.TopK,
                MaxDistance = _options.MaxDistance
            },
            EvidenceKind.Runbook,
            cancellationToken);
    }

    private async Task<IReadOnlyList<Evidence>> SearchAsync(
        string query,
        MemorySearchFilter filter,
        EvidenceKind evidenceKind,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        try
        {
            var embedding = await _embeddings.EmbedAsync(query, cancellationToken);
            var matches = await _memory.SearchAsync(embedding, filter, cancellationToken);

            // Metadata filters are a PREFERENCE, not a requirement.
            //
            // Filtering first gives better precision when it matches, but on a
            // small corpus it can exclude everything. Observed in a real run:
            // an incident categorised http_error_rate matched no runbook,
            // because runbooks are categorised by the problem they describe
            // (dependency_latency, out_of_memory and so on) and none carries
            // that category. Retrieval silently returned nothing, even though
            // the correct runbook was the nearest vector by a wide margin.
            //
            // So a filtered search that finds nothing is retried without the
            // facets. Similarity and the distance cut-off still decide
            // relevance, which is what actually keeps weak matches out.
            if (matches.Count == 0 && (filter.Category is not null || filter.Workload is not null))
            {
                var relaxed = filter with { Category = null, Workload = null };
                matches = await _memory.SearchAsync(embedding, relaxed, cancellationToken);

                if (matches.Count > 0)
                {
                    _logger.LogDebug(
                        "No {Kind} memory matched the metadata filter; retried on similarity alone and found {MatchCount}",
                        filter.Kind, matches.Count);
                }
            }

            _logger.LogInformation(
                "Retrieved {MatchCount} {Kind} memory match(es) for a {QueryLength}-character query",
                matches.Count, filter.Kind, query.Length);

            return matches.Select(match => ToEvidence(match, evidenceKind)).ToList();
        }
        catch (EmbeddingUnavailableException ex)
        {
            // Retrieval is an enhancement. Without it an investigation still
            // has all its live telemetry, so this degrades rather than fails.
            _logger.LogWarning(ex, "Semantic retrieval unavailable; the investigation continues without history");
            return [];
        }
    }

    private static Evidence ToEvidence(MemoryMatch match, EvidenceKind kind)
    {
        var prefix = kind == EvidenceKind.HistoricalIncident
            // Stated in the summary itself, not only in the metadata, because
            // the summary is what the model reads most closely.
            ? $"PAST INCIDENT (not evidence about the current one, retrieval confidence {match.RetrievalConfidence:P0})"
            : $"RUNBOOK GUIDANCE (documentation, not an observation, retrieval confidence {match.RetrievalConfidence:P0})";

        var attributes = new Dictionary<string, string>
        {
            ["sourceRef"] = match.SourceRef,
            ["retrievalDistance"] = match.DistanceLabel,
            // Kept separate from any root-cause confidence an agent reports.
            // A strong text match does not make a diagnosis more likely.
            ["retrievalConfidence"] = match.RetrievalConfidence.ToString("F3", CultureInfo.InvariantCulture)
        };

        if (match.IncidentId is not null) attributes["historicalIncidentId"] = match.IncidentId.Value.ToString();
        if (match.RootCauseCategory is not null) attributes["rootCauseCategory"] = match.RootCauseCategory;
        if (match.Workload is not null) attributes["workload"] = match.Workload;
        if (match.OccurredAtUtc is not null) attributes["occurredAtUtc"] = match.OccurredAtUtc.Value.ToString("O");

        return new Evidence
        {
            Id = Evidence.CreateId(kind, "memory", match.SourceRef),
            Kind = kind,
            Source = "memory",
            // The time the remembered thing happened, not the time it was
            // retrieved, so a report's timeline cannot accidentally place a
            // past incident in the present.
            ObservedAtUtc = match.OccurredAtUtc ?? DateTimeOffset.UtcNow,
            Workload = match.Workload,
            Summary = $"{prefix}: {match.Title}\n{Truncate(match.Content, 900)}",
            Query = $"semantic search (kind={match.Kind}, distance={match.DistanceLabel})",
            Attributes = attributes
        };
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "...";
}
