using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Private Sentinel (12-11-26): the app's own pages (the Quick Notes and Web Clips folders, the
// Daily notes folder and each day's note) are one per person, not one for everyone. SystemKey is
// unique, so a person's copy carries their name: "quick-notes:ann". A page made under the old
// shared key by this same person is adopted, so nobody loses their notes; one someone else made
// stays theirs, and this person gets their own.
public static class SentinelPersonalPages
{
    public static string KeyFor(string baseKey, string username) => $"{baseKey}:{username.Trim().ToLowerInvariant()}";

    public static async Task<WikiPage?> FindAsync(IAppDbContext db, string baseKey, string username, CancellationToken cancellationToken)
    {
        var key = KeyFor(baseKey, username);
        var page = await db.WikiPages.FirstOrDefaultAsync(item => item.SystemKey == key, cancellationToken);
        if (page is not null) return page;
        var legacy = await db.WikiPages.FirstOrDefaultAsync(item => item.SystemKey == baseKey, cancellationToken);
        if (legacy is null || !string.Equals(legacy.CreatedBy, username, StringComparison.OrdinalIgnoreCase)) return null;
        legacy.SystemKey = key;
        await db.SaveChangesAsync(cancellationToken);
        return legacy;
    }

    // Writes someone's app-made page from Markdown (20-11-26: the morning briefing, watchlist
    // dossiers, the weekly civic page): made the first time, replaced in place after, through the
    // wiki's own save so revisions and every save rule apply.
    public static async Task<Guid> WriteAsync(IAppDbContext db, IWikiService wiki, string baseKey, string username, string title, string icon,
        string markdown, CancellationToken cancellationToken)
    {
        var blocks = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(markdown));
        var page = await FindAsync(db, baseKey, username, cancellationToken);
        if (page is null)
        {
            var created = await wiki.SavePageAsync(new WikiPageEditorModel { Title = title, Icon = icon, BlocksJson = blocks }, username, cancellationToken: cancellationToken);
            var stored = await db.WikiPages.FirstAsync(p => p.Id == created.Id, cancellationToken);
            stored.SystemKey = KeyFor(baseKey, username);
            await db.SaveChangesAsync(cancellationToken);
            return stored.Id;
        }
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id,
            Title = page.Title,
            Slug = page.Slug,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            ParentWikiPageId = page.ParentWikiPageId,
            IsFullWidth = page.IsFullWidth,
            FontStyle = page.FontStyle,
            ExpectedContentVersion = page.ContentVersion,
            BaseBlocksJson = page.BlocksJson,
            BlocksJson = blocks
        }, username, cancellationToken: cancellationToken);
        return page.Id;
    }
}
