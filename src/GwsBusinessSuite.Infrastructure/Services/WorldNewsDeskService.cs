using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// N2 (World News trust plan, 13-11-26): a verified story becomes a draft article in Content, with
// the evidence written in (StoryDraft). A draft only: a person edits and publishes it.
public sealed partial class WorldNewsDeskService(IAppDbContext db, WorldNewsService news)
{
    public async Task<Guid> CreateDraftAsync(StoryDocument story, EvidenceBoard? board, string username, CancellationToken cancellationToken = default)
    {
        var checks = news.Store.DeskChecks(story.Event.Id);
        var title = story.Event.Headline ?? story.Event.Title;
        var site = await db.SiteSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var article = new Article
        {
            Title = title.Length > 200 ? title[..200] : title,
            Slug = await UniqueSlugAsync(title, cancellationToken),
            BodyMarkdown = StoryDraft.Markdown(story, checks, board, at => at.ToLocalGwsDateTime()),
            MetaDescription = story.Trust is { } trust ? $"{trust.Confidence.Label}: {trust.Confidence.Reason}" : string.Empty,
            // "verified" only once the desk has ticked every checklist item with its evidence.
            Tags = DeskChecklist.IsComplete(checks) ? "world news, verified" : "world news",
            Status = ArticleStatuses.Draft,
            Source = ArticleSource.Manual,
            CategoryId = site?.DefaultArticleCategoryId,
            CreatedBy = username,
        };
        if (!string.IsNullOrWhiteSpace(site?.DefaultAuthorByline)) article.Author = site.DefaultAuthorByline;
        db.Articles.Add(article);
        await db.SaveChangesAsync(cancellationToken);
        return article.Id;
    }

    private async Task<string> UniqueSlugAsync(string title, CancellationToken cancellationToken)
    {
        var slug = NotSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].Trim('-');
        if (slug.Length == 0) slug = "world-news";
        var candidate = slug;
        for (var n = 2; await db.Articles.AnyAsync(a => a.Slug == candidate, cancellationToken); n++) candidate = $"{slug}-{n}";
        return candidate;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
