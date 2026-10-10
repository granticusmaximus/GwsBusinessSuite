namespace GwsBusinessSuite.Application.Wiki;

public static class SentinelReminderStates
{
    public const string Scheduled = "scheduled";
    public const string Sent = "sent";
    public const string Cancelled = "cancelled";
}

public sealed record SentinelReminderView(
    Guid Id,
    Guid WikiPageId,
    Guid WikiBlockId,
    string Label,
    DateTimeOffset DueAt,
    string State);

// Reminders on date mentions: "remind me" on a chip rings the collaboration bell once, at the
// mentioned time (9am local for a whole day), linking back to the page and block.
public interface ISentinelReminderService
{
    // For the signed-in user only (never a client-supplied owner), on a page they can view.
    // mentionValue is the chip's "datemention:" value; the reminder must be in the future.
    Task<SentinelReminderView> CreateAsync(
        Guid wikiPageId, Guid wikiBlockId, string mentionValue, string label, string username,
        CancellationToken cancellationToken = default);

    // The user's own reminders on a page that haven't fired or been cancelled.
    Task<IReadOnlyList<SentinelReminderView>> ListScheduledForPageAsync(Guid wikiPageId, string username, CancellationToken cancellationToken = default);

    // Only the owner can cancel; cancelling a sent or already-cancelled reminder does nothing.
    Task CancelAsync(Guid reminderId, string username, CancellationToken cancellationToken = default);

    // Sends every due reminder once. Returns how many bells were sent.
    Task<int> SendDueAsync(CancellationToken cancellationToken = default);
}
