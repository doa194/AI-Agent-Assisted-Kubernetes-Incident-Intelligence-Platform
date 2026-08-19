using System.Reflection;
using System.Text;
using KubeSage.Platform.Configuration;
using KubeSage.Platform.Modules.AgentWorkflows;
using KubeSage.Platform.Modules.Incidents;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.Retrieval;

// Puts things into semantic memory.
//
// Two sources, indexed at different times:
//
//   runbooks  - embedded at start-up from the corpus compiled into the
//               assembly, and only re-embedded when their content changes;
//   incidents - embedded after a report is generated, so the platform
//               gradually accumulates its own operational history.
//
// The text that gets embedded is written carefully. It is not the raw report
// and not raw telemetry, but a short description of the PROBLEM in the
// vocabulary someone would use when hitting it again. That is what makes a
// later search find it.
public sealed class SemanticMemoryIndexer
{
    private const string RunbookResourcePrefix = "KubeSage.Platform.Knowledge.Runbooks.";

    private readonly EmbeddingClient _embeddings;
    private readonly SemanticMemoryRepository _memory;
    private readonly RetrievalOptions _options;
    private readonly ILogger<SemanticMemoryIndexer> _logger;

    public SemanticMemoryIndexer(
        EmbeddingClient embeddings,
        SemanticMemoryRepository memory,
        IOptions<KubeSageOptions> options,
        ILogger<SemanticMemoryIndexer> logger)
    {
        _embeddings = embeddings;
        _memory = memory;
        _options = options.Value.Retrieval;
        _logger = logger;
    }

    // Indexes the runbook corpus, skipping sections that have not changed.
    public async Task<int> IndexRunbooksAsync(CancellationToken cancellationToken)
    {
        var sections = LoadRunbookSections();

        if (sections.Count == 0)
        {
            _logger.LogWarning("No runbooks were found in the assembly; retrieval will only return past incidents");
            return 0;
        }

        var pending = new List<(RunbookSection Section, string Content)>();

        foreach (var section in sections)
        {
            var content = section.ToEmbeddableText();

            // Embedding is the slow part, so unchanged sections are skipped.
            // On a normal restart this makes indexing effectively free.
            if (await _memory.IsCurrentAsync(MemoryKind.Runbook, section.SourceRef, content, cancellationToken))
            {
                continue;
            }

            pending.Add((section, content));
        }

        if (pending.Count == 0)
        {
            _logger.LogInformation("Runbook corpus is already indexed ({SectionCount} sections)", sections.Count);
            return 0;
        }

        // One batched request rather than one per section: for a model this
        // small, per-request overhead dominates the actual work.
        var vectors = await _embeddings.EmbedBatchAsync(
            pending.Select(p => p.Content).ToList(), cancellationToken);

        for (var index = 0; index < pending.Count; index++)
        {
            var (section, content) = pending[index];

            await _memory.UpsertAsync(
                new MemoryRecord
                {
                    Kind = MemoryKind.Runbook,
                    SourceRef = section.SourceRef,
                    Title = section.Title,
                    Content = content,
                    // Runbooks are indexed against the incident category they
                    // address, so a search can be narrowed to guidance that is
                    // actually about this kind of problem.
                    Category = section.Category
                },
                vectors[index],
                cancellationToken);
        }

        _logger.LogInformation(
            "Indexed {IndexedCount} runbook section(s) of {TotalCount}", pending.Count, sections.Count);

        return pending.Count;
    }

    // Records a finished investigation as a memory for future incidents.
    //
    // Only conclusive investigations are indexed. Storing an inconclusive one
    // would fill memory with entries that say "we did not work this out",
    // which is worse than no match at all: it would crowd out useful history
    // and could steer a later investigation toward giving up.
    public async Task IndexIncidentAsync(InvestigationContext context, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || context.Report is null)
        {
            return;
        }

        var report = context.Report;
        var incident = context.Incident;

        var content = BuildIncidentMemory(incident, report);

        try
        {
            var embedding = await _embeddings.EmbedAsync(content, cancellationToken);

            await _memory.UpsertAsync(
                new MemoryRecord
                {
                    Kind = MemoryKind.Incident,
                    SourceRef = incident.Id.ToString(),
                    IncidentId = incident.Id,
                    Title = report.Title,
                    Content = content,
                    Workload = incident.AffectedWorkloads.FirstOrDefault(),
                    Category = incident.Category,
                    RootCauseCategory = report.RootCauseCategory,
                    Severity = incident.Severity.ToString(),
                    OccurredAtUtc = incident.FirstDetectedAtUtc
                },
                embedding,
                cancellationToken);

            _logger.LogInformation(
                "Indexed incident {IncidentId} into semantic memory as '{Title}'", incident.Id, report.Title);
        }
        catch (EmbeddingUnavailableException ex)
        {
            // Losing a memory entry is a shame but not a failure of the
            // investigation, whose report is already stored.
            _logger.LogWarning(ex, "Could not index incident {IncidentId} into semantic memory", incident.Id);
        }
    }

    // The text actually embedded for an incident.
    //
    // Deliberately written as a problem description rather than a report
    // extract: the symptoms someone would search for, then the cause that
    // explained them. Including the affected workloads and the root cause
    // category gives the embedding the vocabulary a future match needs.
    private static string BuildIncidentMemory(Incident incident, ReportResult report) =>
        $"""
         Incident: {report.Title}
         Category: {incident.Category}
         Affected workloads: {string.Join(", ", incident.AffectedWorkloads)}
         Severity: {incident.Severity}

         Symptoms: {incident.Title}
         {report.Summary}

         Root cause ({report.RootCauseCategory}): {report.LikelyRootCause}

         Impact: {report.Impact}

         Resolution guidance: {string.Join(" ", report.RecommendedActions)}
         """;

    // Runbooks are split on their top-level "## " headings.
    //
    // Section-level chunks are the right size here: a whole runbook covers
    // symptoms, causes and actions, and embedding all of it produces a vague
    // average that matches everything weakly. One section is a single coherent
    // idea and matches sharply.
    private static List<RunbookSection> LoadRunbookSections()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var sections = new List<RunbookSection>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(RunbookResourcePrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".md", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var markdown = reader.ReadToEnd();

            var fileName = resourceName[RunbookResourcePrefix.Length..].Replace(".md", string.Empty, StringComparison.Ordinal);
            sections.AddRange(SplitSections(markdown, fileName));
        }

        return sections;
    }

    private static IEnumerable<RunbookSection> SplitSections(string markdown, string fileName)
    {
        var lines = markdown.Split('\n');
        var documentTitle = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim()
                            ?? fileName;

        var category = CategoryFor(fileName);

        var currentHeading = string.Empty;
        var body = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (currentHeading.Length > 0 && body.Length > 0)
                {
                    yield return new RunbookSection(fileName, documentTitle, currentHeading, body.ToString().Trim(), category);
                }

                currentHeading = line[3..].Trim();
                body.Clear();
                continue;
            }

            if (currentHeading.Length > 0)
            {
                body.AppendLine(line);
            }
        }

        if (currentHeading.Length > 0 && body.Length > 0)
        {
            yield return new RunbookSection(fileName, documentTitle, currentHeading, body.ToString().Trim(), category);
        }
    }

    // Maps a runbook file to the incident category it addresses, so retrieval
    // can be filtered to guidance about the right kind of problem.
    private static string? CategoryFor(string fileName) => fileName switch
    {
        "dependency-latency" => IncidentCategory.DependencyLatency,
        "pod-crash-loop" => IncidentCategory.PodRestartLoop,
        "out-of-memory" => IncidentCategory.OutOfMemory,
        "database-unavailable" => IncidentCategory.DependencyUnavailable,
        "readiness-failure" => IncidentCategory.ReadinessFailure,
        _ => null
    };

    private sealed record RunbookSection(
        string FileName,
        string DocumentTitle,
        string Heading,
        string Body,
        string? Category)
    {
        public string SourceRef => $"{FileName}#{Heading.ToLowerInvariant().Replace(' ', '-')}";

        public string Title => $"{DocumentTitle} - {Heading}";

        // The document title is repeated into the embedded text so a section
        // called "Symptoms" is not embedded as generic prose detached from
        // the problem it describes.
        public string ToEmbeddableText() =>
            $"""
             {DocumentTitle}
             {Heading}

             {Body}
             """;
    }
}
