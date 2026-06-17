using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Blog;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SanityImportService(
    ISanityReader sanityReader,
    IDbContextFactory<ApplicationDbContext> dbFactory) : ISanityImportService
{
    public async Task<SanityImportResult> ImportAsync(CancellationToken ct = default)
    {
        var summaries = await sanityReader.GetArticlesAsync(ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existingSlugs = await db.Articles
            .Select(a => a.Slug)
            .ToHashSetAsync(ct);

        int imported = 0, skipped = 0, errors = 0;

        foreach (var summary in summaries)
        {
            if (existingSlugs.Contains(summary.Slug))
            {
                skipped++;
                continue;
            }

            try
            {
                var detail = await sanityReader.GetArticleBySlugAsync(summary.Slug, ct);
                if (detail is null)
                {
                    errors++;
                    continue;
                }

                var article = new Article
                {
                    Slug              = detail.Slug,
                    Title             = detail.Title,
                    Topic             = detail.Topic,
                    BodyMarkdown      = detail.ArticleMarkdown ?? string.Empty,
                    MetaDescription   = detail.MetaDescription ?? string.Empty,
                    PrimaryKeyword    = detail.PrimaryKeyword ?? string.Empty,
                    SecondaryKeywords = detail.SecondaryKeywords ?? string.Empty,
                    Author            = detail.Author,
                    EstimatedReadingTime = detail.EstimatedReadingTime ?? string.Empty,
                    HeroImageUrl      = detail.HeroImageUrl,
                    HeroImageAltText  = detail.HeroImageAltText ?? string.Empty,
                    HeroImageCaption  = detail.HeroImageCaption ?? string.Empty,
                    Status            = ArticleStatuses.Published,
                    Source            = ArticleSource.SanityImport,
                    PublishedAt       = detail.PublishedAt,
                    CreatedBy         = "sanity-import"
                };

                db.Articles.Add(article);
                await db.SaveChangesAsync(ct);
                imported++;
            }
            catch
            {
                errors++;
            }
        }

        var message = $"Import complete: {imported} imported, {skipped} skipped (already exist), {errors} errors.";
        return new SanityImportResult(imported, skipped, errors, message);
    }
}
