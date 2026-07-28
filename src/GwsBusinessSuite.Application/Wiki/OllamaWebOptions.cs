namespace GwsBusinessSuite.Application.Wiki;

public sealed class OllamaWebOptions
{
    public const string SectionName = "OllamaWeb";
    public const string DefaultBaseUrl = "https://ollama.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string ApiKey { get; init; } = string.Empty;
    public int MaxResults { get; init; } = 5;
}
