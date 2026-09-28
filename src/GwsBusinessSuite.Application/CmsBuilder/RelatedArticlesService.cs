using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IRelatedArticlesService
{
    Task<List<RelatedArticleView>> GetRelatedArticlesAsync(Article article, int take = 3, CancellationToken cancellationToken = default);
}

// Extracted from Program.cs's own GetRelatedArticlesAsync local function so both the hardcoded
// /blog/{slug} route and the page-editor's "related-posts" widget (CmsBlockHtmlRenderer) can
// share one scoring implementation instead of drifting apart. Lives in the Application layer
// using IAppDbContext (the same abstraction CommentService already uses for exactly this reason)
// rather than Infrastructure - CmsBlockHtmlRenderer's own doc comment says it has "no DB access
// of its own" by design, and IAppDbContext already exposes Articles with zero direct EF-provider
// dependency, so a genuine Infrastructure-layer service was never actually necessary here.
public sealed class RelatedArticlesService(IAppDbContext db) : IRelatedArticlesService
{
    // Scored by shared category (weighted higher) plus shared tags, entirely from data every
    // article already carries - no embedding index required. Verified finding (unchanged from
    // the original Program.cs implementation): the site's semantic search index only covers CMS
    // pages, wiki pages, and CRM records, not blog Articles, so building this on
    // IHybridSearchService would have meant standing up a new indexing path rather than reusing
    // an existing one.
    public async Task<List<RelatedArticleView>> GetRelatedArticlesAsync(Article article, int take = 3, CancellationToken cancellationToken = default)
    {
        var currentTags = ParseTags(article.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        // Matches LoadPublicArticleSummariesAsync's/IsArticlePubliclyVisible's own public-
        // visibility bar (Status == Published, not just "has a PublishedAt value") - the original
        // check this was extracted from only tested PublishedAt != null, which let a Draft article
        // that happened to carry an old PublishedAt timestamp leak into recommendations. The
        // future-dated (scheduled) exclusion is applied after materializing rather than in the SQL
        // Where clause - this project's EF Core/SQLite combination can't reliably translate a
        // DateTimeOffset range comparison server-side (an established, previously-hit gotcha), and
        // this query already has to materialize before scoring anyway since scoring needs
        // client-side tag-list operations.
        var candidates = await db.Articles
            .AsNoTracking()
            .Where(x => x.Id != article.Id && x.TrashedAt == null && x.Status == ArticleStatuses.Published && x.PublishedAt != null)
            .Select(x => new { x.Title, x.Slug, x.CategoryId, x.Tags, x.PublishedAt, x.HeroImageUrl, x.HeroImageDataUri })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(x => x.PublishedAt <= now)
            .Select(x => new
            {
                x.Title,
                x.Slug,
                x.PublishedAt,
                x.HeroImageUrl,
                x.HeroImageDataUri,
                // Distinct(OrdinalIgnoreCase) before counting - a candidate with sloppy/duplicate
                // CSV tags (e.g. "dotnet, DOTNET, Dotnet") must not inflate its score by matching
                // the same shared tag more than once.
                Score = (x.CategoryId.HasValue && x.CategoryId == article.CategoryId ? 2 : 0)
                      + ParseTags(x.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Count(t => currentTags.Contains(t))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.PublishedAt)
            .Take(Math.Clamp(take, 1, 6))
            .Select(x => new RelatedArticleView(
                x.Title,
                x.Slug,
                string.IsNullOrEmpty(x.HeroImageUrl)
                    ? (string.IsNullOrEmpty(x.HeroImageDataUri) ? null : $"/og-image/{x.Slug}")
                    : x.HeroImageUrl))
            .ToList();
    }

    private static List<string> ParseTags(string tags) =>
        tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
