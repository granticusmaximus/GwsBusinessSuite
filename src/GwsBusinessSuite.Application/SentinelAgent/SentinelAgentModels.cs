namespace GwsBusinessSuite.Application.SentinelAgent;

// Which of this computer's Ollama models to try for the agent, best first. Only families that do
// Ollama tool calling are listed (gemma3 and deepseek-r1 don't); sentinelgpt is the Mac's
// gemma4-based build, 7/7 in the 04-09-26 competence pass against llama3.2's 2/7. A model whose
// template turns out to lack tools answers HTTP 400 and the next one is tried.
public static class SentinelAgentModels
{
    private static readonly string[] Families = ["sentinelgpt", "gemma4", "qwen3", "qwen2.5", "llama3.1", "llama3.3", "mistral", "llama3.2"];

    public static IReadOnlyList<string> LocalCandidates(IEnumerable<string> installed)
    {
        var models = installed.Where(name => !name.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
        return Families
            .SelectMany(family => models.Where(name => name.StartsWith(family, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsNoToolsError(Exception exception) =>
        exception.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase);
}
