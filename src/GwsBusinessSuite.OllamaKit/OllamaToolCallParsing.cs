using System.Text.Json;

namespace GwsBusinessSuite.OllamaKit;

// A fallback for models/quantizations that don't reliably emit native tool_calls on /api/chat,
// instead writing the call as JSON text in the message body. Only recognizes calls naming a tool
// already present in the definitions passed in, so it can't invent an unregistered tool.
public static class OllamaToolCallParsing
{
    public static IReadOnlyList<OllamaToolCall> TryParseContentToolCall(
        string content,
        IReadOnlyList<OllamaToolDefinition> definitions)
    {
        var candidate = content.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = candidate.IndexOf('\n');
            var closingFence = candidate.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && closingFence > firstNewline)
                candidate = candidate[(firstNewline + 1)..closingFence].Trim();
        }

        if (TryParseOneContentToolCall(candidate, definitions, out var single))
            return [single];

        var lines = candidate.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 2 or > 8)
            return [];
        var calls = new List<OllamaToolCall>(lines.Length);
        foreach (var line in lines)
        {
            if (!TryParseOneContentToolCall(line, definitions, out var call))
                return [];
            calls.Add(call);
        }
        return calls;
    }

    private static bool TryParseOneContentToolCall(
        string candidate,
        IReadOnlyList<OllamaToolDefinition> definitions,
        out OllamaToolCall call)
    {
        call = null!;
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : root.TryGetProperty("tool", out var toolElement) ? toolElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)
                || !definitions.Any(definition => string.Equals(definition.Name, name, StringComparison.Ordinal)))
                return false;
            if (!root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
                return false;
            call = new OllamaToolCall(name, arguments.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
