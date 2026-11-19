namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class ContentStudioOptions
{
    public const string SectionName = "ContentStudio";
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "sentinelgpt";
    public const int DefaultGenerationTimeoutMinutes = 20;
    public const string DefaultAuthorName = "GWS Editorial";
    public const string DefaultSiteBaseUrl = "https://example.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string Model { get; init; } = DefaultModel;
    public int GenerationTimeoutMinutes { get; init; } = DefaultGenerationTimeoutMinutes;
    public string AuthorName { get; init; } = DefaultAuthorName;
    public string SiteBaseUrl { get; init; } = DefaultSiteBaseUrl;

    // One context size for every request to the server's Ollama (ContentStudio:ContextLength, 19-11-26).
    // Ollama reloads a model whenever a request asks for a different num_ctx, and callers asked for
    // 4096, 8192 and 16384: on the AI droplet that reloaded the 2 GB model 18 times in 40 minutes,
    // so every background job timed out. Unset, each caller's own size is sent as before.
    public int? ContextLength { get; init; }
}
