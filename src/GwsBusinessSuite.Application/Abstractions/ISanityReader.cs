namespace GwsBusinessSuite.Application.Abstractions;

public sealed record SanityArticleSummary(
    string Slug,
    string Title,
    string? MetaDescription,
    string? PrimaryKeyword,
    string? EstimatedReadingTime,
    DateTimeOffset? PublishedAt,
    bool HasHeroImage,
    string? HeroImageUrl);

public sealed record SanityArticleDetail(
    string Slug,
    string Title,
    string? Topic,
    string? MetaDescription,
    string? ArticleMarkdown,
    string? PrimaryKeyword,
    string? SecondaryKeywords,
    DateTimeOffset? PublishedAt,
    string Author,
    string? EstimatedReadingTime,
    bool HasHeroImage,
    string? HeroImageUrl,
    string? HeroImageAltText,
    string? HeroImageCaption);

public interface ISanityReader
{
    Task<IReadOnlyList<SanityArticleSummary>> GetArticlesAsync(CancellationToken ct = default);
    Task<SanityArticleDetail?> GetArticleBySlugAsync(string slug, CancellationToken ct = default);
}
