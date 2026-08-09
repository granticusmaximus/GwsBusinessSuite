using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

public sealed partial class AutomationNodeRegistry(
    IAutomationHttpClient httpClient,
    IOllamaService? ollama = null,
    IAppDbContextFactory? dbContextFactory = null,
    // Resolved lazily inside ExecuteSetRowPropertyAsync, not injected directly: Wiki.IWikiDatabaseService
    // depends on IAutomationTriggerService, which depends on IAutomationExecutionService, which
    // depends on this registry - an eager constructor dependency here would be circular. A scope's
    // IServiceProvider is safe because by the time a node actually executes, this registry (and
    // everything above it in that chain) is already fully constructed.
    IServiceProvider? serviceProvider = null) : IAutomationNodeRegistry
{
    private static readonly IReadOnlyList<AutomationNodeDefinition> Definitions =
    [
        new("core.manualTrigger", 1, "Manual Trigger", "Starts when you select Run workflow.", "Triggers", "bi-play-circle-fill", true, ["main"], "{}"),
        new("core.webhookTrigger", 1, "Webhook Trigger", "Starts an active workflow from its public webhook path.", "Triggers", "bi-broadcast-pin", true, ["main"], "{\"path\":\"incoming-event\"}"),
        new("core.scheduleTrigger", 1, "Schedule Trigger", "Starts an active workflow at a recurring minute interval.", "Triggers", "bi-clock-fill", true, ["main"], "{\"intervalMinutes\":60}"),
        new("database.rowChangedTrigger", 1, "Database Row Changed", "Starts an active workflow when a row's properties change in a Sentinel database. Paste the database's id (visible in its Sentinel URL) into wikiDatabaseId.", "Triggers", "bi-table", true, ["main"], "{\"wikiDatabaseId\":\"\"}"),
        new("database.setRowProperty", 1, "Set Database Row Property", "Sets one property on a Sentinel database row. Paste the database, row, and property ids and an optional {{ $json.path }} expression for the value. Never re-triggers a Database Row Changed workflow, so it cannot cause an automation loop - chaining a second workflow off this write is not supported.", "Actions", "bi-pencil-square", false, ["main"], "{\"wikiDatabaseId\":\"\",\"rowId\":\"{{ $json.rowId }}\",\"propertyId\":\"\",\"value\":\"\"}", IsIdempotent: false),
        new("database.addRow", 1, "Add Database Row", "Creates a new row in a Sentinel database. propertyValues maps property ids to values (string values support {{ $json.path }} expressions); parentRowId is optional and nests the new row as a sub-item. Like Set Database Row Property, this never re-triggers a Database Row Changed workflow.", "Actions", "bi-plus-square", false, ["main"], "{\"wikiDatabaseId\":\"\",\"parentRowId\":\"\",\"propertyValues\":{}}", IsIdempotent: false),
        new("core.set", 1, "Set Fields", "Adds or replaces JSON fields using literal values or expressions.", "Data", "bi-braces", false, ["main"], "{\"values\":{\"message\":\"Hello from GWS\"}}"),
        new("core.if", 1, "If", "Routes an item to the true or false output.", "Flow", "bi-signpost-split-fill", false, ["true", "false"], "{\"left\":\"{{ $json.enabled }}\",\"operator\":\"equals\",\"right\":\"true\"}"),
        new("core.httpRequest", 1, "HTTP Request", "Calls an HTTP API and returns status, headers, and response data.", "Actions", "bi-globe2", false, ["main"], "{\"method\":\"GET\",\"url\":\"https://example.com\",\"headers\":{},\"body\":\"\"}", IsIdempotent: false),
        new("core.splitOut", 1, "Split Out", "Emits one item for each value in an array field.", "Data", "bi-distribute-vertical", false, ["main"], "{\"field\":\"items\",\"includeSource\":false}"),
        new("core.batch", 1, "Batch Items", "Groups an input array into smaller batches.", "Flow", "bi-collection", false, ["main"], "{\"field\":\"items\",\"batchSize\":10}"),
        new("core.merge", 1, "Merge", "Waits for labeled inputs and combines them into one item.", "Flow", "bi-bezier2", false, ["main"], "{}"),
        new("core.limit", 1, "Limit", "Keeps the first or last items from an array.", "Data", "bi-funnel", false, ["main"], "{\"field\":\"items\",\"maxItems\":10,\"keep\":\"first\"}"),
        new("core.sort", 1, "Sort", "Sorts an array by a JSON field.", "Data", "bi-sort-down", false, ["main"], "{\"field\":\"items\",\"sortBy\":\"name\",\"direction\":\"ascending\"}"),
        new("core.removeDuplicates", 1, "Remove Duplicates", "Removes repeated array items using a selected field.", "Data", "bi-intersect", false, ["main"], "{\"field\":\"items\",\"compareBy\":\"id\"}"),
        new("core.template", 1, "Template", "Builds formatted text from the current JSON item.", "Data", "bi-file-text", false, ["main"], "{\"outputField\":\"text\",\"template\":\"Hello {{ $json.name }}\"}"),
        new("core.dateTime", 1, "Date & Time", "Adds the current UTC time in ISO and Unix formats.", "Data", "bi-calendar3", false, ["main"], "{\"outputField\":\"timestamp\"}"),
        new("core.noOp", 1, "No Operation", "Passes input through unchanged for layout and debugging.", "Flow", "bi-arrow-right-circle", false, ["main"], "{}"),
        new("core.stopError", 1, "Stop and Error", "Stops the workflow with a configured error message.", "Flow", "bi-exclamation-octagon", false, ["main"], "{\"message\":\"Workflow stopped\"}"),
        new("core.wait", 1, "Wait", "Pauses the workflow until a duration, timestamp, or resume webhook.", "Flow", "bi-hourglass-split", false, ["main"], "{\"mode\":\"duration\",\"durationMs\":60000,\"timestamp\":null}"),
        new("core.approval", 1, "Approval", "Pauses the workflow for a human decision.", "Flow", "bi-person-check", false, ["approved", "rejected"], "{\"message\":\"Approve this step?\",\"timeoutHours\":0}"),
        new(
            "ai.modelAdvisor",
            1,
            "Model Adviser",
            "Asks an installed Ollama model for bounded specialist advice and appends it to the workflow item.",
            "AI",
            "bi-cpu-fill",
            false,
            ["main"],
            "{\"model\":\"qwen2.5-coder\",\"role\":\"Review the request as a senior .NET and C# engineer. Identify correctness, security, testing, and architecture concerns.\",\"promptPath\":\"prompt\",\"outputField\":\"qwenAdvice\"}"),
        new(
            "ai.sentinelSynthesize",
            1,
            "SentinelGPT Synthesize",
            "Uses SentinelGPT to reconcile specialist advice into one evidence-aware proposed lesson.",
            "AI",
            "bi-stars",
            false,
            ["main"],
            "{\"model\":\"sentinelgpt\",\"promptPath\":\"prompt\",\"answerField\":\"sentinelAnswer\"}"),
        new(
            "ai.saveApprovedLesson",
            1,
            "Save Approved Lesson",
            "Stores a human-approved SentinelGPT answer as reusable learning memory. Rejected or unapproved items are not stored.",
            "AI",
            "bi-journal-check",
            false,
            ["main"],
            "{\"promptPath\":\"prompt\",\"answerPath\":\"sentinelAnswer\"}"),
    ];

    public IReadOnlyList<AutomationNodeDefinition> ListDefinitions() => Definitions;

    public AutomationNodeDefinition? Find(string typeKey, int version = 1) => Definitions.FirstOrDefault(
        definition => definition.Version == version && definition.TypeKey.Equals(typeKey, StringComparison.OrdinalIgnoreCase));

    public async Task<AutomationNodeRunResult> ExecuteAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        string? workflowOwnerUsername = null,
        CancellationToken cancellationToken = default)
    {
        return node.TypeKey switch
        {
            "core.manualTrigger" => SingleOutput("main", input),
            "core.webhookTrigger" => SingleOutput("main", input),
            "core.scheduleTrigger" => SingleOutput("main", input),
            "database.rowChangedTrigger" => SingleOutput("main", input),
            "database.setRowProperty" => await ExecuteSetRowPropertyAsync(node, input, workflowOwnerUsername, cancellationToken),
            "database.addRow" => await ExecuteAddRowAsync(node, input, workflowOwnerUsername, cancellationToken),
            "core.set" => ExecuteSet(node, input),
            "core.if" => ExecuteIf(node, input),
            "core.httpRequest" => await ExecuteHttpAsync(node, input, credentialJson, cancellationToken),
            "core.splitOut" => ExecuteSplitOut(node, input),
            "core.batch" => ExecuteBatch(node, input),
            "core.merge" => SingleOutput("main", input),
            "core.limit" => ExecuteLimit(node, input),
            "core.sort" => ExecuteSort(node, input),
            "core.removeDuplicates" => ExecuteRemoveDuplicates(node, input),
            "core.template" => ExecuteTemplate(node, input),
            "core.dateTime" => ExecuteDateTime(node, input),
            "core.noOp" => SingleOutput("main", input),
            "core.stopError" => throw new InvalidOperationException(ResolveText(ParseObject(node.ParametersJson, node.Name)["message"]?.GetValue<string>() ?? "Workflow stopped.", input)),
            "core.wait" or "core.approval" => throw new InvalidOperationException($"{node.Name} must be paused and resumed by the execution engine, not called directly."),
            "ai.modelAdvisor" => await ExecuteModelAdvisorAsync(node, input, cancellationToken),
            "ai.sentinelSynthesize" => await ExecuteSentinelSynthesisAsync(node, input, cancellationToken),
            "ai.saveApprovedLesson" => await ExecuteSaveApprovedLessonAsync(node, input, cancellationToken),
            _ => throw new InvalidOperationException($"Node type '{node.TypeKey}' is not executable.")
        };
    }

    private async Task<AutomationNodeRunResult> ExecuteModelAdvisorAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var ai = ollama ?? throw new InvalidOperationException("Ollama is not available to the automation engine.");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var source = RequireObject(input, node.Name);
        var model = RequireSafeModelName(parameters["model"]?.GetValue<string>(), node.Name);
        var role = parameters["role"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(role))
            throw new InvalidOperationException($"{node.Name} requires a specialist role.");
        var promptPath = parameters["promptPath"]?.GetValue<string>()?.Trim() ?? "prompt";
        var outputField = RequireSafeFieldName(parameters["outputField"]?.GetValue<string>() ?? "advice", node.Name);
        var prompt = FindString(source, promptPath)
            ?? throw new InvalidOperationException($"{node.Name} could not find a non-empty prompt at '{promptPath}'.");

        var systemPrompt =
            $"{role} Return independent advisory analysis, not a final user-facing answer. " +
            "Challenge unsupported assumptions. Separate verified facts from inference. " +
            "Never claim that an action ran. Keep the response under 700 words and do not request or reveal secrets.";
        var advice = (await ai.GenerateAsync(
            model,
            systemPrompt,
            $"REQUEST:\n{LimitText(prompt, 12_000)}\n\nWORKFLOW CONTEXT:\n{LimitText(source.ToJsonString(), 12_000)}",
            cancellationToken)).Trim();
        if (advice.Length == 0) throw new InvalidOperationException($"{model} returned empty specialist advice.");

        var output = source.DeepClone().AsObject();
        output[outputField] = LimitText(advice, 8_000);
        output[$"{outputField}Model"] = model;
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private async Task<AutomationNodeRunResult> ExecuteSentinelSynthesisAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var ai = ollama ?? throw new InvalidOperationException("Ollama is not available to the automation engine.");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var source = RequireObject(input, node.Name);
        var model = RequireSafeModelName(parameters["model"]?.GetValue<string>() ?? "sentinelgpt", node.Name);
        var promptPath = parameters["promptPath"]?.GetValue<string>()?.Trim() ?? "prompt";
        var answerField = RequireSafeFieldName(parameters["answerField"]?.GetValue<string>() ?? "sentinelAnswer", node.Name);
        var prompt = FindStringRecursive(source, promptPath)
            ?? throw new InvalidOperationException($"{node.Name} could not find a non-empty prompt at '{promptPath}'.");
        var advice = FindPropertiesEndingWith(source, "Advice")
            .Select(item => $"{item.Key}:\n{item.Value}")
            .ToList();
        if (advice.Count == 0)
            throw new InvalidOperationException($"{node.Name} requires at least one specialist advice field.");

        var systemPrompt =
            "You are SentinelGPT acting as the final judge. Reconcile the specialist opinions, but do not treat them as factual sources. " +
            "Prefer supplied verified evidence, identify disagreements, correct faulty premises, and produce the strongest practical answer. " +
            "Do not claim that any application action succeeded. Keep the answer under 1,200 words.";
        var combinedAdvice = string.Join("\n\n", advice);
        var answer = (await ai.GenerateAsync(
            model,
            systemPrompt,
            $"ORIGINAL REQUEST:\n{LimitText(prompt, 12_000)}\n\nSPECIALIST ADVICE:\n{LimitText(combinedAdvice, 16_000)}",
            cancellationToken)).Trim();
        if (answer.Length == 0) throw new InvalidOperationException("SentinelGPT returned an empty synthesis.");

        var output = source.DeepClone().AsObject();
        output["prompt"] = prompt;
        output[answerField] = LimitText(answer, 12_000);
        output[$"{answerField}Model"] = model;
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private async Task<AutomationNodeRunResult> ExecuteSaveApprovedLessonAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var factory = dbContextFactory
            ?? throw new InvalidOperationException("Learning-memory storage is not available to the automation engine.");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var source = RequireObject(input, node.Name);
        var approved = ResolveNode(source, "_approval.approved")?.GetValue<bool>() == true;
        var output = source.DeepClone().AsObject();
        if (!approved)
        {
            output["learningMemory"] = new JsonObject { ["saved"] = false, ["reason"] = "Human approval is required." };
            return SingleOutput("main", JsonSerializer.SerializeToElement(output));
        }

        var promptPath = parameters["promptPath"]?.GetValue<string>()?.Trim() ?? "prompt";
        var answerPath = parameters["answerPath"]?.GetValue<string>()?.Trim() ?? "sentinelAnswer";
        var prompt = FindStringRecursive(source, promptPath)
            ?? throw new InvalidOperationException($"{node.Name} could not find the lesson prompt.");
        var answer = FindStringRecursive(source, answerPath)
            ?? throw new InvalidOperationException($"{node.Name} could not find the approved lesson answer.");
        var now = DateTimeOffset.UtcNow;
        var lesson = new SentinelAiRun
        {
            ConversationId = Guid.NewGuid(),
            Action = "teacherWorkflow",
            Instruction = LimitText(prompt, 12_000),
            Output = LimitText(answer, 24_000),
            Status = SentinelAiRunStatuses.Approved,
            Model = "sentinelgpt",
            ReviewedAt = now,
            ReviewedBy = "workflow-approval",
            CreatedAt = now,
            CreatedBy = "sentinel-learning-workflow",
            CitationsJson = "[]"
        };
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.SentinelAiRuns.Add(lesson);
        await db.SaveChangesAsync(cancellationToken);
        output["learningMemory"] = new JsonObject
        {
            ["saved"] = true,
            ["lessonId"] = lesson.Id.ToString(),
            ["savedAt"] = now.ToString("O")
        };
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private async Task<AutomationNodeRunResult> ExecuteSetRowPropertyAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? workflowOwnerUsername,
        CancellationToken cancellationToken)
    {
        var wikiDatabaseService = serviceProvider?.GetService(typeof(IWikiDatabaseService)) as IWikiDatabaseService
            ?? throw new InvalidOperationException("Database writes are not available to the automation engine.");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var source = RequireObject(input, node.Name);

        var wikiDatabaseId = ParseRequiredGuid(parameters["wikiDatabaseId"]?.GetValue<string>(), node.Name, "wikiDatabaseId");
        var rowIdText = ResolveText(parameters["rowId"]?.GetValue<string>() ?? string.Empty, input);
        var rowId = ParseRequiredGuid(rowIdText, node.Name, "rowId");
        var propertyId = ParseRequiredGuid(parameters["propertyId"]?.GetValue<string>(), node.Name, "propertyId");
        var value = ResolveText(parameters["value"]?.GetValue<string>() ?? string.Empty, input);

        // This node can write to ANY Sentinel database/row by id, with no ownership or scoping
        // check - previously contained only by both Automation and Wiki being AdminOnly routes
        // (an Admin using this node could already do the same edit directly). Wiki now admits
        // Author/Contributor accounts too (SentinelAccessService.CanAccessAsync gates what they
        // can open/edit there), so this node needed the same real check rather than continuing
        // to bypass it - an Admin-authored workflow still has unrestricted access (Admins
        // aren't subject to SentinelResourcePermission grants anywhere else either), but a
        // Contributor's workflow is now held to the same per-resource Edit access they'd need
        // through the Wiki UI itself.
        await EnsureCanEditDatabaseAsync(wikiDatabaseId, workflowOwnerUsername, node.Name, cancellationToken);

        // "automation-engine" (see WikiDatabaseService.SaveRowAsync) is the actor that skips
        // re-firing database.rowChangedTrigger - required so this node can never chain into an
        // automation loop, including its own workflow's trigger on the same database.
        await wikiDatabaseService.SaveInlineCellAsync(wikiDatabaseId, rowId, propertyId, value, "automation-engine", cancellationToken);

        var output = source.DeepClone().AsObject();
        output["databaseWrite"] = new JsonObject
        {
            ["saved"] = true,
            ["wikiDatabaseId"] = wikiDatabaseId.ToString(),
            ["rowId"] = rowId.ToString(),
            ["propertyId"] = propertyId.ToString()
        };
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private async Task<AutomationNodeRunResult> ExecuteAddRowAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? workflowOwnerUsername,
        CancellationToken cancellationToken)
    {
        var wikiDatabaseService = serviceProvider?.GetService(typeof(IWikiDatabaseService)) as IWikiDatabaseService
            ?? throw new InvalidOperationException("Database writes are not available to the automation engine.");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var source = RequireObject(input, node.Name);

        var wikiDatabaseId = ParseRequiredGuid(parameters["wikiDatabaseId"]?.GetValue<string>(), node.Name, "wikiDatabaseId");
        // Same access model as Set Database Row Property - see EnsureCanEditDatabaseAsync's own
        // comment for why an Admin-authored workflow bypasses this while others don't.
        await EnsureCanEditDatabaseAsync(wikiDatabaseId, workflowOwnerUsername, node.Name, cancellationToken);

        var parentRowIdText = ResolveText(parameters["parentRowId"]?.GetValue<string>() ?? string.Empty, input);
        var parentRowId = Guid.TryParse(parentRowIdText, out var parsedParentRowId) ? parsedParentRowId : (Guid?)null;

        // Same per-key resolution as core.set's "values": string leaves support {{ $json.path }}
        // expressions, everything else (numbers/booleans/arrays already shaped for a specific
        // property type) is copied through unchanged.
        var values = new JsonObject();
        if (parameters["propertyValues"] is JsonObject propertyValues)
        {
            foreach (var pair in propertyValues)
            {
                values[pair.Key] = pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    ? JsonValue.Create(ResolveText(text, input))
                    : pair.Value?.DeepClone();
            }
        }

        // "automation-engine" (see WikiDatabaseService.SaveRowAsync) is the actor that skips
        // re-firing database.rowChangedTrigger - same loop guard as Set Database Row Property.
        var row = await wikiDatabaseService.SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor
        {
            ParentRowId = parentRowId,
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value)
        }, "automation-engine", cancellationToken);

        var output = source.DeepClone().AsObject();
        output["databaseRow"] = new JsonObject
        {
            ["created"] = true,
            ["wikiDatabaseId"] = wikiDatabaseId.ToString(),
            ["rowId"] = row.Id.ToString()
        };
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private static Guid ParseRequiredGuid(string? value, string nodeName, string parameterName) =>
        Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{nodeName} requires a valid {parameterName}.");

    // Admins bypass SentinelResourcePermission everywhere else in the app (Wiki.razor's
    // CanAccessPageOrDatabaseAsync does the same _isAdmin short-circuit), so this mirrors that
    // rather than introducing a second, different access model for automation specifically.
    private async Task EnsureCanEditDatabaseAsync(
        Guid wikiDatabaseId, string? workflowOwnerUsername, string nodeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowOwnerUsername))
        {
            throw new InvalidOperationException($"{nodeName} could not determine the workflow's owner to check database access.");
        }

        var factory = dbContextFactory
            ?? throw new InvalidOperationException($"{nodeName} cannot verify database access without database access itself.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var isAdmin = await db.AppUsers.AsNoTracking()
            .AnyAsync(user => user.Username == workflowOwnerUsername && user.Role == AppRoles.Admin, cancellationToken);
        if (isAdmin)
        {
            return;
        }

        var accessService = serviceProvider?.GetService(typeof(ISentinelAccessService)) as ISentinelAccessService
            ?? throw new InvalidOperationException($"{nodeName} cannot verify database access right now.");
        var canEdit = await accessService.CanAccessAsync(
            wikiDatabaseId, isDatabase: true, workflowOwnerUsername, SentinelAccessLevels.Edit, cancellationToken);
        if (!canEdit)
        {
            throw new InvalidOperationException(
                $"{nodeName} cannot write to this database - '{workflowOwnerUsername}' (this workflow's owner) does not have Edit access to it.");
        }
    }

    // Unlike core.batch/core.limit (which already clamp their size parameters), splitOut had
    // no ceiling at all: each output item becomes its own queued execution step, and every
    // step re-serializes the *entire remaining frontier* to the DB (AutomationExecutionService
    // .RunLoopAsync's checkpoint) - an ordinary large array turned into O(n^2) CPU/DB-write
    // work with no malice required. A silent truncation would quietly drop items from a
    // "process every row" workflow, which is worse than failing loudly, so this throws with an
    // actionable fix (chunk through core.batch first) rather than truncating.
    private const int MaxSplitOutItems = 2_000;

    private static AutomationNodeRunResult ExecuteSplitOut(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["field"]?.GetValue<string>()?.Trim() ?? "items";
        var source = RequireObject(input, node.Name);
        var array = ResolveNode(source, field) as JsonArray
            ?? throw new InvalidOperationException($"{node.Name} expected '{field}' to be an array.");
        if (array.Count > MaxSplitOutItems)
        {
            throw new InvalidOperationException(
                $"{node.Name} would fan out into {array.Count} items, which exceeds the {MaxSplitOutItems:N0} item safety limit. " +
                "Insert a core.batch node before this one to process the array in smaller chunks instead.");
        }
        var includeSource = root["includeSource"]?.GetValue<bool>() ?? false;
        var items = new List<JsonElement>();
        foreach (var value in array)
        {
            JsonNode? output = value?.DeepClone();
            if (includeSource)
            {
                var wrapper = source.DeepClone().AsObject();
                wrapper[field.Split('.').Last()] = output;
                output = wrapper;
            }
            items.Add(JsonSerializer.SerializeToElement(output));
        }
        return MultipleOutput("main", items);
    }

    private static AutomationNodeRunResult ExecuteBatch(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["field"]?.GetValue<string>()?.Trim() ?? "items";
        var size = Math.Clamp(root["batchSize"]?.GetValue<int>() ?? 10, 1, 1000);
        var source = RequireObject(input, node.Name);
        var array = ResolveNode(source, field) as JsonArray
            ?? throw new InvalidOperationException($"{node.Name} expected '{field}' to be an array.");
        var batches = array.Select(item => item?.DeepClone()).Chunk(size)
            .Select(chunk => JsonSerializer.SerializeToElement(new JsonObject
            {
                ["items"] = new JsonArray(chunk.ToArray()),
                ["count"] = chunk.Length
            })).ToList();
        return MultipleOutput("main", batches);
    }

    private static AutomationNodeRunResult ExecuteLimit(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["field"]?.GetValue<string>()?.Trim() ?? "items";
        var max = Math.Clamp(root["maxItems"]?.GetValue<int>() ?? 10, 0, 10_000);
        var source = RequireObject(input, node.Name);
        var array = ResolveNode(source, field) as JsonArray
            ?? throw new InvalidOperationException($"{node.Name} expected '{field}' to be an array.");
        var values = string.Equals(root["keep"]?.GetValue<string>(), "last", StringComparison.OrdinalIgnoreCase)
            ? array.Skip(Math.Max(0, array.Count - max))
            : array.Take(max);
        return SingleOutput("main", ReplaceArray(source, field, values));
    }

    private static AutomationNodeRunResult ExecuteSort(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["field"]?.GetValue<string>()?.Trim() ?? "items";
        var sortBy = root["sortBy"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var source = RequireObject(input, node.Name);
        var array = ResolveNode(source, field) as JsonArray
            ?? throw new InvalidOperationException($"{node.Name} expected '{field}' to be an array.");
        var values = array.Select(item => item?.DeepClone()).ToList();
        values.Sort((left, right) => CompareNodes(ResolveNode(left, sortBy), ResolveNode(right, sortBy)));
        if (string.Equals(root["direction"]?.GetValue<string>(), "descending", StringComparison.OrdinalIgnoreCase)) values.Reverse();
        return SingleOutput("main", ReplaceArray(source, field, values));
    }

    private static AutomationNodeRunResult ExecuteRemoveDuplicates(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["field"]?.GetValue<string>()?.Trim() ?? "items";
        var compareBy = root["compareBy"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var source = RequireObject(input, node.Name);
        var array = ResolveNode(source, field) as JsonArray
            ?? throw new InvalidOperationException($"{node.Name} expected '{field}' to be an array.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = array.Where(item => seen.Add((ResolveNode(item, compareBy) ?? item)?.ToJsonString() ?? "null"))
            .Select(item => item?.DeepClone());
        return SingleOutput("main", ReplaceArray(source, field, values));
    }

    private static AutomationNodeRunResult ExecuteTemplate(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var output = input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText())!.AsObject() : new JsonObject { ["value"] = JsonNode.Parse(input.GetRawText()) };
        output[root["outputField"]?.GetValue<string>()?.Trim() ?? "text"] = ResolveText(root["template"]?.GetValue<string>() ?? string.Empty, input);
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private static AutomationNodeRunResult ExecuteDateTime(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["outputField"]?.GetValue<string>()?.Trim() ?? "timestamp";
        var output = input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText())!.AsObject() : new JsonObject { ["value"] = JsonNode.Parse(input.GetRawText()) };
        var now = DateTimeOffset.UtcNow;
        output[field] = new JsonObject { ["iso"] = now.ToString("O"), ["unixSeconds"] = now.ToUnixTimeSeconds() };
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private static AutomationNodeRunResult ExecuteSet(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var output = input.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(input.GetRawText())!.AsObject()
            : new JsonObject { ["value"] = JsonNode.Parse(input.GetRawText()) };
        if (root["values"] is JsonObject values)
        {
            foreach (var pair in values)
            {
                output[pair.Key] = pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    ? JsonValue.Create(ResolveText(text, input))
                    : pair.Value?.DeepClone();
            }
        }
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private static AutomationNodeRunResult ExecuteIf(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var left = ResolveText(root["left"]?.GetValue<string>() ?? string.Empty, input);
        var right = ResolveText(root["right"]?.GetValue<string>() ?? string.Empty, input);
        var op = root["operator"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "equals";
        var isTrue = op switch
        {
            "equals" => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "notequals" => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "contains" => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            "exists" => !string.IsNullOrWhiteSpace(left),
            "greaterthan" => decimal.TryParse(left, out var l) && decimal.TryParse(right, out var r) && l > r,
            "lessthan" => decimal.TryParse(left, out var l) && decimal.TryParse(right, out var r) && l < r,
            _ => throw new InvalidOperationException($"If node operator '{op}' is not supported.")
        };
        return SingleOutput(isTrue ? "true" : "false", input);
    }

    private async Task<AutomationNodeRunResult> ExecuteHttpAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        CancellationToken cancellationToken)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var methodText = root["method"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "GET";
        var url = ResolveText(root["url"]?.GetValue<string>() ?? string.Empty, input);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("HTTP Request URL must be an absolute HTTP or HTTPS URL.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeaders(headers, root["headers"] as JsonObject, input);
        // Node evidence (AutomationNodeExecution.OutputJson) stores this method's return value
        // verbatim, at-rest-unencrypted, visible in the Automation UI's execution history. A
        // credential's decrypted header VALUES are real secrets - an endpoint that reflects
        // request headers back (an echo/debug endpoint, or just a misconfigured API) would
        // otherwise leak them straight into that plaintext history, undoing the point of
        // encrypting the credential in the first place. Collected here so the response can be
        // scanned for them below, regardless of whether they show up in its body or headers.
        var credentialSecretValues = new List<string>();
        if (!string.IsNullOrWhiteSpace(credentialJson))
        {
            var credential = ParseObject(credentialJson, "Credential");
            AddHeaders(headers, credential["headers"] as JsonObject, input);
            if (credential["headers"] is JsonObject credentialHeaders)
            {
                foreach (var pair in credentialHeaders)
                {
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                        credentialSecretValues.Add(ResolveText(text, input));
                }
            }
        }
        var body = root["body"] is JsonValue bodyValue && bodyValue.TryGetValue<string>(out var bodyText)
            ? ResolveText(bodyText, input)
            : root["body"]?.ToJsonString();
        var response = await httpClient.SendAsync(new AutomationHttpRequest(
            new HttpMethod(methodText), uri.ToString(), body, headers), cancellationToken);

        JsonNode? parsedBody;
        try { parsedBody = JsonNode.Parse(response.Body); }
        catch (JsonException) { parsedBody = JsonValue.Create(response.Body); }
        var output = new JsonObject
        {
            ["statusCode"] = response.StatusCode,
            ["body"] = parsedBody,
            ["headers"] = JsonSerializer.SerializeToNode(response.Headers)
        };
        var cloned = JsonSerializer.SerializeToElement(output).Clone();

        // Outputs (real data flow to downstream nodes) stays unredacted - a legitimate
        // workflow could genuinely need an unredacted value from the response (e.g. a
        // refreshed token a downstream node re-uses). DisplayOutputJson (what actually gets
        // persisted to AutomationNodeExecution.OutputJson, at rest, visible in the execution
        // history UI) is the only thing redacted - see the credentialSecretValues comment
        // above for why this is necessary at all.
        var displayOutputJson = credentialSecretValues.Count == 0
            ? cloned.GetRawText()
            : RedactSecrets(cloned.GetRawText(), credentialSecretValues);
        return new AutomationNodeRunResult(
            new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase) { ["main"] = [cloned] },
            displayOutputJson);
    }

    private static string RedactSecrets(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    private static void AddHeaders(Dictionary<string, string> destination, JsonObject? source, JsonElement input)
    {
        if (source is null) return;
        foreach (var pair in source)
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                destination[pair.Key] = ResolveText(text, input);
    }

    private static AutomationNodeRunResult SingleOutput(string port, JsonElement value)
    {
        var cloned = value.Clone();
        return new AutomationNodeRunResult(
            new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase) { [port] = [cloned] },
            cloned.GetRawText());
    }

    private static AutomationNodeRunResult MultipleOutput(string port, IReadOnlyList<JsonElement> values)
    {
        var cloned = values.Select(value => value.Clone()).ToList();
        return new AutomationNodeRunResult(
            new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase) { [port] = cloned },
            JsonSerializer.Serialize(cloned));
    }

    private static JsonObject RequireObject(JsonElement input, string nodeName) => input.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(input.GetRawText())!.AsObject()
        : throw new InvalidOperationException($"{nodeName} requires an object input.");

    private static JsonNode? ResolveNode(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path)) return root;
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            current = current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next) ? next : null;
        return current;
    }

    private static string? FindString(JsonNode root, string path)
    {
        var value = ResolveNode(root, path);
        var text = value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue)
            ? stringValue
            : value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? FindStringRecursive(JsonNode? root, string propertyName)
    {
        if (root is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(propertyName, out var direct))
            {
                var text = direct is JsonValue value && value.TryGetValue<string>(out var stringValue)
                    ? stringValue
                    : direct?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            foreach (var child in obj)
            {
                var found = FindStringRecursive(child.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        else if (root is JsonArray array)
        {
            foreach (var child in array)
            {
                var found = FindStringRecursive(child, propertyName);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        return null;
    }

    private static IEnumerable<KeyValuePair<string, string>> FindPropertiesEndingWith(JsonNode? root, string suffix)
    {
        if (root is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    yield return new KeyValuePair<string, string>(property.Key, text.Trim());
                }
                foreach (var nested in FindPropertiesEndingWith(property.Value, suffix)) yield return nested;
            }
        }
        else if (root is JsonArray array)
        {
            foreach (var child in array)
                foreach (var nested in FindPropertiesEndingWith(child, suffix))
                    yield return nested;
        }
    }

    private static string RequireSafeModelName(string? value, string nodeName)
    {
        var model = value?.Trim() ?? string.Empty;
        if (!Regex.IsMatch(model, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$"))
            throw new InvalidOperationException($"{nodeName} requires a valid installed Ollama model name.");
        return model;
    }

    private static string RequireSafeFieldName(string value, string nodeName)
    {
        var field = value.Trim();
        if (!Regex.IsMatch(field, "^[A-Za-z][A-Za-z0-9_]{0,63}$"))
            throw new InvalidOperationException($"{nodeName} output fields must start with a letter and contain only letters, numbers, or underscores.");
        return field;
    }

    private static string LimitText(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static JsonElement ReplaceArray(JsonObject source, string field, IEnumerable<JsonNode?> values)
    {
        var output = source.DeepClone().AsObject();
        var segments = field.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonObject parent = output;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (parent[segments[index]] is not JsonObject child) parent[segments[index]] = child = new JsonObject();
            parent = child;
        }
        parent[segments.Last()] = new JsonArray(values.Select(value => value?.DeepClone()).ToArray());
        return JsonSerializer.SerializeToElement(output);
    }

    private static int CompareNodes(JsonNode? left, JsonNode? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (decimal.TryParse(left.ToString(), out var leftNumber) && decimal.TryParse(right.ToString(), out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject ParseObject(string json, string label)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)?.AsObject() ?? new JsonObject(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{label} parameters are not a JSON object: {ex.Message}");
        }
    }

    private static string ResolveText(string template, JsonElement input)
    {
        return ExpressionPattern().Replace(template, match => ResolvePath(input, match.Groups[1].Value));
    }

    private static string ResolvePath(JsonElement input, string path)
    {
        var current = input;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return string.Empty;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : current.GetRawText();
    }

    [GeneratedRegex(@"\{\{\s*\$json(?:\.([A-Za-z0-9_.-]+))?\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex ExpressionPattern();
}
