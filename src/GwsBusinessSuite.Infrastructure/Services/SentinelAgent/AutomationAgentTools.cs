using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Automations for the agent (Phase C, 30-10-26), through the workflow and execution services like
// the automation pages. Admin only: for anyone else the editor checks per-workflow grants, which
// this doesn't repeat. Anything that can act outside the app waits for a Confirm: making a
// workflow (the old answer-only "propose a workflow" card, now a Confirm), running one, and
// switching one on. Switching off is routine and has Undo.
public sealed class AutomationAgentTools(
    IAutomationWorkflowService workflows,
    IAutomationExecutionService executions,
    IAutomationNodeRegistry registry) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int MaxNodes = 15;
    private const int NodeTypesShown = 25;
    private const int RunsShown = 5;

    public SentinelAgentArea Area { get; } = new("automations", "automation workflows: list, make, run, switch on or off", ["automat", "workflow", "trigger", "webhook", "every day", "every week", "every monday"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "list_automations",
            "List the automation workflows with their ids, whether each is on, and when each last ran.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at the automations",
            ListAsync),
        new SentinelAgentTool(
            "read_automation",
            "Read one automation workflow: its steps (nodes) in order, how they connect, and its latest runs.",
            SentinelAgentJson.Schema(("workflowId", "string", "The workflow id", true)),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading an automation",
            ReadAsync),
        new SentinelAgentTool(
            "automation_node_types",
            "The steps an automation can use (typeKey, what it does, its default settings). Search for what you need, e.g. 'email', 'crm' or 'schedule'. Call this before create_automation.",
            SentinelAgentJson.Schema(("search", "string", "Words to look for; leave out for every typeKey by category", false)),
            SentinelAgentRisk.Read, Roles,
            args => args.OptionalText("search") is { } search ? $"Looking up automation steps for '{search}'" : "Looking up automation steps",
            NodeTypesAsync),
        new SentinelAgentTool(
            "create_automation",
            "Propose a new automation workflow from steps you choose with automation_node_types: one trigger first, then the rest. Connections can be left out to run the steps in the order given. Only proposes; once confirmed it's made switched off, as a draft to check in the editor.",
            CreateSchema,
            SentinelAgentRisk.Confirm, Roles,
            args => $"Proposing the automation '{args.OptionalText("name")}'",
            CreateAsync,
            UndoCreateAsync,
            (args, _, _) => Task.FromResult(Preview(Graph(args)))),
        new SentinelAgentTool(
            "run_automation",
            "Run an automation workflow now, from its published version. It may send email or call other services, so this only proposes: the person confirms first.",
            SentinelAgentJson.Schema(
                ("workflowId", "string", "The workflow id", true),
                ("input", "string", "Optional: a JSON object the trigger passes to the first step", false)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to run an automation",
            RunAsync,
            preview: async (args, _, ct) =>
            {
                var workflow = await WorkflowAsync(args.Id("workflowId"), ct);
                if (workflow.CurrentVersion == 0)
                    throw new SentinelAgentToolException($"'{workflow.Name}' has never been published, so it can't run yet. Switching it on publishes it.");
                Input(args);
                return $"Run the automation '{workflow.Name}' now";
            }),
        new SentinelAgentTool(
            "turn_on_automation",
            "Switch an automation workflow on, so its trigger (a schedule, a webhook, a CRM change) runs it. Its latest draft is checked and published first. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(("workflowId", "string", "The workflow id", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to switch an automation on",
            TurnOnAsync,
            UndoTurnOnAsync,
            async (args, _, ct) =>
            {
                var workflow = await ReadyToTurnOnAsync(args.Id("workflowId"), ct);
                return $"Publish and switch on the automation '{workflow.Name}'";
            }),
        new SentinelAgentTool(
            "turn_off_automation",
            "Switch an automation workflow off, so nothing runs it until it's switched on again.",
            SentinelAgentJson.Schema(("workflowId", "string", "The workflow id", true)),
            SentinelAgentRisk.Change, Roles,
            _ => "Switching an automation off",
            TurnOffAsync,
            UndoTurnOffAsync)
    ];

    public static string LinkTo(Guid workflowId) => $"/admin/automation/{workflowId}";

    private static readonly string CreateSchema = """
        {"type":"object","properties":{
          "name":{"type":"string","description":"The workflow's name"},
          "description":{"type":"string","description":"What it does, in a sentence"},
          "nodes":{"type":"array","description":"The steps, trigger first","items":{"type":"object","properties":{
            "name":{"type":"string","description":"A short label, different for each step"},
            "typeKey":{"type":"string","description":"A typeKey from automation_node_types"},
            "parameters":{"type":"object","description":"The step's settings, shaped like its default settings; leave out to use the defaults"}},
            "required":["name","typeKey"]}},
          "connections":{"type":"array","description":"Optional: which step leads to which","items":{"type":"object","properties":{
            "from":{"type":"string","description":"The earlier step's name"},
            "to":{"type":"string","description":"The next step's name"},
            "fromOutput":{"type":"string","description":"Leave out for main; true or false after an If step"}},
            "required":["from","to"]}}},
          "required":["name","nodes"]}
        """;

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var list = await workflows.ListAsync(ct);
        return SentinelAgentToolResult.Of(new
        {
            automations = list.Select(workflow => new
            {
                id = workflow.Id,
                name = workflow.Name,
                description = workflow.Description,
                on = workflow.Status == AutomationWorkflowStatuses.Active,
                published = workflow.CurrentVersion > 0,
                steps = workflow.NodeCount,
                lastRan = workflow.LastExecutedAt?.ToString("yyyy-MM-dd HH:mm")
            })
        });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var workflow = await WorkflowAsync(args.Id("workflowId"), ct);
        var names = workflow.Nodes.ToDictionary(node => node.Id, node => node.Name);
        return SentinelAgentToolResult.Of(new
        {
            id = workflow.Id,
            name = workflow.Name,
            description = workflow.Description,
            on = workflow.Status == AutomationWorkflowStatuses.Active,
            publishedVersion = workflow.CurrentVersion,
            steps = workflow.Nodes.Select(node => new { name = node.Name, typeKey = node.TypeKey, off = node.IsDisabled ? true : (bool?)null }),
            connections = workflow.Connections.Select(connection =>
                $"{names.GetValueOrDefault(connection.SourceNodeId, "?")}{(connection.SourceOutput == "main" ? "" : $" ({connection.SourceOutput})")} -> {names.GetValueOrDefault(connection.TargetNodeId, "?")}"),
            latestRuns = workflow.RecentExecutions.Take(RunsShown)
                .Select(run => new { status = run.Status, mode = run.Mode, started = run.StartedAt?.ToString("yyyy-MM-dd HH:mm") })
        });
    }

    private Task<SentinelAgentToolResult> NodeTypesAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var definitions = registry.ListDefinitions();
        if (args.OptionalText("search") is not { } search)
        {
            return Task.FromResult(SentinelAgentToolResult.Of(new
            {
                note = "Search with a word to see what a step does and its settings.",
                categories = definitions.GroupBy(definition => definition.Category)
                    .ToDictionary(group => group.Key, group => group.Select(definition => definition.TypeKey))
            }));
        }

        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = definitions
            .Select(definition => (Definition: definition, Score: words.Count(word =>
                definition.TypeKey.Contains(word, StringComparison.OrdinalIgnoreCase)
                || definition.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)
                || definition.Category.Contains(word, StringComparison.OrdinalIgnoreCase)
                || definition.Description.Contains(word, StringComparison.OrdinalIgnoreCase))))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Definition.IsTrigger)
            .Take(NodeTypesShown)
            .Select(match => match.Definition)
            .ToList();
        return Task.FromResult(SentinelAgentToolResult.Of(new
        {
            found = matches.Count,
            steps = matches.Select(definition => new
            {
                typeKey = definition.TypeKey,
                name = definition.DisplayName,
                trigger = definition.IsTrigger ? true : (bool?)null,
                what = definition.Description,
                outputs = definition.Outputs.Count > 1 ? definition.Outputs : null,
                defaults = definition.DefaultParametersJson
            })
        }));
    }

    private async Task<SentinelAgentToolResult> CreateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var graph = Graph(args);
        var created = await workflows.CreateFromGraphAsync(graph.Name, graph.Description, graph.Nodes, graph.Connections, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { created = true, workflowId = created.Id, name = created.Name, steps = created.Nodes.Count, on = false },
            new SentinelAgentChange($"Made the automation '{created.Name}' ({Count(created.Nodes.Count, "step")}, switched off)", "automation", created.Id.ToString(), created.Name, LinkTo(created.Id),
                SentinelAgentJson.Serialize(new WorkflowUndo(created.Id))));
    }

    private async Task UndoCreateAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var workflow = await WorkflowAsync(SentinelAgentJson.Deserialize<WorkflowUndo>(undoJson).WorkflowId, ct);
        if (workflow.Status == AutomationWorkflowStatuses.Active)
            throw new SentinelAgentToolException($"'{workflow.Name}' has been switched on since. Switch it off first, then delete it in the editor.");
        await workflows.DeleteWorkflowAsync(workflow.Id, ct);
    }

    private async Task<SentinelAgentToolResult> RunAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var workflow = await WorkflowAsync(args.Id("workflowId"), ct);
        var run = await executions.ExecuteAsync(workflow.Id, Input(args), cancellationToken: ct);
        var outcome = run.Status == AutomationExecutionStatuses.Succeeded
            ? "it finished"
            : string.IsNullOrWhiteSpace(run.ErrorMessage) ? run.Status.ToLowerInvariant() : $"{run.Status.ToLowerInvariant()}: {run.ErrorMessage}";
        return SentinelAgentToolResult.Of(
            new { ran = workflow.Name, status = run.Status, error = string.IsNullOrWhiteSpace(run.ErrorMessage) ? null : run.ErrorMessage },
            new SentinelAgentChange($"Ran the automation '{workflow.Name}' ({outcome})", "automation-run", run.Id.ToString(), workflow.Name, LinkTo(workflow.Id), null));
    }

    private async Task<SentinelAgentToolResult> TurnOnAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var workflow = await ReadyToTurnOnAsync(args.Id("workflowId"), ct);
        var version = await workflows.PublishAsync(workflow.Id, "Published by SentinelGPT", ct);
        await workflows.SetActiveAsync(workflow.Id, true, ct);
        return SentinelAgentToolResult.Of(new { on = true, name = workflow.Name, version },
            new SentinelAgentChange($"Published and switched on the automation '{workflow.Name}' (version {version})", "automation", workflow.Id.ToString(), workflow.Name, LinkTo(workflow.Id),
                SentinelAgentJson.Serialize(new WorkflowUndo(workflow.Id))));
    }

    // Undo leaves the new published version in the history; it only stops the workflow running.
    private Task UndoTurnOnAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        workflows.SetActiveAsync(SentinelAgentJson.Deserialize<WorkflowUndo>(undoJson).WorkflowId, false, ct);

    private async Task<SentinelAgentToolResult> TurnOffAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var workflow = await WorkflowAsync(args.Id("workflowId"), ct);
        if (workflow.Status != AutomationWorkflowStatuses.Active) throw new SentinelAgentToolException($"'{workflow.Name}' is already off.");
        await workflows.SetActiveAsync(workflow.Id, false, ct);
        return SentinelAgentToolResult.Of(new { on = false, name = workflow.Name },
            new SentinelAgentChange($"Switched off the automation '{workflow.Name}'", "automation", workflow.Id.ToString(), workflow.Name, LinkTo(workflow.Id),
                SentinelAgentJson.Serialize(new WorkflowUndo(workflow.Id))));
    }

    private async Task UndoTurnOffAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var workflow = await WorkflowAsync(SentinelAgentJson.Deserialize<WorkflowUndo>(undoJson).WorkflowId, ct);
        if (workflow.Status == AutomationWorkflowStatuses.Active) throw new SentinelAgentToolException($"'{workflow.Name}' is already on again.");
        await workflows.SetActiveAsync(workflow.Id, true, ct);
    }

    private async Task<AutomationWorkflowView> WorkflowAsync(Guid workflowId, CancellationToken ct) =>
        await workflows.GetAsync(workflowId, ct)
            ?? throw new SentinelAgentToolException($"There's no automation with id {workflowId}. List the automations first.");

    // Checked when proposing and again on Confirm: the editor's Activate button does the same.
    private async Task<AutomationWorkflowView> ReadyToTurnOnAsync(Guid workflowId, CancellationToken ct)
    {
        var workflow = await WorkflowAsync(workflowId, ct);
        if (workflow.Status == AutomationWorkflowStatuses.Active) throw new SentinelAgentToolException($"'{workflow.Name}' is already on.");
        var validation = await workflows.ValidateAsync(workflow.Id, ct);
        if (!validation.IsValid)
            throw new SentinelAgentToolException($"'{workflow.Name}' can't be switched on yet: {string.Join(" ", validation.Errors)} It needs fixing in the editor.");
        return workflow;
    }

    private static string Input(JsonObject args)
    {
        if (args.OptionalText("input") is not { } input) return args["input"] is JsonObject inputObject ? inputObject.ToJsonString() : "{}";
        try
        {
            return JsonNode.Parse(input) is JsonObject ? input : throw new SentinelAgentToolException("'input' should be a JSON object, like {\"name\": \"value\"}.");
        }
        catch (JsonException)
        {
            throw new SentinelAgentToolException("'input' should be a JSON object, like {\"name\": \"value\"}.");
        }
    }

    // The proposed workflow, checked against the node registry the way the old proposal was, so a
    // mistake goes back to the model before the person sees a card.
    private ProposedGraph Graph(JsonObject args)
    {
        var name = args.Text("name");
        var nodes = args.Objects("nodes");
        if (nodes.Count == 0) throw new SentinelAgentToolException("'nodes' needs the workflow's steps, starting with a trigger.");
        if (nodes.Count > MaxNodes) throw new SentinelAgentToolException($"That's {nodes.Count} steps; keep it to {MaxNodes} or fewer.");

        var byName = new Dictionary<string, (AutomationNodeView View, AutomationNodeDefinition Definition)>(StringComparer.OrdinalIgnoreCase);
        var views = new List<AutomationNodeView>();
        foreach (var node in nodes)
        {
            var nodeName = node.OptionalText("name") ?? throw new SentinelAgentToolException("Every step needs a name.");
            var typeKey = node.OptionalText("typeKey") ?? throw new SentinelAgentToolException($"The step '{nodeName}' needs a typeKey.");
            var definition = registry.Find(typeKey)
                ?? throw new SentinelAgentToolException($"'{typeKey}' isn't an automation step. Look it up with automation_node_types.");
            if (byName.ContainsKey(nodeName)) throw new SentinelAgentToolException($"Two steps are called '{nodeName}'; give each its own name.");
            var view = new AutomationNodeView(
                Guid.NewGuid(), nodeName, definition.TypeKey, definition.Version,
                PositionX: 120 + views.Count % 4 * 280, PositionY: 140 + views.Count / 4 * 200,
                ParametersJson: Parameters(node, nodeName, definition),
                CredentialId: null, IsDisabled: false, ContinueOnFail: false, RetryOnFail: false,
                MaxTries: 1, WaitBetweenTriesMs: 0, TimeoutMs: 0, Notes: string.Empty);
            byName[nodeName] = (view, definition);
            views.Add(view);
        }

        if (!byName.Values.Any(node => node.Definition.IsTrigger))
            throw new SentinelAgentToolException("The workflow needs a trigger step first (search automation_node_types for 'trigger').");

        var links = args.Objects("connections");
        var connections = new List<AutomationConnectionView>();
        if (links.Count == 0)
        {
            for (var i = 1; i < views.Count; i++)
                connections.Add(new AutomationConnectionView(Guid.NewGuid(), views[i - 1].Id, "main", views[i].Id, "main"));
        }

        foreach (var link in links)
        {
            var from = link.OptionalText("from") ?? string.Empty;
            var to = link.OptionalText("to") ?? string.Empty;
            if (!byName.TryGetValue(from, out var source) || !byName.TryGetValue(to, out var target))
                throw new SentinelAgentToolException($"A connection names a step that isn't in 'nodes' ('{from}' -> '{to}'). Use the steps' names.");
            var output = link.OptionalText("fromOutput") ?? source.Definition.Outputs.FirstOrDefault() ?? "main";
            if (!source.Definition.Outputs.Contains(output, StringComparer.OrdinalIgnoreCase))
                throw new SentinelAgentToolException($"'{from}' has the outputs {string.Join(", ", source.Definition.Outputs)}, not '{output}'.");
            connections.Add(new AutomationConnectionView(Guid.NewGuid(), source.View.Id, output.ToLowerInvariant(), target.View.Id, "main"));
        }

        return new ProposedGraph(name, args.OptionalText("description") ?? string.Empty, views, connections);
    }

    // Settings sent as an object or as a JSON string; left out, the step's defaults.
    private static string Parameters(JsonObject node, string nodeName, AutomationNodeDefinition definition)
    {
        if (node["parameters"] is JsonObject parameters) return parameters.ToJsonString();
        var text = node.OptionalText("parameters") ?? node.OptionalText("parametersJson");
        if (text is null) return definition.DefaultParametersJson;
        try
        {
            return JsonNode.Parse(text) is JsonObject ? text : throw new SentinelAgentToolException($"The settings for '{nodeName}' should be a JSON object.");
        }
        catch (JsonException)
        {
            throw new SentinelAgentToolException($"The settings for '{nodeName}' aren't valid JSON.");
        }
    }

    private static string Preview(ProposedGraph graph)
    {
        var steps = string.Join(" -> ", graph.Nodes.Select(node => node.Name));
        return $"Make the automation '{graph.Name}', switched off: {steps}";
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private sealed record ProposedGraph(string Name, string Description, IReadOnlyList<AutomationNodeView> Nodes, IReadOnlyList<AutomationConnectionView> Connections);

    private sealed record WorkflowUndo(Guid WorkflowId);
}
