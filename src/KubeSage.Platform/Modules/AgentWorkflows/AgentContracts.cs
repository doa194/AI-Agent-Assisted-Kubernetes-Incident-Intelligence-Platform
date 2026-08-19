using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// The structured shapes the three agents must return.
//
// Every agent is constrained by a JSON schema at generation time and its
// output is validated afterwards. Free-form prose is never accepted as an
// agent result, because prose cannot be checked: there is no way to confirm
// that a paragraph only cited evidence that actually exists.
//
// Note what is absent from all three: any field for the model's reasoning.
// Conclusions are recorded, the thinking behind them is not.

// ---------------------------------------------------------------------------
// Triage
// ---------------------------------------------------------------------------

// Decides whether a candidate deserves a full investigation.
//
// This exists mainly to protect the time budget. On hardware where an
// investigation takes minutes, spending that on a self-resolving blip means a
// real incident waits behind it.
public sealed record TriageResult
{
    [JsonPropertyName("actionable")]
    public required bool Actionable { get; init; }

    // Triage may RAISE severity but never lower it below what the detection
    // rules assigned. A deterministic threshold that fired is a fact; the
    // model's opinion does not overrule it.
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("affectedWorkloads")]
    public required string[] AffectedWorkloads { get; init; }

    [JsonPropertyName("reasonSummary")]
    public required string ReasonSummary { get; init; }

    // What triage could not tell from the evidence it was given. Used to
    // direct the investigation rather than being discarded.
    [JsonPropertyName("missingEvidence")]
    public string[] MissingEvidence { get; init; } = [];

    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "actionable": { "type": "boolean" },
            "severity": { "type": "string", "enum": ["Low", "Medium", "High", "Critical"] },
            "affectedWorkloads": { "type": "array", "items": { "type": "string" } },
            "reasonSummary": { "type": "string" },
            "missingEvidence": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["actionable", "severity", "affectedWorkloads", "reasonSummary"]
        }
        """);
}

// ---------------------------------------------------------------------------
// Investigation
// ---------------------------------------------------------------------------

public sealed record HypothesisResult
{
    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    // A short machine-comparable label, so an investigation can be scored
    // against a known outcome without matching English wording.
    [JsonPropertyName("rootCauseCategory")]
    public required string RootCauseCategory { get; init; }

    // The workload actually believed to be at fault. For a dependency problem
    // this is deliberately NOT the workload showing the errors, and getting
    // that distinction right is the main thing an investigation is for.
    [JsonPropertyName("suspectedWorkload")]
    public required string SuspectedWorkload { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    // Identifiers of the evidence supporting this hypothesis. Validated
    // against the collected evidence; a hypothesis citing something that does
    // not exist is rejected rather than corrected.
    //
    // The schema caps this list. A hypothesis supported by forty pieces of
    // evidence is less discriminating than one supported by four, and an
    // unbounded array once ran past the output token limit and truncated the
    // whole response mid-generation.
    [JsonPropertyName("evidenceIds")]
    public required string[] EvidenceIds { get; init; }
}

public sealed record InvestigationResult
{
    // "Inconclusive" is a first-class outcome, not a failure. An honest
    // admission that the evidence does not support a conclusion is worth far
    // more than a confident wrong answer.
    [JsonPropertyName("conclusive")]
    public required bool Conclusive { get; init; }

    [JsonPropertyName("hypotheses")]
    public required HypothesisResult[] Hypotheses { get; init; }

    [JsonPropertyName("impactSummary")]
    public required string ImpactSummary { get; init; }

    [JsonPropertyName("evidenceGaps")]
    public string[] EvidenceGaps { get; init; } = [];

    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "conclusive": { "type": "boolean" },
            "impactSummary": { "type": "string" },
            "hypotheses": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "statement": { "type": "string" },
                  "rootCauseCategory": { "type": "string" },
                  "suspectedWorkload": { "type": "string" },
                  "confidence": { "type": "number" },
                  "evidenceIds": { "type": "array", "items": { "type": "string" }, "maxItems": 6 }
                },
                "required": ["statement", "rootCauseCategory", "suspectedWorkload", "confidence", "evidenceIds"]
              }
            },
            "evidenceGaps": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["conclusive", "hypotheses", "impactSummary"]
        }
        """);
}

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------

public sealed record ReportResult
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("impact")]
    public required string Impact { get; init; }

    [JsonPropertyName("timeline")]
    public string[] Timeline { get; init; } = [];

    [JsonPropertyName("likelyRootCause")]
    public required string LikelyRootCause { get; init; }

    [JsonPropertyName("rootCauseCategory")]
    public required string RootCauseCategory { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("alternativeHypotheses")]
    public string[] AlternativeHypotheses { get; init; } = [];

    // What a human might do about it. Recommendations only - the platform
    // never acts on them, and has no permission to.
    [JsonPropertyName("recommendedActions")]
    public string[] RecommendedActions { get; init; } = [];

    // How to confirm the diagnosis independently. This is what makes a report
    // checkable rather than merely readable.
    [JsonPropertyName("verificationSteps")]
    public string[] VerificationSteps { get; init; } = [];

    [JsonPropertyName("evidenceIds")]
    public required string[] EvidenceIds { get; init; }

    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "summary": { "type": "string" },
            "impact": { "type": "string" },
            "timeline": { "type": "array", "items": { "type": "string" } },
            "likelyRootCause": { "type": "string" },
            "rootCauseCategory": { "type": "string" },
            "confidence": { "type": "number" },
            "alternativeHypotheses": { "type": "array", "items": { "type": "string" } },
            "recommendedActions": { "type": "array", "items": { "type": "string" } },
            "verificationSteps": { "type": "array", "items": { "type": "string" } },
            "evidenceIds": { "type": "array", "items": { "type": "string" }, "maxItems": 10 }
          },
          "required": ["title", "summary", "impact", "likelyRootCause", "rootCauseCategory", "confidence", "evidenceIds"]
        }
        """);
}
