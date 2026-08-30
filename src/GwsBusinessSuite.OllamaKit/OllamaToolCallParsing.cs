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

    // A lenient companion to TryParseContentToolCall: recognizes content that was clearly an
    // *attempted* tool call even though it failed to parse as one (wrong field name, invalid
    // JSON tokens, unregistered tool name, etc.). Callers use this to give the model one
    // corrective retry instead of showing the raw, malformed attempt to the user as if it were a
    // real answer. Deliberately narrower than "any JSON-looking text" - a genuine final answer is
    // never a bare JSON object whose properties are "name" alongside "arguments"/"parameters".
    //
    // Scans every top-level balanced-brace object in the content, not just a whole-content match -
    // a model that isn't confident enough to emit a clean tool call will sometimes narrate around
    // it instead ("I don't have direct access, so I'll use the get_page function: {"name":...}"),
    // which used to slip past this check entirely (it didn't start with '{') and get shown to the
    // user as a normal final answer instead of being corrected. That narrated-example shape is just
    // as much a failed attempt as a bare malformed object - the model needs the same "call it for
    // real, or say you can't" correction either way.
    public static bool LooksLikeFailedToolCallAttempt(string content) =>
        ExtractTopLevelJsonObjectSpans(content).Any(candidate =>
            candidate.Contains("\"name\"") && (candidate.Contains("\"arguments\"") || candidate.Contains("\"parameters\"")));

    private static IEnumerable<string> ExtractTopLevelJsonObjectSpans(string content)
    {
        var depth = 0;
        var start = -1;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (content[i] == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                    yield return content[start..(i + 1)];
            }
        }
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
