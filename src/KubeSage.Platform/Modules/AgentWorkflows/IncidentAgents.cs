using System.Text.Json;
using KubeSage.Platform.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Builds the three agents the investigation workflow uses.
//
// Each is a Microsoft Agent Framework ChatClientAgent over the Ollama adapter,
// configured with the JSON schema it must answer with. Constraining generation
// with a schema is what makes the output machine-checkable, and being
// machine-checkable is what allows the validator to reject unsupported claims.
//
// Only ONE of the three is given tools. That asymmetry is deliberate and is
// the core of the security model: triage and reporting reason over evidence
// they were handed, and cannot reach the cluster at all.
public sealed class IncidentAgents
{
    private readonly IChatClient _chatClient;
    private readonly OllamaOptions _options;

    public IncidentAgents(
        [FromKeyedServices(ChatClientKey)] IChatClient chatClient,
        IOptions<KubeSageOptions> options)
    {
        _chatClient = chatClient;
        _options = options.Value.Ollama;
    }

    public const string ChatClientKey = "kubesage-ollama";

    public const string TriageAgentName = "triage";
    public const string InvestigationAgentName = "investigation";
    public const string ReportAgentName = "report";
    public const string ClusterAnalysisAgentName = "cluster-analysis";

    // Decides whether an incident is worth a full investigation. No tools: it
    // judges only the evidence already collected deterministically.
    public AIAgent CreateTriageAgent() => new ChatClientAgent(
        _chatClient,
        new ChatClientAgentOptions
        {
            Name = TriageAgentName,
            Description = "Decides whether an incident candidate is actionable and how severe it is.",
            ChatOptions = SchemaOptions(
                PromptBuilder.TriageSystemPrompt(), TriageResult.Schema, maxTokens: 600)
        });

    // The main reasoning agent, and the only one with tools.
    public AIAgent CreateInvestigationAgent(IReadOnlyList<AITool> tools) => new ChatClientAgent(
        _chatClient,
        new ChatClientAgentOptions
        {
            Name = InvestigationAgentName,
            Description = "Correlates evidence and ranks likely root causes.",
            ChatOptions = SchemaOptions(
                PromptBuilder.InvestigationSystemPrompt(InvestigationTools.Descriptors),
                InvestigationResult.Schema, maxTokens: 1600, tools: tools)
        });

    // Turns a validated investigation into an operator-facing report. No
    // tools: it must not be able to introduce evidence the investigation did
    // not have, which is how a report stays traceable to what was collected.
    public AIAgent CreateReportAgent() => new ChatClientAgent(
        _chatClient,
        new ChatClientAgentOptions
        {
            Name = ReportAgentName,
            Description = "Writes the final evidence-backed incident report.",
            ChatOptions = SchemaOptions(
                PromptBuilder.ReportSystemPrompt(), ReportResult.Schema, maxTokens: 1800)
        });

    // Summarises whole-cluster health for the startup and periodic reports.
    //
    // Not one of the three incident agents: it answers "how is everything"
    // rather than "why did this break", and it has no tools for the same
    // reason the triage and report agents have none - it reasons only over
    // evidence that was collected for it.
    public AIAgent CreateClusterAnalysisAgent() => new ChatClientAgent(
        _chatClient,
        new ChatClientAgentOptions
        {
            Name = ClusterAnalysisAgentName,
            Description = "Summarises overall cluster health for the startup and periodic reports.",
            ChatOptions = SchemaOptions(
                PromptBuilder.ClusterAnalysisSystemPrompt(),
                ClusterHealthResult.Schema,
                maxTokens: 1200)
        });

    // Instructions are carried on ChatOptions rather than passed to the agent
    // constructor, because that is the only overload that also accepts a
    // response schema - and the schema is what makes the output checkable.
    private ChatOptions SchemaOptions(
        string instructions,
        JsonElement schema,
        int maxTokens,
        IReadOnlyList<AITool>? tools = null)
    {
        var options = new ChatOptions
        {
            Instructions = instructions,
            ModelId = _options.ChatModel,
            Temperature = (float)_options.Temperature,
            MaxOutputTokens = maxTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema),
            // Reasoning is off by default. On this hardware the reasoning
            // channel generates at the same few tokens per second as the
            // answer, so enabling it roughly doubles every call for a task
            // that is mostly evidence comparison rather than deduction.
            AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = false }
        };

        if (tools is { Count: > 0 })
        {
            options.Tools = [.. tools];
        }

        return options;
    }
}
