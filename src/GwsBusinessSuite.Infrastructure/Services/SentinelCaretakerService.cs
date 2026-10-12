using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelCaretakerService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null,
    IHybridSearchService? hybridSearch = null) : ISentinelCaretakerService
{
    // Dates are compared in memory throughout: SQLite can't compare DateTimeOffset.
    private async Task<List<WikiPage>> LivePagesAsync(string username, CancellationToken ct)
    {
        var pages = await dbContext.WikiPages.AsNoTracking()
            .Where(p => p.TrashedAt == null && p.SystemKey == null && p.NotionArchivedAt == null)
            .ToListAsync(ct);
        if (accessService is null) return pages;
        var visible = await accessService.GetAccessibleTargetsAsync(pages.Select(p => new SentinelAccessTarget(p.Id, IsDatabase: false)).ToList(), username, SentinelAccessLevels.View, ct);
        return pages.Where(p => visible.Contains(new SentinelAccessTarget(p.Id, IsDatabase: false))).ToList();
    }

    public async Task<IReadOnlyList<SentinelChangedPage>> RecentChangesAsync(string username, int days, CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90));
        return (await LivePagesAsync(username, cancellationToken))
            .Where(p => (p.UpdatedAt ?? p.CreatedAt) >= since)
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(p => new SentinelChangedPage(p.Id, p.Title, p.UpdatedBy ?? p.CreatedBy, p.UpdatedAt ?? p.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<SentinelStalePage>> StalePagesAsync(string username, int days, CancellationToken cancellationToken = default)
    {
        var before = timeProvider.GetUtcNow().AddDays(-Math.Clamp(days, 7, 3650));
        return (await LivePagesAsync(username, cancellationToken))
            .Where(p => p.OwnerUsername is { Length: > 0 } && (p.UpdatedAt ?? p.CreatedAt) < before)
            .OrderBy(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(p => new SentinelStalePage(p.Id, p.Title, p.OwnerUsername!, p.UpdatedAt ?? p.CreatedAt))
            .ToList();
    }

    public async Task<int> NudgeOwnersAsync(IReadOnlyList<SentinelStalePage> pages, int days, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var window = now.AddDays(-Math.Clamp(days, 1, 365));
        var recent = (await dbContext.SentinelNotifications.AsNoTracking()
                .Where(n => n.Kind == SentinelCaretaker.NudgeKind).Select(n => new { n.Username, n.CreatedAt }).ToListAsync(cancellationToken))
            .Where(n => n.CreatedAt >= window).Select(n => n.Username.ToLowerInvariant()).ToHashSet();
        var sent = 0;
        foreach (var owner in pages.GroupBy(p => p.Owner.ToLowerInvariant()))
        {
            if (recent.Contains(owner.Key)) continue;
            var list = owner.ToList();
            await dbContext.SentinelNotifications.AddAsync(new SentinelNotification
            {
                Username = list[0].Owner,
                Kind = SentinelCaretaker.NudgeKind,
                WikiPageId = list[0].Id,
                Message = SentinelCaretaker.NudgeMessage(list, days),
                CreatedAt = now,
                CreatedBy = "caretaker"
            }, cancellationToken);
            sent++;
        }
        if (sent > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return sent;
    }

    public async Task<IReadOnlyList<SentinelOrphanPage>> OrphanPagesAsync(string username, int limit, CancellationToken cancellationToken = default)
    {
        var pages = await LivePagesAsync(username, cancellationToken);
        // Linked if any page's blocks point at it, or it sits under a parent in the tree.
        var allBlocks = await dbContext.WikiPages.AsNoTracking().Where(p => p.TrashedAt == null).Select(p => p.BlocksJson).ToListAsync(cancellationToken);
        var linked = new HashSet<Guid>();
        foreach (var json in allBlocks)
        {
            foreach (var block in WikiBlockJson.ParseBlocks(json))
            {
                foreach (var span in block.RichText.Where(s => s.Link?.StartsWith("wikilink:", StringComparison.Ordinal) == true))
                    if (Guid.TryParse(span.Link!["wikilink:".Length..], out var id)) linked.Add(id);
                if (block.Props.TryGetValue("pageId", out var pageId) && Guid.TryParse(pageId, out var linkedId)) linked.Add(linkedId);
            }
        }
        var orphans = pages.Where(p => p.ParentWikiPageId is null && !linked.Contains(p.Id))
            .OrderBy(p => p.CreatedAt).Take(Math.Clamp(limit, 1, 50)).ToList();
        var visibleIds = pages.Select(p => p.Id).ToHashSet();
        var titles = pages.ToDictionary(p => p.Id, p => p.Title);
        var result = new List<SentinelOrphanPage>();
        foreach (var orphan in orphans)
        {
            IReadOnlyList<SentinelRelatedPage> suggestions = [];
            if (hybridSearch is not null && orphan.Title.Trim().Length > 0)
            {
                suggestions = (await hybridSearch.SearchAsync(orphan.Title, [SemanticSourceTypes.WikiPage], 6, cancellationToken))
                    .Where(h => h.SourceId != orphan.Id && visibleIds.Contains(h.SourceId)).DistinctBy(h => h.SourceId).Take(3)
                    .Select(h => new SentinelRelatedPage(h.SourceId, titles[h.SourceId], null, h.Preview)).ToList();
            }
            result.Add(new SentinelOrphanPage(orphan.Id, orphan.Title, suggestions));
        }
        return result;
    }
}
