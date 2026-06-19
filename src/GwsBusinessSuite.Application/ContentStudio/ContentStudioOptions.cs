namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class ContentStudioOptions
{
    public const string SectionName = "ContentStudio";
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "llama3.2";
    public const int DefaultGenerationTimeoutMinutes = 5;
    public const string DefaultAuthorName = "GWS Editorial";
    public const string DefaultSiteBaseUrl = "https://example.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string Model { get; init; } = DefaultModel;
    public int GenerationTimeoutMinutes { get; init; } = DefaultGenerationTimeoutMinutes;
    public string AuthorName { get; init; } = DefaultAuthorName;
    public string SiteBaseUrl { get; init; } = DefaultSiteBaseUrl;
}
