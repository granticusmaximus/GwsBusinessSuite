namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class ContentStudioOptions
{
    public const string SectionName = "ContentStudio";
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "llama3.2";
    public const string DefaultImageModel = "x/z-image-turbo";
    public const int DefaultGenerationTimeoutMinutes = 5;
    public const int DefaultImageWidth = 1200;
    public const int DefaultImageHeight = 630;
    public const int DefaultImageSteps = 4;
    public const string DefaultAuthorName = "GWS Editorial";
    public const string DefaultSiteBaseUrl = "https://example.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string Model { get; init; } = DefaultModel;
    public int GenerationTimeoutMinutes { get; init; } = DefaultGenerationTimeoutMinutes;
    public string ImageModel { get; init; } = DefaultImageModel;
    public int ImageWidth { get; init; } = DefaultImageWidth;
    public int ImageHeight { get; init; } = DefaultImageHeight;
    public int ImageSteps { get; init; } = DefaultImageSteps;
    public string AuthorName { get; init; } = DefaultAuthorName;
    public string SiteBaseUrl { get; init; } = DefaultSiteBaseUrl;
}

public sealed class SanityOptions
{
    public const string SectionName = "Sanity";

    public string ProjectId { get; init; } = string.Empty;
    public string Dataset { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string ApiVersion { get; init; } = "2021-10-21";
    public string DocumentType { get; init; } = "seoArticle";
    public string DocumentIdPrefix { get; init; } = "gws-seo-";
    public bool AutoPublishOnApproval { get; init; }
}
