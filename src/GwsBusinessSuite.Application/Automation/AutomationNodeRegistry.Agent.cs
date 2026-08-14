using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Application.Automation;

// "Goal-driven agent workflows" - the ai.agent node type. Given a goal and an explicit
// allowlist of other node types it may call as tools, it loops using Ollama's native
// /api/chat tool-calling (IOllamaService.ChatAsync/OllamaToolDefinition - already implemented
// for a "SentinelGptToolCallLoop" that doesn't exist yet in this codebase, so this is its
// first real caller) rather than asking the model to hand-write JSON in free text. Each tool
// call executes through this SAME registry's ExecuteAsync, so a tool call is indistinguishable
// from a human-authored node. Every step is recorded in the node's own output for audit.
public sealed partial class AutomationNodeRegistry
{
    // Configurable per node up to this ceiling, regardless of what the workflow author sets -
    // an unbounded or very large step budget on an autonomous loop is a real cost/runaway risk.
    private const int AgentHardMaxSteps = 10;

    // Node types an agent may never call, regardless of its own allowedTools list: triggers
    // don't make sense as an ad-hoc tool call; core.wait/core.approval need the execution
    // engine's own pause/resume machinery and throw if invoked directly; ai.agent and
    // automation.subWorkflow are excluded to keep an agent's tool graph flat rather than
    // opening unbounded recursion.
    private static readonly HashSet<string> AgentForbiddenTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "core.manualTrigger", "core.webhookTrigger", "core.scheduleTrigger",
        "database.rowChangedTrigger", "crm.dealStageChangedTrigger", "cms.pagePublishedTrigger",
        "core.wait", "core.approval", "ai.agent", "automation.subWorkflow"
    };

    private const string AgentSystemPrompt =
        "You are an autonomous workflow agent working toward a specific goal. Call the " +
        "available tools as needed, one at a time, to make progress toward the goal. When you " +
        "have completed the goal, or determined it cannot be completed, respond in plain text " +
        "with your final answer and do not call any more tools. Never claim an action succeeded " +
        "that you did not actually call a tool for.";

    private async Task<AutomationNodeRunResult> ExecuteAgentAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        CancellationToken cancellationToken)
    {
        var ai = ollama ?? throw new InvalidOperationException("Ollama is not available to the automation engine.");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var model = RequireSafeModelName(parameters["model"]?.GetValue<string>(), node.Name);
        var goal = parameters["goal"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(goal))
            throw new InvalidOperationException($"{node.Name} requires a goal.");
        var outputField = RequireSafeFieldName(parameters["outputField"]?.GetValue<string>() ?? "agentResult", node.Name);
        var maxSteps = Math.Clamp(parameters["maxSteps"]?.GetValue<int>() ?? 5, 1, AgentHardMaxSteps);

        var allowedTools = (parameters["allowedTools"] as JsonArray)?
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (allowedTools.Count == 0)
            throw new InvalidOperationException($"{node.Name} needs at least one tool listed in allowedTools.");

        var toolDefinitions = new List<OllamaToolDefinition>();
        foreach (var toolTypeKey in allowedTools)
        {
            if (AgentForbiddenTools.Contains(toolTypeKey))
                throw new InvalidOperationException($"{node.Name} cannot allow '{toolTypeKey}' - that node type isn't permitted as an agent tool.");
            var definition = Find(toolTypeKey)
                ?? throw new InvalidOperationException($"{node.Name} allows an unknown tool '{toolTypeKey}'.");
            toolDefinitions.Add(new OllamaToolDefinition(
                definition.TypeKey, definition.Description, BuildJsonSchemaFromExample(definition.DefaultParametersJson)));
        }

        var messages = new List<OllamaChatMessage>
        {
            new("system", AgentSystemPrompt),
            new("user", $"GOAL: {LimitText(goal, 4000)}")
        };
        var transcript = new JsonArray();
        string? finalAnswer = null;
        var finished = false;

        for (var step = 1; step <= maxSteps && !finished; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await ai.ChatAsync(model, messages, toolDefinitions, cancellationToken);

            if (response.ToolCalls.Count == 0)
            {
                finalAnswer = response.Content;
                finished = true;
                transcript.Add(new JsonObject { ["step"] = step, ["action"] = "finish" });
                break;
            }

            // Only the first requested call is honored per turn - keeps step accounting and
            // the transcript one-call-per-step, matching this node's own maxSteps contract.
            var call = response.ToolCalls[0];
            var callId = $"call-{step}";
            messages.Add(new OllamaChatMessage("assistant", response.Content, ToolCalls: [call]));

            if (!allowedTools.Contains(call.Name, StringComparer.OrdinalIgnoreCase) || AgentForbiddenTools.Contains(call.Name))
            {
                var rejection = $"Rejected: '{call.Name}' is not in your allowed tool list. Choose one of: {string.Join(", ", allowedTools)}.";
                messages.Add(new OllamaChatMessage("tool", rejection, ToolCallId: callId, Name: call.Name));
                transcript.Add(new JsonObject { ["step"] = step, ["action"] = "call_tool", ["tool"] = call.Name, ["rejected"] = true, ["observation"] = rejection });
                continue;
            }

            string observation;
            JsonNode? argumentsNode = null;
            try
            {
                argumentsNode = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? new JsonObject() : JsonNode.Parse(call.ArgumentsJson);
                var toolNode = new AutomationNodeSnapshot(
                    Guid.NewGuid(), call.Name, call.Name, 1,
                    (argumentsNode as JsonObject)?.ToJsonString() ?? "{}", null, false, false, false, 1, 0, 0);
                var toolResult = await ExecuteAsync(toolNode, input, credentialJson, cancellationToken: cancellationToken);
                observation = LimitText(toolResult.DisplayOutputJson, 4000);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observation = $"Tool call failed: {ex.Message}";
            }

            messages.Add(new OllamaChatMessage("tool", observation, ToolCallId: callId, Name: call.Name));
            transcript.Add(new JsonObject
            {
                ["step"] = step,
                ["action"] = "call_tool",
                ["tool"] = call.Name,
                ["parameters"] = argumentsNode?.DeepClone(),
                ["observation"] = observation
            });
        }

        finalAnswer ??= $"Stopped after {maxSteps} step(s) without finishing.";

        var source = RequireObject(input, node.Name);
        var output = source.DeepClone().AsObject();
        output[outputField] = new JsonObject
        {
            ["goal"] = goal,
            ["finished"] = finished,
            ["finalAnswer"] = finalAnswer,
            ["stepCount"] = transcript.Count,
            ["steps"] = transcript.DeepClone()
        };
        var cloned = JsonSerializer.SerializeToElement(output).Clone();
        return new AutomationNodeRunResult(
            new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase) { ["main"] = [cloned] },
            cloned.GetRawText());
    }

    // Derives a loose JSON Schema from a node type's DefaultParametersJson example (property
    // names + inferred types) rather than requiring a hand-authored schema per tool - good
    // enough for a tool-calling model to know what fields exist and roughly what shape they
    // are, without this codebase needing ~20 parallel schema definitions to maintain.
    private static string BuildJsonSchemaFromExample(string exampleJson)
    {
        if (JsonNode.Parse(exampleJson) is not JsonObject example)
        {
            return """{"type":"object"}""";
        }

        var properties = new JsonObject();
        foreach (var (key, value) in example)
        {
            properties[key] = new JsonObject { ["type"] = InferSchemaType(value) };
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties }.ToJsonString();
    }

    private static string InferSchemaType(JsonNode? value) => value switch
    {
        JsonArray => "array",
        JsonObject => "object",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<double>(out _) => "number",
        _ => "string"
    };
}
