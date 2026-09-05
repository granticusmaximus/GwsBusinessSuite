using System.Reflection;

namespace GwsBusinessSuite.SentinelAgentKit;

// One browsable entry in the suggested-model list. ApproximateSize is a string rather than a
// number because it's a human hint for "will this fit and how long will it take", not something
// to compute with - Ollama only reports a real size once a model is actually installed.
public sealed record SuggestedModel(
    string Name,
    string Description,
    string ApproximateSize,
    bool SupportsTools);

public static class ModelCatalog
{
    private const string ModelResource = "GwsBusinessSuite.SentinelAgentKit.required-models.txt";
    private const string ProfileResource = "GwsBusinessSuite.SentinelAgentKit.SentinelGPT.Modelfile";

    public static IReadOnlyList<string> RequiredModels { get; } = ReadResource(ModelResource)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !line.StartsWith('#'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string SentinelProfile => ReadResource(ProfileResource);

    public static IReadOnlyList<string> ExpectedInstalledModels =>
        [.. RequiredModels, "sentinelgpt"];

    // A curated starting point, not the full Ollama library (https://ollama.com/library has the
    // complete catalog, and there is no public API to enumerate it) - every browser built on
    // this also accepts any model name typed directly, so this list is a shortcut rather than a
    // limit. All of these are free, openly-licensed weights.
    //
    // SupportsTools is the field worth reading before installing something for SentinelGPT: a
    // model without it can hold a conversation but can never search the wiki or touch an
    // attached folder, because Ollama rejects a populated tools array outright for such a model.
    public static IReadOnlyList<SuggestedModel> SuggestedFreeModels { get; } =
    [
        new("gemma4", "Google - the SentinelGPT profile's base. Best all-round accuracy per second here.", "~9.6 GB", true),
        new("llama3.2", "Meta - very small and fast, but reaches for tools it doesn't need.", "~2 GB", true),
        new("qwen2.5-coder", "Alibaba - coding-focused, strong at code explanation and review.", "~4.7 GB", true),
        new("qwen3", "Alibaba - strong general reasoning; the 14b tag is noticeably slower.", "~9 GB", true),
        new("mistral", "Mistral AI - fast, capable general-purpose model.", "~4 GB", true),
        new("phi4", "Microsoft - small and efficient, good at structured reasoning.", "~9 GB", true),
        new("deepseek-r1", "DeepSeek - reasoning-focused. Cannot use tools; thinks before answering.", "~5 GB", false),
        new("gemma3", "Google - solid general-purpose chat, but no tool-calling support.", "~8 GB", false),
        new("llava", "Vision-capable model for image understanding. No tool-calling.", "~4.7 GB", false),
        new("embeddinggemma", "Google's embedding model - used for search indexing, not chat.", "~0.6 GB", false),
        new("nomic-embed-text", "Lightweight embedding model - used for search indexing, not chat.", "~0.3 GB", false)
    ];

    private static string ReadResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource is missing: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
