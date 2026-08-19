using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace KubeSage.Platform.Modules.AgentWorkflows;

// Turns the investigation tool allow-list into AIFunctions the Agent Framework
// can offer to the model.
//
// Each function is bound to ONE investigation's context, so the budget and the
// evidence pool it touches belong to that run and cannot leak into another.
// That is why these are built per investigation rather than registered once.
//
// Every function returns TEXT describing what was found, and separately
// records the real Evidence objects in the context. The model therefore sees
// evidence identifiers it can cite, while the authoritative evidence stays in
// deterministic hands - the model cannot alter what was collected, only read a
// rendering of it.
public sealed class InvestigationToolFactory
{
    private readonly InvestigationTools _tools;
    private readonly ILogger<InvestigationToolFactory> _logger;

    public InvestigationToolFactory(InvestigationTools tools, ILogger<InvestigationToolFactory> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    public IReadOnlyList<AITool> CreateFor(InvestigationContext context, CancellationToken cancellationToken)
    {
        // Without this, a search for "incidents like this one" would happily
        // return this very incident and read as strong corroboration.
        _tools.CurrentIncidentId = context.IncidentId;

        return InvestigationTools.Descriptors
            .Select(descriptor => CreateFunction(descriptor, context, cancellationToken))
            .ToList();
    }

    private AITool CreateFunction(
        ToolDescriptor descriptor,
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        // Arguments arrive as a loose bag because the model decides what to
        // send. They are converted to JSON and handed to the tool layer, which
        // validates and clamps every one of them before any query runs.
        return AIFunctionFactory.Create(
            async (AIFunctionArguments arguments) =>
                await InvokeAsync(descriptor.Name, arguments, context, cancellationToken),
            descriptor.Name,
            descriptor.Description);
    }

    private async Task<string> InvokeAsync(
        string toolName,
        AIFunctionArguments arguments,
        InvestigationContext context,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(
            arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

        var call = new ToolCall(toolName, payload);
        var result = await _tools.ExecuteAsync(call, context.Budget, cancellationToken);

        context.ToolsUsed.Add(toolName);

        if (!result.Succeeded)
        {
            // Returned as ordinary text so the agent can adapt. A rejected
            // call is not a crash - it is the boundary doing its job, and the
            // agent should carry on with what it already has.
            return result.Message ?? "The tool call was rejected.";
        }

        var added = context.AddEvidence(result.Evidence);

        _logger.LogInformation(
            "Tool {Tool} added {NewCount} new evidence item(s) of {ReturnedCount} returned",
            toolName, added, result.Evidence.Count);

        return Render(result.Evidence, context.Budget.Remaining);
    }

    // Renders evidence for the model, inside the same fenced blocks the main
    // prompt uses, so tool output is visibly untrusted data too.
    private static string Render(IReadOnlyList<Telemetry.Evidence> evidence, int remainingCalls)
    {
        if (evidence.Count == 0)
        {
            return "No evidence matched that query. That absence may itself be informative.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Collected {evidence.Count} evidence item(s). Cite them by id.");
        builder.AppendLine();

        PromptBuilder.AppendEvidence(builder, evidence);

        builder.AppendLine($"({remainingCalls} tool call(s) remaining in this investigation.)");

        return builder.ToString();
    }
}
