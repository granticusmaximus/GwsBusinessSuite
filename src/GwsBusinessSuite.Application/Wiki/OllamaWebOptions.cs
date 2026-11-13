namespace GwsBusinessSuite.Application.Wiki;

public sealed class OllamaWebOptions
{
    public const string SectionName = "OllamaWeb";
    public const string DefaultBaseUrl = "https://ollama.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string ApiKey { get; init; } = string.Empty;

    // Key-less web search (13-11-26, Grant): a self-hosted SearXNG (on the AI droplet's private
    // address in production). Used when ApiKey is empty; pages are then fetched directly by the
    // server through a client that refuses private addresses.
    public string SearxngUrl { get; init; } = string.Empty;
    public int MaxResults { get; init; } = 5;
}
