using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Articles that need a refresh, and SentinelGPT's suggested changes for one (19-11-26). Page views
// come from the site's own analytics (/blog/{slug}); dates are compared in memory (SQLite).
public sealed class ArticleRefreshService(IAppDbContextFactory contexts, IOllamaService ollama, IOptions<ContentStudioOptions> options, TimeProvider time)
{
    public async Task<IReadOnlyList<RefreshCandidate>> CandidatesAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var articles = await db.Articles.AsNoTracking().Where(a => a.Status == ArticleStatuses.Published).ToListAsync(ct);
        var views = (await db.WebAnalyticsEvents.AsNoTracking()
                .Where(e => e.EventName == WebAnalyticsEventNames.PageView && e.Path.StartsWith("/blog/"))
                .Select(e => new { e.Path, e.CreatedAt })
                .ToListAsync(ct))
            .Where(e => e.CreatedAt >= now.AddDays(-60))
            .ToList();
        var last30 = views.Where(e => e.CreatedAt >= now.AddDays(-30)).GroupBy(e => e.Path.TrimEnd('/')).ToDictionary(g => g.Key, g => g.Count());
        var prior30 = views.Where(e => e.CreatedAt < now.AddDays(-30)).GroupBy(e => e.Path.TrimEnd('/')).ToDictionary(g => g.Key, g => g.Count());
        return ArticleRefresh.Find(articles, last30, prior30, now);
    }

    // Mac first, like all SentinelGPT work; throws when no model answers, for the page to say so.
    public async Task<string?> SuggestAsync(Guid articleId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var article = await db.Articles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == articleId, ct);
        if (article is null) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        var text = await ollama.GenerateAsync(options.Value.Model, ArticleRefresh.SystemPrompt, ArticleRefresh.Prompt(article, time.GetUtcNow()), limit.Token);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
