using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.Application.SentinelAgent;

// SentinelGPT as an agent in the app (30-10-26, .claude/plans/sentinelgpt-agent-2026-10-30.md): it
// carries out what it's asked through the app's own services, as the person asking. How far a
// tool goes on its own was Grant's call: reads and routine changes run straight away (changes
// with Undo); publishing, deleting and anything leaving the app only propose, and wait for a
// Confirm.
public enum SentinelAgentRisk { Read, Change, Confirm }

public sealed record SentinelAgentUser(string Username, IReadOnlyCollection<string> Roles)
{
    public bool MayUse(ISentinelAgentTool tool) =>
        tool.Roles.Count == 0 || tool.Roles.Any(role => Roles.Contains(role, StringComparer.OrdinalIgnoreCase));

    public string RoleLabel => Roles.Count == 0 ? "no role" : string.Join(", ", Roles);
}

public sealed record SentinelAgentToolContext(SentinelAgentUser User, Guid ConversationId);

// What a change did: shown to the person, listed back to the model in later turns ("the to-do
// list you made" is then an id, not a guess), and what Undo reverses.
public sealed record SentinelAgentChange(string Summary, string ObjectKind, string ObjectId, string ObjectTitle, string? Link, string? UndoJson);

// ForModel is what the model reads back (JSON); a change also goes in the action log.
public sealed record SentinelAgentToolResult(string ForModel, SentinelAgentChange? Change = null)
{
    public static SentinelAgentToolResult Of(object forModel, SentinelAgentChange? change = null) =>
        new(JsonSerializer.Serialize(forModel, SentinelAgentJson.Options), change);
}

// A mistake the model can put right (a missing argument, an id that isn't there): it gets the
// message back as the tool's result instead of the turn failing.
public sealed class SentinelAgentToolException(string message) : Exception(message);

public interface ISentinelAgentTool
{
    string Name { get; }
    string Description { get; }
    string ParametersJson { get; }
    SentinelAgentRisk Risk { get; }
    // Empty means anyone signed in; otherwise the AppRoles that may use it (the same as the page's).
    IReadOnlyCollection<string> Roles { get; }
    bool CanUndo { get; }

    // A few words for the person while it runs ("Creating to-do list 'Groceries'").
    string Describe(JsonObject arguments);

    // A Confirm tool's card, before anything happens ("Publish the site page 'About'").
    Task<string> PreviewAsync(JsonObject arguments, SentinelAgentToolContext context, CancellationToken cancellationToken);

    Task<SentinelAgentToolResult> RunAsync(JsonObject arguments, SentinelAgentToolContext context, CancellationToken cancellationToken);

    Task UndoAsync(string undoJson, SentinelAgentToolContext context, CancellationToken cancellationToken);
}

// Each area of the app hands the agent its tools. Registered scoped: a turn resolves them in its
// own scope, so they never share the page's DbContext (see feedback on circuit-shared contexts).
public interface ISentinelAgentToolset
{
    IEnumerable<ISentinelAgentTool> Tools { get; }
}

public delegate Task<SentinelAgentToolResult> SentinelAgentRun(JsonObject arguments, SentinelAgentToolContext context, CancellationToken cancellationToken);

public delegate Task SentinelAgentUndo(string undoJson, SentinelAgentToolContext context, CancellationToken cancellationToken);

public delegate Task<string> SentinelAgentPreview(JsonObject arguments, SentinelAgentToolContext context, CancellationToken cancellationToken);

public sealed class SentinelAgentTool(
    string name,
    string description,
    string parametersJson,
    SentinelAgentRisk risk,
    IReadOnlyCollection<string> roles,
    Func<JsonObject, string> describe,
    SentinelAgentRun run,
    SentinelAgentUndo? undo = null,
    SentinelAgentPreview? preview = null) : ISentinelAgentTool
{
    public string Name => name;
    public string Description => description;
    public string ParametersJson => parametersJson;
    public SentinelAgentRisk Risk => risk;
    public IReadOnlyCollection<string> Roles => roles;
    public bool CanUndo => undo is not null;

    public string Describe(JsonObject arguments)
    {
        try
        {
            return describe(arguments);
        }
        catch (SentinelAgentToolException)
        {
            return $"Using {name}";
        }
    }

    public Task<string> PreviewAsync(JsonObject arguments, SentinelAgentToolContext context, CancellationToken cancellationToken) =>
        preview is null ? Task.FromResult(Describe(arguments)) : preview(arguments, context, cancellationToken);

    public Task<SentinelAgentToolResult> RunAsync(JsonObject arguments, SentinelAgentToolContext context, CancellationToken cancellationToken) =>
        run(arguments, context, cancellationToken);

    public Task UndoAsync(string undoJson, SentinelAgentToolContext context, CancellationToken cancellationToken) =>
        undo is null ? throw new SentinelAgentToolException($"{name} can't be undone.") : undo(undoJson, context, cancellationToken);
}

public static class SentinelAgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new SentinelAgentToolException("The saved undo details are missing.");

    // A JSON Schema object for a tool's parameters: ("title", "string", "The list's title", required).
    public static string Schema(params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
        {
            var property = parameter.Type switch
            {
                "string[]" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                _ => new JsonObject { ["type"] = parameter.Type }
            };
            property["description"] = parameter.Description;
            properties[parameter.Name] = property;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray())
        }.ToJsonString();
    }
}

// Reading a tool call's arguments the way small models send them: numbers and flags sometimes as
// strings, a list sometimes as one string of lines or as a JSON array inside a string.
public static class SentinelAgentArgs
{
    public static string Text(this JsonObject arguments, string name) =>
        arguments.OptionalText(name) ?? throw new SentinelAgentToolException($"'{name}' is required.");

    public static string? OptionalText(this JsonObject arguments, string name)
    {
        var node = arguments[name];
        var text = node switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out string? s) => s,
            JsonValue value => value.ToJsonString(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static Guid Id(this JsonObject arguments, string name) =>
        arguments.OptionalId(name) ?? throw new SentinelAgentToolException($"'{name}' is required: look it up first, then pass its id.");

    public static Guid? OptionalId(this JsonObject arguments, string name)
    {
        var text = arguments.OptionalText(name);
        if (text is null) return null;
        return Guid.TryParse(text, out var id)
            ? id
            : throw new SentinelAgentToolException($"'{text}' isn't an id. Find the item first and pass the id the tool gave you.");
    }

    public static bool Flag(this JsonObject arguments, string name, bool fallback)
    {
        var text = arguments.OptionalText(name);
        return text is null ? fallback : bool.TryParse(text, out var value) ? value : text is "1" or "yes";
    }

    public static IReadOnlyList<string> Texts(this JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            text = text.Trim();
            if (text.StartsWith('[') && TryParseArray(text) is { } parsed) node = parsed;
            else return Lines(text);
        }

        return node is JsonArray array
            ? array.Select(item => item is JsonValue v && v.TryGetValue(out string? s) ? s : item?.ToJsonString())
                .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim()).ToList()
            : [];
    }

    private static JsonArray? TryParseArray(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // "- milk\n- eggs" or "1. milk\n2. eggs" -> ["milk", "eggs"].
    private static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('-', '*', '•', ' ').Trim())
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(\d+[.)]|\[[ xX]\])\s*", string.Empty))
            .Where(line => line.Length > 0)
            .ToList();
}
