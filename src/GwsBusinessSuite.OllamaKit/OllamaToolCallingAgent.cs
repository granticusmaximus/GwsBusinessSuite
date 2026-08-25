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
                yield return OllamaAgentEvent.Tool(toolCall.Name);
                var result = await _tools.ExecuteAsync(toolCall, cancellationToken);
                _messages.Add(new OllamaChatMessage("tool", result) { ToolName = toolCall.Name });
            }
        }

        throw new InvalidOperationException($"SentinelGPT did not finish within {_maxRounds} tool-call rounds.");
    }

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
