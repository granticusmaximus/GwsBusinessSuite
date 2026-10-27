using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// A DbContext per call from the factory: the bell is a layout island on the page's circuit, and
// the circuit's shared IAppDbContext can't run two operations at once.
public sealed class NotificationInboxStateService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time) : INotificationInboxStateService
{
    // States for notifications this old are pruned on the next write; their sources keep only
    // recent alerts anyway.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<InboxItemState>> ListAsync(string username, CancellationToken ct = default)
    {
        if (!IsPerson(username)) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserNotificationStates.AsNoTracking()
            .Where(state => state.Username == username)
            .Select(state => new InboxItemState(state.Kind, state.ItemId, state.DoneAtUnix, state.SnoozedAtUnix, state.SnoozedUntilUnix))
            .ToListAsync(ct);
    }

    public Task MarkDoneAsync(string username, string kind, Guid itemId, CancellationToken ct = default) =>
        UpdateAsync(username, kind, itemId, (state, now) =>
        {
            state.DoneAtUnix = now;
            state.SnoozedAtUnix = null;
            state.SnoozedUntilUnix = null;
        }, ct);

    public Task UndoDoneAsync(string username, string kind, Guid itemId, CancellationToken ct = default) =>
        UpdateAsync(username, kind, itemId, (state, _) => state.DoneAtUnix = null, ct);

    public Task SnoozeAsync(string username, string kind, Guid itemId, DateTimeOffset until, CancellationToken ct = default)
    {
        if (until <= time.GetUtcNow()) throw new ArgumentException("Snooze until a time in the future.");
        return UpdateAsync(username, kind, itemId, (state, now) =>
        {
            state.SnoozedAtUnix = now;
            state.SnoozedUntilUnix = until.ToUnixTimeMilliseconds();
        }, ct);
    }

    public Task UnsnoozeAsync(string username, string kind, Guid itemId, CancellationToken ct = default) =>
        UpdateAsync(username, kind, itemId, (state, _) =>
        {
            state.SnoozedAtUnix = null;
            state.SnoozedUntilUnix = null;
        }, ct);

    private async Task UpdateAsync(string username, string kind, Guid itemId, Action<UserNotificationState, long> change, CancellationToken ct)
    {
        if (!IsPerson(username)) throw new ArgumentException("Sign in to manage notifications.");
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 40) throw new ArgumentException("Unknown notification.");
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.UserNotificationStates.SingleOrDefaultAsync(
            item => item.Username == username && item.Kind == kind && item.ItemId == itemId, ct);
        if (state is null)
        {
            state = new UserNotificationState { Username = username, Kind = kind, ItemId = itemId, CreatedBy = username, CreatedAt = now };
            db.UserNotificationStates.Add(state);
        }
        else
        {
            state.UpdatedAt = now;
            state.UpdatedBy = username;
        }
        change(state, now.ToUnixTimeMilliseconds());

        var cutoff = (now - KeepFor).ToUnixTimeMilliseconds();
        var stale = await db.UserNotificationStates
            .Where(item => item.Username == username && item.Id != state.Id
                && (item.DoneAtUnix ?? item.SnoozedUntilUnix ?? 0) < cutoff)
            .ToListAsync(ct);
        db.UserNotificationStates.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }

    private static bool IsPerson(string username) => !string.IsNullOrWhiteSpace(username) && username != "unknown";
}
