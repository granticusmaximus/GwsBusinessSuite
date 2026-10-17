using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Offline reading (Sentinel item 12). See SentinelOffline.
public sealed class SentinelOfflineService(IAppDbContext db, ISentinelAccessService accessService) : ISentinelOfflineService
{
    public Task<bool> IsKeptOfflineAsync(Guid pageId, string username, CancellationToken cancellationToken = default) =>
        db.SentinelOfflinePages.AnyAsync(item => item.WikiPageId == pageId && item.Username == username, cancellationToken);

    public async Task SetKeptOfflineAsync(Guid pageId, string username, bool keep, CancellationToken cancellationToken = default)
    {
        var existing = await db.SentinelOfflinePages.FirstOrDefaultAsync(item => item.WikiPageId == pageId && item.Username == username, cancellationToken);
        if (!keep)
        {
            if (existing is null) return;
            db.SentinelOfflinePages.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if (existing is not null) return;
        if (!await accessService.CanAccessAsync(pageId, false, username, SentinelAccessLevels.View, cancellationToken))
            throw new ArgumentException("You can't view that page.");
        if (await db.SentinelOfflinePages.CountAsync(item => item.Username == username, cancellationToken) >= SentinelOffline.MaxPagesPerUser)
            throw new ArgumentException($"You can keep up to {SentinelOffline.MaxPagesPerUser} pages offline - remove one first.");
        db.SentinelOfflinePages.Add(new SentinelOfflinePage { Username = username, WikiPageId = pageId, CreatedBy = username });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelOfflinePageCopy>> GetCopiesAsync(string username, CancellationToken cancellationToken = default)
    {
        var pageIds = await db.SentinelOfflinePages.AsNoTracking()
            .Where(item => item.Username == username)
            .Select(item => item.WikiPageId)
            .ToListAsync(cancellationToken);
        if (pageIds.Count == 0) return [];
        var visible = await accessService.GetAccessibleTargetsAsync(
            pageIds.Select(id => new SentinelAccessTarget(id, false)).ToList(), username, SentinelAccessLevels.View, cancellationToken);
        var pages = await db.WikiPages.AsNoTracking()
            .Where(page => pageIds.Contains(page.Id) && page.TrashedAt == null)
            .ToListAsync(cancellationToken);
        // Linked [[pages]] resolve against every page title, like the editor's read-only render.
        var allPages = await db.WikiPages.AsNoTracking().Where(page => page.TrashedAt == null)
            .Select(page => new WikiPage { Id = page.Id, Title = page.Title, Slug = page.Slug })
            .ToListAsync(cancellationToken);

        var copies = new List<SentinelOfflinePageCopy>();
        foreach (var page in pages.Where(page => visible.Contains(new SentinelAccessTarget(page.Id, false)))
                     .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase))
        {
            var html = WikiBlockHtmlRenderer.RenderPage(WikiBlockJson.ParseBlocks(page.BlocksJson), allPages);
            if (html.Length > SentinelOffline.MaxHtmlLength) html = "<p><em>This page is too large to keep offline.</em></p>";
            copies.Add(new SentinelOfflinePageCopy(page.Id, string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title, page.Icon,
                (page.UpdatedAt ?? page.CreatedAt).ToUnixTimeSeconds(), html));
        }
        return copies;
    }
}
