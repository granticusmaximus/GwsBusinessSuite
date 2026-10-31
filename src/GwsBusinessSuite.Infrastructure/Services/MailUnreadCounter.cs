using System.Collections.Concurrent;
using GwsBusinessSuite.Application.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// The unread count beside Mail in the menu (31-10-26). The menu renders with every page and must
// never wait on a mail server, so Peek returns the last count known and, when that's more than
// two minutes old, asks the mailbox again in the background (in its own scope, so it never shares
// the page's DbContext). The new number shows on the next page.
public sealed class MailUnreadCounter
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, Task<int?>> _fetch;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    private sealed record Entry(int? Count, DateTimeOffset CheckedAt, bool Refreshing);

    public MailUnreadCounter(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<MailUnreadCounter> logger)
        : this(async (username, cancellationToken) =>
        {
            using var scope = scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IMailService>().GetUnreadCountAsync(username, cancellationToken);
        }, time, logger)
    {
    }

    private MailUnreadCounter(Func<string, CancellationToken, Task<int?>> fetch, TimeProvider time, ILogger? logger)
    {
        _fetch = fetch;
        _time = time;
        _logger = logger;
    }

    public static MailUnreadCounter Create(Func<string, CancellationToken, Task<int?>> fetch, TimeProvider time) => new(fetch, time, null);

    // The last known count (null: not known yet, or no mailbox). Never waits.
    public int? Peek(string username)
    {
        if (string.IsNullOrEmpty(username)) return null;
        var now = _time.GetUtcNow();
        var entry = _entries.GetOrAdd(username, _ => new Entry(null, DateTimeOffset.MinValue, false));
        if (!entry.Refreshing && now - entry.CheckedAt >= FreshFor && _entries.TryUpdate(username, entry with { Refreshing = true }, entry))
            _ = RefreshAsync(username);
        return entry.Count;
    }

    // Mail just changed what's unread (opened, marked unread): ask again on the next page.
    public void Invalidate(string username)
    {
        if (_entries.TryGetValue(username, out var entry) && !entry.Refreshing)
            _entries.TryUpdate(username, entry with { CheckedAt = DateTimeOffset.MinValue }, entry);
    }

    public Task RefreshAsync(string username) => RefreshCoreAsync(username);

    private async Task RefreshCoreAsync(string username)
    {
        int? count = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            count = await _fetch(username, timeout.Token);
        }
        catch (Exception ex)
        {
            // Keep showing the last count; the page itself says what's wrong with the mailbox.
            _logger?.LogDebug(ex, "Couldn't count unread mail for {Username}.", username);
            count = _entries.TryGetValue(username, out var previous) ? previous.Count : null;
        }
        _entries[username] = new Entry(count, _time.GetUtcNow(), false);
    }
}
