namespace GwsBusinessSuite.Application.Abstractions;

// Choosing which locally installed Ollama model answers a browser-side request.
public static class LocalModels
{
    // General chat models first (gemma and qwen follow instructions and JSON formats more
    // reliably than llama3.2), never an embedding model, which can't generate text.
    public static string? PickChatModel(IEnumerable<string> installed)
    {
        var usable = installed.Where(name => !name.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
        string? Find(string prefix) => usable.FirstOrDefault(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return Find("gemma") ?? Find("qwen") ?? Find("llama3") ?? Find("mistral") ?? Find("phi") ?? usable.FirstOrDefault();
    }
}
