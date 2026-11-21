using System.Collections.Concurrent;
using GwsBusinessSuite.Application.Mail;

namespace GwsBusinessSuite.Infrastructure.Services;

// The inbox as read for sorting into tabs (Mail tabs, 21-11-26), kept for a short while so switching
// tabs doesn't read the mailbox again. Any change to the mailbox from Mail drops it.
public sealed class MailInboxCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<MailInboxItem> Items)> _inboxes = new();

    public bool TryGet(Guid accountId, out IReadOnlyList<MailInboxItem> items)
    {
        if (_inboxes.TryGetValue(accountId, out var entry) && time.GetUtcNow() - entry.At < Lifetime)
        {
            items = entry.Items;
            return true;
        }
        items = [];
        return false;
    }

    public void Set(Guid accountId, IReadOnlyList<MailInboxItem> items) => _inboxes[accountId] = (time.GetUtcNow(), items);

    public void Invalidate(Guid accountId) => _inboxes.TryRemove(accountId, out _);
}
