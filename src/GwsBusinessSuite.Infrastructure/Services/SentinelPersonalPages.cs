using GwsBusinessSuite.Application.Abstractions;
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
}
