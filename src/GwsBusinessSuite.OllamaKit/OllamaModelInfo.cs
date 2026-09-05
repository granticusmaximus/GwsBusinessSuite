namespace GwsBusinessSuite.OllamaKit;

// What /api/tags reports about one installed model beyond its bare name. Ollama gates chat and
// tool-calling per model and answers HTTP 400 rather than degrading - confirmed empirically
// against a local 0.33.3 daemon:
//
//   embeddinggemma + any chat request  -> {"error":"\"embeddinggemma\" does not support chat"}
//   gemma3:12b     + a non-empty tools -> {"error":"...gemma3:12b does not support tools"}
//
// so any host that lets a human pick a model has to know these up front. Picking an
// embedding-only or vision-only model is never recoverable, and offering tools to a model
// without the "tools" capability fails the entire request rather than just ignoring the tools
// (an *empty* tools array is fine on such a model - also confirmed empirically - which is what
// makes "keep chatting, just without tools" a usable state instead of a hard error).
public sealed record OllamaModelInfo(
    string Name,
    string? ParameterSize,
    IReadOnlyList<string> Capabilities)
{
    // "completion" is Ollama's capability name for ordinary text generation, which is what
    // /api/chat needs; an embedding- or image-only model reports neither.
    public bool SupportsChat => Has("completion");

    public bool SupportsTools => Has("tools");

    // Reasoning models emit a separate thinking stream before their answer. It is worth real
    // wall-clock time (measured on an M3 Pro: gemma4 spent 232 generated tokens deliberating
    // over "what is 17 * 23?" with thinking on, versus 4 tokens and a correct answer with
    // think:false), so hosts that care about latency turn it off explicitly.
    public bool SupportsThinking => Has("thinking");

    private bool Has(string capability) =>
        Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}
