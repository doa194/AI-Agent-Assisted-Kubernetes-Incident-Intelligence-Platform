using System.ComponentModel.DataAnnotations;

namespace KubeSage.Platform.Configuration;

// Every tunable value in the platform lives in this one options tree, bound
// from the "KubeSage" configuration section.
//
// Why one tree: the platform runs unattended. When it behaves unexpectedly at
// three in the morning, an operator needs a single place to look up what
// thresholds, budgets and time windows were actually in force. Scattering
// magic numbers through the code would make that impossible.
//
// Everything is validated at start-up (see ConfigurationExtensions). The
// process refuses to start on invalid configuration rather than failing later
// in the middle of an investigation.
public sealed record KubeSageOptions
{
    public const string SectionName = "KubeSage";

    [Required]
    public DatabaseOptions Database { get; init; } = new();

    [Required]
    public OllamaOptions Ollama { get; init; } = new();

    [Required]
    public TelemetryOptions Telemetry { get; init; } = new();

    [Required]
    public KubernetesOptions Kubernetes { get; init; } = new();

    [Required]
    public DetectionOptions Detection { get; init; } = new();

    [Required]
    public AnalysisOptions Analysis { get; init; } = new();

    [Required]
    public InvestigationOptions Investigation { get; init; } = new();

    [Required]
    public RetrievalOptions Retrieval { get; init; } = new();
}

public sealed record DatabaseOptions
{
    // Used by normal operation. This should point at the low-privilege
    // application role, which cannot change the schema.
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    // Used only by the migration runner at start-up. Separated from the main
    // connection string so day-to-day queries never run with schema-owner
    // rights. If left empty, migrations run with ConnectionString instead,
    // which is convenient for tests using a throwaway database.
    public string? MigrationConnectionString { get; init; }

    [Range(1, 200)]
    public int MaxPoolSize { get; init; } = 20;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    public bool RunMigrationsOnStartup { get; init; } = true;

    // Resolves which connection string the migration runner should use.
    public string EffectiveMigrationConnectionString =>
        string.IsNullOrWhiteSpace(MigrationConnectionString)
            ? ConnectionString
            : MigrationConnectionString;
}

public sealed record OllamaOptions
{
    [Required]
    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    [Required(AllowEmptyStrings = false)]
    public string ChatModel { get; init; } = "gemma4:12b";

    [Required(AllowEmptyStrings = false)]
    public string EmbeddingModel { get; init; } = "embeddinggemma:300m";

    // Context window given to the reasoning model.
    //
    // Sized to the prompts this platform actually sends, which run to a few
    // thousand tokens once evidence is bounded. Oversizing is not free: the
    // key/value cache is allocated up front and competes with model weights
    // for video memory, so a 16k window measurably pushed layers onto the CPU
    // and slowed prompt processing. Evidence is trimmed to fit this rather
    // than this being raised to fit the evidence.
    [Range(2048, 131072)]
    public int ContextTokens { get; init; } = 8192;

    // A 12B model on modest hardware can take minutes for one response, so
    // this timeout is deliberately generous. It exists to catch a hung model
    // server, not to enforce responsiveness.
    [Range(30, 3600)]
    public int RequestTimeoutSeconds { get; init; } = 900;

    // Low temperature: this is an analysis task where reproducibility matters
    // more than variety of phrasing.
    [Range(0.0, 2.0)]
    public double Temperature { get; init; } = 0.1;

    // Dimension of the embedding vectors. EmbeddingGemma produces 768 values.
    // The database column is sized from this, so changing the embedding model
    // requires a migration, not just a configuration change.
    [Range(64, 4096)]
    public int EmbeddingDimensions { get; init; } = 768;

    // How long to wait for the model server at start-up before declaring the
    // AI layer unavailable. Detection keeps working without it.
    [Range(0, 600)]
    public int StartupProbeTimeoutSeconds { get; init; } = 30;
}

public sealed record TelemetryOptions
{
    [Required]
    public Uri LokiEndpoint { get; init; } = new("http://localhost:3100");

    [Required]
    public Uri PrometheusEndpoint { get; init; } = new("http://localhost:9090");

    [Range(5, 300)]
    public int QueryTimeoutSeconds { get; init; } = 30;

    // Hard ceiling on how much log data any single query may return. This is
    // a safety limit for the whole platform: it protects Loki from an
    // expensive query and protects the model's context window from being
    // flooded by an agent asking for too much at once.
    [Range(10, 5000)]
    public int MaxLogLinesPerQuery { get; init; } = 500;

    // Hard ceiling on how far back any single query may look.
    [Range(1, 1440)]
    public int MaxQueryRangeMinutes { get; init; } = 120;

    // Kubernetes namespace containing the observed demo workload.
    [Required(AllowEmptyStrings = false)]
    public string WorkloadNamespace { get; init; } = "kubesage-demo";
}

public sealed record KubernetesOptions
{
    // Path to a kubeconfig file. When empty the client falls back to the
    // default resolution order, which is what happens during local
    // development outside a container.
    public string? KubeConfigPath { get; init; }

    // Namespaces the platform is allowed to read. Anything outside this list
    // is rejected before a request reaches the Kubernetes API, so a confused
    // or manipulated agent cannot browse the whole cluster.
    //
    // Deliberately EMPTY here, with the real list in appsettings.json. The
    // configuration binder ADDS to an array that already has values instead of
    // replacing it, so a default written here would be impossible to remove:
    // the list could be widened by configuration but never narrowed, and a
    // security boundary that only loosens is worse than useless.
    //
    // Empty is safe because the attributes below are enforced at start-up, so
    // a missing list stops the platform rather than silently allowing nothing
    // (or, worse, everything).
    [Required, MinLength(1)]
    public string[] AllowedNamespaces { get; init; } = [];

    [Range(5, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    // Upper bound on items returned by any single Kubernetes list call.
    [Range(10, 2000)]
    public int MaxItemsPerQuery { get; init; } = 200;
}

public sealed record DetectionOptions
{
    // Master switch. Turning detection off leaves the API and telemetry
    // adapters usable, which is handy when investigating the platform itself.
    public bool Enabled { get; init; } = true;

    // Length of the sliding window each rule evaluates.
    [Range(1, 60)]
    public int EvaluationWindowMinutes { get; init; } = 5;

    // How often the detection loop runs.
    [Range(15, 3600)]
    public int EvaluationIntervalSeconds { get; init; } = 60;

    // After an incident with a given fingerprint is raised, the same
    // fingerprint is suppressed for this long. Without it, a five minute
    // outage evaluated every minute would create five duplicate incidents.
    [Range(1, 1440)]
    public int DeduplicationCooldownMinutes { get; init; } = 15;

    // An incident is considered recovered once its condition has been absent
    // for this long.
    [Range(1, 1440)]
    public int RecoveryConfirmationMinutes { get; init; } = 10;

    [Required]
    public DetectionThresholds Thresholds { get; init; } = new();
}

// Threshold values for the deterministic detection rules. These are plain
// numbers evaluated by ordinary code. No model is involved in deciding
// whether something is wrong.
public sealed record DetectionThresholds
{
    // Fraction of requests failing with a 5xx status, between 0 and 1.
    [Range(0.0, 1.0)]
    public double HttpErrorRate { get; init; } = 0.10;

    // A rule only fires when it has seen at least this many requests in the
    // window. Stops a single failed request out of two from being reported as
    // a fifty percent error rate.
    [Range(1, 10000)]
    public int MinimumRequestSample { get; init; } = 20;

    // 95th percentile request duration considered abnormal.
    [Range(0.01, 300.0)]
    public double LatencyP95Seconds { get; init; } = 1.5;

    // Additional pod restarts within the window that count as a problem.
    [Range(1, 100)]
    public int PodRestartIncrease { get; init; } = 2;

    // How many pods of a workload may be unready before it is an incident.
    [Range(1, 100)]
    public int UnreadyPodCount { get; init; } = 1;

    // Occurrences of the same normalised error signature within the window.
    [Range(2, 10000)]
    public int RepeatedErrorSignatureCount { get; init; } = 10;

    // Dependency failures (database, downstream HTTP) within the window.
    [Range(1, 10000)]
    public int DependencyFailureCount { get; init; } = 5;
}

public sealed record AnalysisOptions
{
    // Startup analysis: how long to let telemetry accumulate after the
    // cluster reports ready before producing the first cluster report.
    // Querying Loki immediately after start-up returns almost nothing, which
    // would produce a useless first report.
    [Range(0, 3600)]
    public int StartupWarmupSeconds { get; init; } = 120;

    public bool RunStartupAnalysis { get; init; } = true;

    // Periodic health analysis window.
    [Range(60, 86400)]
    public int ScheduledIntervalSeconds { get; init; } = 300;

    public bool RunScheduledAnalysis { get; init; } = true;
}

public sealed record InvestigationOptions
{
    // How many investigations may run at once. One by default: a local 12B
    // model cannot serve two investigations concurrently without both of them
    // slowing to a crawl.
    [Range(1, 8)]
    public int MaxConcurrent { get; init; } = 1;

    // Total wall-clock budget for a single investigation across all three
    // agents. Exceeding it produces a Failed investigation that can be
    // retried, not a half-written report.
    [Range(60, 7200)]
    public int TimeoutSeconds { get; init; } = 1800;

    // Upper bound on how many evidence-gathering tool calls the Investigation
    // Agent may make. This is the main defence against an agent looping.
    [Range(1, 100)]
    public int MaxToolCalls { get; init; } = 20;

    // Upper bound on distinct evidence items handed to the model.
    [Range(1, 500)]
    public int MaxEvidenceItems { get; init; } = 60;

    // Failed investigations are retried this many times before being left in
    // the Failed state for an operator to look at.
    [Range(0, 10)]
    public int MaxRetries { get; init; } = 3;

    [Range(1, 3600)]
    public int RetryBaseDelaySeconds { get; init; } = 30;

    // How often the durable work queue is polled for pending work.
    [Range(1, 300)]
    public int DispatcherPollSeconds { get; init; } = 5;

    // Work claimed by a process that then died is released after this long so
    // another process (or the same one after a restart) can pick it up.
    //
    // This must comfortably exceed TimeoutSeconds. The margin covers the time
    // between an investigation hitting its own budget and the worker finishing
    // its clean-up; without it, a still-running investigation could be claimed
    // a second time and produce a duplicate report.
    [Range(60, 7200)]
    public int WorkLeaseSeconds { get; init; } = 2400;
}

public sealed record RetrievalOptions
{
    // Turning this off disables semantic memory entirely; investigations then
    // rely only on live telemetry.
    public bool Enabled { get; init; } = true;

    [Range(1, 50)]
    public int TopK { get; init; } = 5;

    // Cosine distance above which a match is considered irrelevant. Returning
    // a weak match is worse than returning nothing, because the agent may
    // treat an unrelated past incident as a clue.
    [Range(0.0, 2.0)]
    public double MaxDistance { get; init; } = 0.65;

    // Runbooks are re-indexed at start-up when their content hash changes.
    public bool IndexRunbooksOnStartup { get; init; } = true;
}
