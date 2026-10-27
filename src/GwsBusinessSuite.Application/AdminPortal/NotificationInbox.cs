namespace GwsBusinessSuite.Application.AdminPortal;

// The bell's inbox (shell redesign R5c). Each source keeps its own notifications and read flags;
// the inbox adds what the person did with them (done, snoozed) and decides what shows.
// ActivityAt is the item's latest activity: an alert's creation time, or a chat thread's last
// message, so a done or snoozed thread comes back when someone writes again.
public sealed record InboxItem(string Kind, Guid Id, string Title, string Message, bool IsRead, DateTimeOffset ActivityAt, string NavigateTo);

public sealed record InboxItemState(string Kind, Guid ItemId, long? DoneAtUnix, long? SnoozedAtUnix, long? SnoozedUntilUnix);

public sealed record InboxEntry(InboxItem Item, bool BackFromSnooze, DateTimeOffset? SnoozedUntil);

// UnreadCount is the bell's badge: unread items that are showing (snoozed ones don't count).
// NextWake is when the earliest snooze ends, so the bell knows when to look again.
public sealed record InboxView(IReadOnlyList<InboxEntry> Visible, IReadOnlyList<InboxEntry> Snoozed, int UnreadCount, DateTimeOffset? NextWake);

public enum SnoozeChoice { OneHour, TomorrowMorning, NextWeek }

public interface INotificationInboxStateService
{
    Task<IReadOnlyList<InboxItemState>> ListAsync(string username, CancellationToken ct = default);
    Task MarkDoneAsync(string username, string kind, Guid itemId, CancellationToken ct = default);
    Task UndoDoneAsync(string username, string kind, Guid itemId, CancellationToken ct = default);
    Task SnoozeAsync(string username, string kind, Guid itemId, DateTimeOffset until, CancellationToken ct = default);
    Task UnsnoozeAsync(string username, string kind, Guid itemId, CancellationToken ct = default);
}

public static class NotificationInboxRules
{
    public const int MaxVisible = 50;
    public const int MorningHour = 9;

    public static InboxView Apply(IEnumerable<InboxItem> items, IEnumerable<InboxItemState> states, DateTimeOffset now)
    {
        var byItem = states.GroupBy(state => (state.Kind, state.ItemId)).ToDictionary(group => group.Key, group => group.First());
        var nowUnix = now.ToUnixTimeMilliseconds();
        var visible = new List<(InboxEntry Entry, long SortKey)>();
        var snoozed = new List<InboxEntry>();
        DateTimeOffset? nextWake = null;

        foreach (var item in items)
        {
            var activity = item.ActivityAt.ToUnixTimeMilliseconds();
            byItem.TryGetValue((item.Kind, item.Id), out var state);
            if (state?.DoneAtUnix is { } doneAt && activity <= doneAt) continue;

            if (state is { SnoozedUntilUnix: { } until } && activity <= (state.SnoozedAtUnix ?? long.MaxValue))
            {
                var untilTime = DateTimeOffset.FromUnixTimeMilliseconds(until);
                if (until > nowUnix)
                {
                    snoozed.Add(new InboxEntry(item, false, untilTime));
                    nextWake = nextWake is { } earliest && earliest <= untilTime ? earliest : untilTime;
                }
                else
                {
                    // Back from snooze: it sorts by when it came back, so it lands at the top.
                    visible.Add((new InboxEntry(item, true, untilTime), until));
                }
                continue;
            }

            visible.Add((new InboxEntry(item, false, null), activity));
        }

        var ordered = visible.OrderByDescending(entry => entry.SortKey).Select(entry => entry.Entry).ToList();
        return new InboxView(
            ordered.Take(MaxVisible).ToList(),
            snoozed.OrderBy(entry => entry.SnoozedUntil).ToList(),
            ordered.Count(entry => !entry.Item.IsRead),
            nextWake);
    }

    // Snooze times are in the zone the app shows its times in (the server's local zone, like every
    // ToLocalTime in the admin): an hour from now, tomorrow at 09:00, or next Monday at 09:00.
    public static DateTimeOffset SnoozeUntil(SnoozeChoice choice, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (choice == SnoozeChoice.OneHour) return now.AddHours(1);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var days = 1;
        if (choice == SnoozeChoice.NextWeek)
        {
            days = ((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7;
            if (days == 0) days = 7;
        }
        var morning = local.Date.AddDays(days).AddHours(MorningHour);
        return new DateTimeOffset(morning, zone.GetUtcOffset(morning));
    }
}
