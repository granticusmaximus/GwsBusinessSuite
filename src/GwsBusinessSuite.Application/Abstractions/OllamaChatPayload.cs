using System.Text.Json;
using System.Text.Json.Nodes;

namespace GwsBusinessSuite.Application.Abstractions;

// The /api/chat request and reply, shared by the server's OllamaService and the browser relay to
// this computer's Ollama (BrowserLocalOllamaService), so a tool-calling turn is the same request
// wherever the model runs.
public static class OllamaChatPayload
{
    public static JsonObject Build(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools, string keepAlive)
    {
        var messagesArray = new JsonArray();
        foreach (var message in messages)
        {
            var messageObject = new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };
            if (message.ToolCalls is { Count: > 0 } toolCalls)
            {
                var toolCallsArray = new JsonArray();
                foreach (var toolCall in toolCalls)
                {
                    toolCallsArray.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = ParseJsonOrEmptyObject(toolCall.ArgumentsJson)
                        }
                    });
                }
                messageObject["tool_calls"] = toolCallsArray;
            }
            if (message.Images is { Count: > 0 } images)
            {
                messageObject["images"] = new JsonArray(images.Select(image => (JsonNode)JsonValue.Create(image)!).ToArray());
            }
            messagesArray.Add(messageObject);
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = messagesArray,
            ["keep_alive"] = keepAlive,
            // Mirrors GwsBusinessSuite.OllamaKit.OllamaClient's tool-calling payload (the CLI and
            // Mac app's client), which had these tuned and this one didn't: Ollama's stock
            // context window is often small enough (2048-4096) to silently truncate a multi-turn
            // tool-calling transcript with no error, and a lower temperature measurably reduces
            // hallucinated tool arguments versus a chat-tuned default. think:false is safe to
            // send even to models without a "thinking" capability (they ignore it) and to
            // reasoning models that think anyway (they accept it without erroring) - verified
            // against llama3.2, qwen2.5-coder, and deepseek-r1 there; kept here rather than
            // gated on a per-model capability lookup for the same reason.
            ["options"] = new JsonObject { ["num_ctx"] = 16_384, ["temperature"] = 0.2 },
            ["think"] = false
        };
        if (tools is { Count: > 0 })
        {
            var toolsArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolsArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = ParseJsonOrEmptyObject(tool.ParametersJsonSchema)
                    }
                });
            }
            payload["tools"] = toolsArray;
        }

        return payload;
    }

    // An /api/chat reply (stream:false) as text, as the browser relay hands it back.
    public static OllamaChatResponse Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException($"Local Ollama: {error.GetString()}");
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return new OllamaChatResponse(string.Empty, []);

        var content = message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
        var calls = new List<OllamaToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function) || !function.TryGetProperty("name", out var name)) continue;
                var arguments = function.TryGetProperty("arguments", out var args) ? args : default;
                calls.Add(new OllamaToolCall(
                    name.GetString() ?? string.Empty,
                    arguments.ValueKind switch
                    {
                        JsonValueKind.Undefined => "{}",
                        // Some models send the arguments as a JSON string rather than an object.
                        JsonValueKind.String => arguments.GetString() ?? "{}",
                        _ => arguments.GetRawText()
                    }));
            }
        }

        return new OllamaChatResponse(content, calls);
    }

    public static JsonNode ParseJsonOrEmptyObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
