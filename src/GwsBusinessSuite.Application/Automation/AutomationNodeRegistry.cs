using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Automation;

public sealed partial class AutomationNodeRegistry(IAutomationHttpClient httpClient) : IAutomationNodeRegistry
{
    private static readonly IReadOnlyList<AutomationNodeDefinition> Definitions =
    [
        new("core.manualTrigger", 1, "Manual Trigger", "Starts when you select Run workflow.", "Triggers", "bi-play-circle-fill", true, ["main"], "{}"),
        new("core.webhookTrigger", 1, "Webhook Trigger", "Starts an active workflow from its public webhook path.", "Triggers", "bi-broadcast-pin", true, ["main"], "{\"path\":\"incoming-event\"}"),
        new("core.scheduleTrigger", 1, "Schedule Trigger", "Starts an active workflow at a recurring minute interval.", "Triggers", "bi-clock-fill", true, ["main"], "{\"intervalMinutes\":60}"),
        new("core.set", 1, "Set Fields", "Adds or replaces JSON fields using literal values or expressions.", "Data", "bi-braces", false, ["main"], "{\"values\":{\"message\":\"Hello from GWS\"}}"),
        new("core.if", 1, "If", "Routes an item to the true or false output.", "Flow", "bi-signpost-split-fill", false, ["true", "false"], "{\"left\":\"{{ $json.enabled }}\",\"operator\":\"equals\",\"right\":\"true\"}"),
        new("core.httpRequest", 1, "HTTP Request", "Calls an HTTP API and returns status, headers, and response data.", "Actions", "bi-globe2", false, ["main"], "{\"method\":\"GET\",\"url\":\"https://example.com\",\"headers\":{},\"body\":\"\"}"),
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
    ];

    public IReadOnlyList<AutomationNodeDefinition> ListDefinitions() => Definitions;

    public AutomationNodeDefinition? Find(string typeKey, int version = 1) => Definitions.FirstOrDefault(
        definition => definition.Version == version && definition.TypeKey.Equals(typeKey, StringComparison.OrdinalIgnoreCase));

    public async Task<AutomationNodeRunResult> ExecuteAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        CancellationToken cancellationToken = default)
    {
        return node.TypeKey switch
        {
            "core.manualTrigger" => SingleOutput("main", input),
            "core.webhookTrigger" => SingleOutput("main", input),
            "core.scheduleTrigger" => SingleOutput("main", input),
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
            _ => throw new InvalidOperationException($"Node type '{node.TypeKey}' is not executable.")
        };
    }

    private static AutomationNodeRunResult ExecuteSplitOut(AutomationNodeSnapshot node, JsonElement input)
    {
        var root = ParseObject(node.ParametersJson, node.Name);
        var field = root["field"]?.GetValue<string>()?.Trim() ?? "items";
        var source = RequireObject(input, node.Name);
        var array = ResolveNode(source, field) as JsonArray
            ?? throw new InvalidOperationException($"{node.Name} expected '{field}' to be an array.");
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
        if (!string.IsNullOrWhiteSpace(credentialJson))
        {
            var credential = ParseObject(credentialJson, "Credential");
            AddHeaders(headers, credential["headers"] as JsonObject, input);
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
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
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
