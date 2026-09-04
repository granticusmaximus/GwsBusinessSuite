using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.OllamaKit;

// A content delta to append to the in-progress answer, or a tool-activity notification (the
// model called a tool and is waiting on its result) - never both on the same event. UI callers
// distinguish them by which property is non-null: append ContentDelta to a transcript bubble,
// show ToolActivity as a transient "Searching..."-style indicator.
public sealed record OllamaAgentEvent(string? ContentDelta, string? ToolActivity)
{
    public static OllamaAgentEvent Content(string delta) => new(delta, null);
    public static OllamaAgentEvent Tool(string toolName) => new(null, toolName);
}

// A generic ReAct-style tool-calling loop: stream a response, dispatch any tool calls the model
// makes via the injected executor, feed results back, and repeat until the model gives a final
// answer with no further tool calls or _maxRounds is exhausted. Deliberately has no persona
// system, no plan-mode, and no knowledge of what a "tool" does - callers needing that (see
// SentinelCLI's SentinelCodingAgent) build it on top rather than this class growing it.
public sealed class OllamaToolCallingAgent
{
    private readonly OllamaClient _ollama;
    private readonly IOllamaToolExecutor _tools;
    private readonly string _model;
    private readonly string _systemPrompt;
    private readonly int _maxRounds;
    private readonly List<OllamaChatMessage> _messages;

    public OllamaToolCallingAgent(
        OllamaClient ollama, IOllamaToolExecutor tools, string model, string systemPrompt, int maxRounds = 6)
    {
        _ollama = ollama;
        _tools = tools;
        _model = model;
        _systemPrompt = systemPrompt;
        _maxRounds = maxRounds;
        _messages = [new OllamaChatMessage("system", systemPrompt)];
    }

    public IReadOnlyList<OllamaChatMessage> Messages => _messages;

    public void LoadConversation(IEnumerable<OllamaChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        if (_messages.Count == 0)
            _messages.Add(new OllamaChatMessage("system", _systemPrompt));
    }

    public void ClearConversation()
    {
        _messages.Clear();
        _messages.Add(new OllamaChatMessage("system", _systemPrompt));
    }

    public async IAsyncEnumerable<OllamaAgentEvent> RunTurnStreamingAsync(
        string prompt, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _messages.Add(new OllamaChatMessage("user", prompt.Trim()));

        // Identical repeat calls within one turn are a small-model loop symptom, not progress: the
        // same tool with the same arguments returns the same result, so re-running it can only burn
        // rounds (and, for a tool reporting itself unavailable, burn all of them). Tracked per turn
        // and answered with a corrective tool result instead, the same technique the malformed
        // tool-call branch below already uses. Varied calls to the same tool are untouched - reading
        // five different files in one turn is legitimate work.
        var executedCalls = new HashSet<string>(StringComparer.Ordinal);

        for (var round = 1; round <= _maxRounds; round++)
        {
            var content = new StringBuilder();
            IReadOnlyList<OllamaToolCall>? toolCalls = null;

            await foreach (var chunk in _ollama.ChatStreamAsync(_model, _messages, _tools.Definitions, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                {
                    content.Append(chunk.ContentDelta);
                    yield return OllamaAgentEvent.Content(chunk.ContentDelta);
                }
                if (chunk.ToolCalls is { Count: > 0 } calls)
                    toolCalls = calls;
            }

            // No native tool_calls arrived - fall back to parsing the streamed content itself,
            // same as the non-streaming coding-agent loop does for models that don't reliably
            // emit native tool_calls.
            toolCalls ??= OllamaToolCallParsing.TryParseContentToolCall(content.ToString(), _tools.Definitions);

            if (toolCalls.Count == 0 && OllamaToolCallParsing.LooksLikeFailedToolCallAttempt(content.ToString()))
            {
                // The model clearly attempted a tool call (JSON-shaped, has "name") but it didn't
                // parse - wrong field name, invalid JSON tokens, etc. Threading this back as a
                // "tool" result (the same shape a real tool's own validation error uses) gives the
                // model one corrective round instead of the raw, malformed attempt being shown to
                // the user as if it were a real answer. Counts against _maxRounds like any other
                // round, so this can't loop forever. The malformed JSON already streamed out as
                // Content events above (streaming can't know it's malformed until it's complete) -
                // yielding a Tool event here tells UI callers to treat that streamed-so-far text as
                // transient activity to be replaced, the same as a real tool call's activity
                // indicator, instead of leaving it on screen concatenated with what follows.
                yield return OllamaAgentEvent.Tool("retrying");
                _messages.Add(new OllamaChatMessage("assistant", content.ToString()));
                _messages.Add(new OllamaChatMessage("tool", JsonSerializer.Serialize(new
                {
                    error = "That wasn't a real tool call - either it was narrated in prose instead of issued " +
                            "through the tool-calling mechanism, or it used a placeholder/example value instead " +
                            "of a real one, or the JSON was malformed (wrong field name, invalid tokens like " +
                            "undefined). Never describe what a tool call would look like and never invent a " +
                            "placeholder id/argument - either issue the tool call for real with a real argument " +
                            "value you actually have (e.g. an id from a prior tool result), or, if you don't have " +
                            "what a tool call needs, say so directly in plain text instead of showing an example."
                })) { ToolName = "invalid_tool_call" });
                continue;
            }

            _messages.Add(new OllamaChatMessage("assistant", content.ToString())
            {
                ToolCalls = toolCalls.Count > 0 ? toolCalls.Select(ToApiToolCall).ToArray() : null
            });

            if (toolCalls.Count == 0)
            {
                if (content.Length == 0)
                    throw new InvalidOperationException("Ollama returned an empty response.");
                yield break;
            }

            foreach (var toolCall in toolCalls)
            {
                if (!executedCalls.Add(CallSignature(toolCall)))
                {
                    // No activity event: nothing actually ran, so there's no work for a UI to
                    // narrate - only the model needs to hear about this.
                    _messages.Add(new OllamaChatMessage("tool", JsonSerializer.Serialize(new
                    {
                        error = "You already made this exact tool call earlier in this turn and its result is " +
                                "above. Repeating it returns the same thing and cannot get you anything new - " +
                                "use the result you already have, try genuinely different arguments if that is " +
                                "warranted, or answer in plain text saying what you couldn't find."
                    })) { ToolName = toolCall.Name });
                    continue;
                }

                yield return OllamaAgentEvent.Tool(toolCall.Name);
                var result = await _tools.ExecuteAsync(toolCall, cancellationToken);
                _messages.Add(new OllamaChatMessage("tool", result) { ToolName = toolCall.Name });
            }
        }

        throw new InvalidOperationException($"SentinelGPT did not finish within {_maxRounds} tool-call rounds.");
    }

    // Re-serialized rather than compared raw so the same call doesn't slip through on incidental
    // formatting differences (whitespace, property order) between one round and the next.
    private static string CallSignature(OllamaToolCall call)
    {
        string arguments;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            arguments = JsonSerializer.Serialize(NormalizeJson(document.RootElement));
        }
        catch (JsonException)
        {
            arguments = call.ArgumentsJson;
        }
        return $"{call.Name} {arguments}";
    }

    private static object? NormalizeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(property => property.Name, property => NormalizeJson(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJson).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static OllamaApiToolCall ToApiToolCall(OllamaToolCall call)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        return new OllamaApiToolCall
        {
            Type = "function",
            Function = new OllamaApiFunctionCall { Name = call.Name, Arguments = document.RootElement.Clone() }
        };
    }
}
