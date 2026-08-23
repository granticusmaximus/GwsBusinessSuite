using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.SentinelCli;

public sealed record OllamaToolDefinition(string Name, string Description, string ParametersJsonSchema)
{
    public object ToApiShape() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = Description,
            parameters = JsonSerializer.Deserialize<JsonElement>(ParametersJsonSchema)
        }
    };
}

public sealed record OllamaToolCall(string Name, JsonElement Arguments);

public sealed class OllamaChatMessage
{
    public OllamaChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }

    [JsonPropertyName("role")]
    public string Role { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OllamaApiToolCall>? ToolCalls { get; init; }
}

public sealed class OllamaApiToolCall
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public required OllamaApiFunctionCall Function { get; init; }
}

public sealed class OllamaApiFunctionCall
{
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

public sealed record OllamaChatResult(
    string Content,
    IReadOnlyList<OllamaToolCall> ToolCalls,
    OllamaChatMessage AssistantMessage);

public static class ModelCatalog
{
    private const string ModelResource = "GwsBusinessSuite.SentinelCli.required-models.txt";
    private const string ProfileResource = "GwsBusinessSuite.SentinelCli.SentinelGPT.Modelfile";

    public static IReadOnlyList<string> RequiredModels { get; } = ReadResource(ModelResource)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !line.StartsWith('#'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string SentinelProfile => ReadResource(ProfileResource);

    public static IReadOnlyList<string> ExpectedInstalledModels =>
        [.. RequiredModels, "sentinelgpt"];

    private static string ReadResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource is missing: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
