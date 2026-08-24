using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.OllamaKit;

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

// ArgumentsJson is a raw string rather than JsonElement so a call round-trips cleanly through
// JSON persistence (session history) without a JsonDocument lifetime to manage - callers that
// need structured access parse it themselves via JsonDocument.Parse(call.ArgumentsJson).
public sealed record OllamaToolCall(string Name, string ArgumentsJson);

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

// One item per streamed /api/chat line. ToolCalls is populated on the (typically final) chunk
// that carries them - Ollama does not interleave tool_calls across multiple chunks - and Done
// marks the terminal chunk, mirroring OllamaChatResult's shape closely enough that a caller can
// accumulate ContentDelta into the same Content a non-streaming ChatAsync would have returned.
public sealed record OllamaChatStreamChunk(string ContentDelta, IReadOnlyList<OllamaToolCall>? ToolCalls, bool Done);
