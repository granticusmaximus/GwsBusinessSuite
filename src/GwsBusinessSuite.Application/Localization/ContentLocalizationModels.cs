using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Localization;

public static class ContentLocalizationContentTypes
{
    public const string Article = "Article";
    public const string CmsPage = "CmsPage";
}

public sealed record LocalizableContent(Guid Id, string Title, string ContentType);

public sealed record ContentLocalizationSummary(
    Guid Id,
    string LanguageCode,
    string Status,
    bool IsAiGenerated,
    string? AiModel,
    DateTimeOffset UpdatedAt);

public sealed record ContentLocalizationDetail(
    Guid Id,
    string ContentType,
    Guid ContentId,
    string LanguageCode,
    string Title,
    string Body,
    string? MetaDescription,
    string Status,
    bool IsAiGenerated,
    string? AiModel,
    DateTimeOffset UpdatedAt);

// Resolved translated content for a single public-facing render (see GetPublishedAsync) -
// deliberately narrower than ContentLocalizationDetail, since the public route only ever needs
// enough to substitute into the existing render path, not the admin bookkeeping fields.
public sealed record PublishedLocalization(string Title, string Body, string? MetaDescription);

public sealed class ContentLocalizationEditor
{
    public string LanguageCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? MetaDescription { get; set; }
    public string Status { get; set; } = ContentLocalizationStatuses.Draft;
}
