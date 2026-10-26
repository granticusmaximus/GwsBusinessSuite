using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// A short-lived context per call: a page's header and the rail (RailShortcuts) use this on the
// same circuit at the same moment, and the circuit's shared IAppDbContext can't run two at once.
public sealed class UserShortcutService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time) : IUserShortcutService
{
    public async Task<UserShortcutsView> ListAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.UserShortcuts.AsNoTracking().Where(s => s.Username == username).ToListAsync(ct);
        return new UserShortcutsView(
            all.Where(s => s.Kind == UserShortcutKinds.Pin).OrderBy(s => s.LastUsedUnix).Select(ToView).ToList(),
            all.Where(s => s.Kind == UserShortcutKinds.Recent).OrderByDescending(s => s.LastUsedUnix)
                .Take(UserShortcutRules.MaxRecents).Select(ToView).ToList());
    }

    public async Task<bool> IsPinnedAsync(string username, string url, CancellationToken ct = default)
    {
        if (UserShortcutRules.NormalizeUrl(url) is not { } normalized) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserShortcuts.AnyAsync(s => s.Username == username && s.Kind == UserShortcutKinds.Pin && s.Url == normalized, ct);
    }

    public async Task<bool> TogglePinAsync(string username, string url, string title, string icon, CancellationToken ct = default)
    {
        var normalized = UserShortcutRules.NormalizeUrl(url) ?? throw new UserShortcutException("This page can't be pinned.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pins = await db.UserShortcuts.Where(s => s.Username == username && s.Kind == UserShortcutKinds.Pin).ToListAsync(ct);
        if (pins.FirstOrDefault(p => p.Url == normalized) is { } existing)
        {
            db.UserShortcuts.Remove(existing);
            await db.SaveChangesAsync(ct);
            return false;
        }
        if (pins.Count >= UserShortcutRules.MaxPins)
            throw new UserShortcutException($"You can pin up to {UserShortcutRules.MaxPins} pages. Unpin one under Recent on the rail first.");
        db.UserShortcuts.Add(Create(username, UserShortcutKinds.Pin, normalized, title, icon));
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Moves the record to the top of the recents, keeping a few more than are shown.
    public async Task RecordVisitAsync(string username, string url, string title, string icon, CancellationToken ct = default)
    {
        if (UserShortcutRules.NormalizeUrl(url) is not { } normalized || string.IsNullOrWhiteSpace(title)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recents = await db.UserShortcuts.Where(s => s.Username == username && s.Kind == UserShortcutKinds.Recent).ToListAsync(ct);
        if (recents.FirstOrDefault(r => r.Url == normalized) is { } existing)
        {
            existing.Title = Truncate(title.Trim(), 200);
            existing.Icon = Truncate(icon ?? string.Empty, 60);
            existing.LastUsedUnix = time.GetUtcNow().ToUnixTimeMilliseconds();
            existing.UpdatedAt = time.GetUtcNow();
        }
        else
        {
            db.UserShortcuts.Add(Create(username, UserShortcutKinds.Recent, normalized, title, icon));
            foreach (var old in recents.OrderByDescending(r => r.LastUsedUnix).Skip(UserShortcutRules.MaxRecents * 2 - 1)) db.UserShortcuts.Remove(old);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same record opened twice at once (two tabs): the other visit was recorded.
        }
    }

    public async Task RemoveAsync(string username, string kind, string url, CancellationToken ct = default)
    {
        if (UserShortcutRules.NormalizeUrl(url) is not { } normalized) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var found = await db.UserShortcuts.Where(s => s.Username == username && s.Kind == kind && s.Url == normalized).ToListAsync(ct);
        if (found.Count == 0) return;
        db.UserShortcuts.RemoveRange(found);
        await db.SaveChangesAsync(ct);
    }

    private UserShortcut Create(string username, string kind, string url, string title, string icon) => new()
    {
        Username = username,
        Kind = kind,
        Url = url,
        Title = Truncate(string.IsNullOrWhiteSpace(title) ? url : title.Trim(), 200),
        Icon = Truncate(icon ?? string.Empty, 60),
        LastUsedUnix = time.GetUtcNow().ToUnixTimeMilliseconds(),
        CreatedBy = username
    };

    private static UserShortcutView ToView(UserShortcut shortcut) => new(shortcut.Kind, shortcut.Url, shortcut.Title, shortcut.Icon);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
