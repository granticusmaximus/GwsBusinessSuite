using System.Reflection;

namespace GwsBusinessSuite.SentinelCli;

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

    // A curated starting point, not the full Ollama library (https://ollama.com/library has the
    // complete catalog) - /availablemodels also accepts any model name typed directly, this list
    // just gives the session something to browse. Stick to long-established, well-known model
    // families so this doesn't rot as new releases come and go.
    public static IReadOnlyList<(string Name, string Description)> SuggestedFreeModels { get; } =
    [
        ("llama3.2", "Meta's general-purpose model (already required by GWS)"),
        ("llama3.1", "Meta's larger general-purpose model"),
        ("qwen2.5-coder", "Coding-focused model (already required by GWS)"),
        ("deepseek-r1", "Reasoning-focused model (already required by GWS)"),
        ("mistral", "Fast general-purpose model from Mistral AI"),
        ("gemma2", "Google's open-weight general-purpose model"),
        ("phi4", "Microsoft's small, efficient reasoning model"),
        ("codellama", "Meta's code-focused model"),
        ("starcoder2", "Code-focused model trained on permissively licensed source"),
        ("llava", "Vision-capable model for image understanding"),
        ("nomic-embed-text", "Lightweight embedding model"),
        ("embeddinggemma", "Google's embedding model (already required by GWS)")
    ];

    private static string ReadResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource is missing: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
