using System.Net;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Board notifications (Boards K9b). A sweep every few minutes compares each board with what's
// already been said: a person newly assigned to a card (not by themselves), and the assignees of
// an open card due tomorrow or overdue. Each is said once (BoardNotification's unique key), and
// unassigning someone clears their "assigned" note so a later reassignment says it again.
// Every call opens its own context (the factory): the bell calls it from a layout island, which
// shares the circuit's scoped context with whatever page is open.
public sealed class BoardNotificationService(
    IAppDbContextFactory dbFactory,
    IKanbanBoardService boards,
    BoardNotificationNotifier notifier,
    TimeProvider timeProvider,
    IMailTransport? mailTransport = null,
    IOptions<GrowthReportEmailOptions>? mailOptions = null,
    IOptions<SupportNotificationOptions>? links = null,
    ILogger<BoardNotificationService>? logger = null) : IBoardNotificationService
{
    private const int ListDays = 30;
    private const int DigestHour = 7;

    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var zone = timeProvider.LocalTimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var users = (await db.AppUsers.AsNoTracking().Where(user => user.IsActive).Select(user => user.Username).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var said = await db.BoardNotifications.ToListAsync(cancellationToken);
        // Due-date notes go after 90 days (a card still overdue then says so once more);
        // "assigned" ones stay while the person is assigned, so it isn't said twice.
        foreach (var old in said.Where(note => note.Kind != BoardNotificationKinds.Assigned && note.CreatedAt < now.AddDays(-90)).ToList())
        {
            db.BoardNotifications.Remove(old);
            said.Remove(old);
        }
        var known = said.Select(note => (note.Username.ToLowerInvariant(), note.RowId, note.Kind, note.Key)).ToHashSet();
        var told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var made = 0;

        foreach (var summary in await boards.ListBoardsAsync(cancellationToken))
        {
            if (await boards.GetBoardAsync(summary.DatabaseId, cancellationToken) is not { } board) continue;
            var settings = board.Settings ?? KanbanSettings.Default;
            // The first look at a board records what's already there without telling anyone.
            var quiet = settings.NotificationsSince is null;
            var editors = (await db.WikiDatabaseRows.AsNoTracking()
                    .Where(row => row.WikiDatabaseId == board.DatabaseId)
                    .Select(row => new { row.Id, Editor = row.UpdatedBy ?? row.CreatedBy })
                    .ToListAsync(cancellationToken))
                .ToDictionary(row => row.Id, row => row.Editor);

            foreach (var column in board.Columns)
            foreach (var card in column.Cards)
            {
                var assignees = card.Assignees.Where(users.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var name = card.Key is null ? $"\"{card.Title}\"" : $"{card.Key} \"{card.Title}\"";

                foreach (var person in assignees)
                {
                    // Assigning yourself is no news.
                    var self = string.Equals(editors.GetValueOrDefault(card.RowId), person, StringComparison.OrdinalIgnoreCase);
                    if (Add(person, card.RowId, BoardNotificationKinds.Assigned, BoardNotificationKinds.Assigned, $"{name} was assigned to you.", quiet || self))
                        made++;
                }
                foreach (var stale in said.Where(note => note.RowId == card.RowId && note.Kind == BoardNotificationKinds.Assigned && !assignees.Contains(note.Username)).ToList())
                {
                    db.BoardNotifications.Remove(stale);
                    said.Remove(stale);
                    known.Remove((stale.Username.ToLowerInvariant(), stale.RowId, stale.Kind, stale.Key));
                }

                if (card.Due is not { } due || column.Group == WikiDatabaseStatusGroups.Complete) continue;
                var dueDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, zone).DateTime);
                var (kind, text) = dueDay == today.AddDays(1) ? (BoardNotificationKinds.DueTomorrow, $"{name} is due tomorrow.")
                    : dueDay < today ? (BoardNotificationKinds.Overdue, $"{name} has been due since {dueDay:dd-MM-yy}.")
                    : (null, null);
                if (kind is null) continue;
                foreach (var person in assignees)
                {
                    if (Add(person, card.RowId, kind, dueDay.ToString("yyyy-MM-dd"), text!, quiet)) made++;
                }

                bool Add(string person, Guid rowId, string noteKind, string key, string message, bool silent)
                {
                    if (!known.Add((person.ToLowerInvariant(), rowId, noteKind, key))) return false;
                    var note = new BoardNotification
                    {
                        Username = person, WikiDatabaseId = board.DatabaseId, RowId = rowId, Kind = noteKind, Key = key,
                        Message = $"{board.Title}: {message}", CreatedAt = now, CreatedBy = "board-notifications",
                        // Recorded but not shown: the first look at a board, or your own assignment.
                        ReadAt = silent ? now : null
                    };
                    db.BoardNotifications.Add(note);
                    said.Add(note);
                    if (silent) return false;
                    told.Add(person);
                    return true;
                }
            }

            if (quiet)
                await boards.SaveSettingsAsync(board.DatabaseId, settings with { NotificationsSince = now.ToUnixTimeSeconds() }, "board-notifications", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var person in told) notifier.Notify(person);
        return made;
    }

    public async Task<IReadOnlyList<BoardNotificationView>> ListAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var since = timeProvider.GetUtcNow().AddDays(-ListDays);
        var notes = await db.BoardNotifications.AsNoTracking().Where(note => note.Username == username).ToListAsync(cancellationToken);
        var titles = await db.WikiDatabases.AsNoTracking().Select(database => new { database.Id, database.Title }).ToDictionaryAsync(d => d.Id, d => d.Title, cancellationToken);
        // SQLite can't compare DateTimeOffset, so the window and order are applied here. Quietly
        // recorded ones (read the moment they were made) aren't news.
        return notes
            .Where(note => note.CreatedAt >= since && note.ReadAt != note.CreatedAt)
            .OrderByDescending(note => note.CreatedAt)
            .Take(50)
            .Select(note => new BoardNotificationView(note.Id, note.WikiDatabaseId, note.RowId, note.Kind,
                note.Kind switch
                {
                    BoardNotificationKinds.Assigned => "Assigned to you",
                    BoardNotificationKinds.DueTomorrow => "Due tomorrow",
                    _ => "Overdue"
                } + (titles.TryGetValue(note.WikiDatabaseId, out var title) ? $" · {title}" : string.Empty),
                note.Message, note.CreatedAt, note.ReadAt is not null))
            .ToList();
    }

    public async Task MarkReadAsync(string username, Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var note = await db.BoardNotifications.FirstOrDefaultAsync(item => item.Id == id && item.Username == username, cancellationToken);
        if (note is null || note.ReadAt is not null) return;
        note.ReadAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var note in await db.BoardNotifications.Where(item => item.Username == username && item.ReadAt == null).ToListAsync(cancellationToken))
            note.ReadAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> SendDigestsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (mailTransport is null || mailOptions is null || !mailTransport.Describe(mailOptions.Value).CanSend) return 0;
        var local = TimeZoneInfo.ConvertTime(now, timeProvider.LocalTimeZone);
        if (local.Hour < DigestHour) return 0;
        // Everything made before this morning's 07:00 and not yet emailed: one email a day.
        var cutoff = new DateTimeOffset(local.Date.AddHours(DigestHour), local.Offset);
        var profiles = await db.MemberProfiles.AsNoTracking().Where(profile => profile.EmailBoardDigest).ToListAsync(cancellationToken);
        var usernames = await db.AppUsers.AsNoTracking().Where(user => user.IsActive).ToDictionaryAsync(user => user.Id, user => user.Username, cancellationToken);
        var wanting = profiles
            .Where(profile => usernames.ContainsKey(profile.AppUserId) && MailboxAddress.TryParse(profile.Email, out _))
            .ToDictionary(profile => usernames[profile.AppUserId], profile => profile.Email, StringComparer.OrdinalIgnoreCase);
        if (wanting.Count == 0) return 0;

        var pending = (await db.BoardNotifications.Where(note => note.ReadAt == null && note.EmailedAt == null).ToListAsync(cancellationToken))
            .Where(note => note.CreatedAt < cutoff && wanting.ContainsKey(note.Username))
            .GroupBy(note => note.Username, StringComparer.OrdinalIgnoreCase);
        var baseUrl = (links?.Value.AdminBaseUrl ?? string.Empty).TrimEnd('/');
        var sent = 0;
        foreach (var group in pending)
        {
            var notes = group.OrderBy(note => note.CreatedAt).ToList();
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GWS Business Suite", mailOptions.Value.FromAddress));
            message.To.Add(MailboxAddress.Parse(wanting[group.Key]));
            message.Subject = $"{notes.Count} board {(notes.Count == 1 ? "update" : "updates")} for you";
            string Link(BoardNotification note) => $"{baseUrl}/admin/boards/{note.WikiDatabaseId}?card={note.RowId}";
            message.Body = new BodyBuilder
            {
                TextBody = string.Join("\n", notes.Select(note => $"- {note.Message} {Link(note)}"))
                    + "\n\nTurn these emails off on your Community profile.",
                HtmlBody = "<ul>" + string.Concat(notes.Select(note =>
                        $"<li><a href=\"{WebUtility.HtmlEncode(Link(note))}\">{WebUtility.HtmlEncode(note.Message)}</a></li>"))
                    + "</ul><p style=\"color:#666\">Turn these emails off on your Community profile.</p>"
            }.ToMessageBody();
            try
            {
                await mailTransport.SendAsync(message, mailOptions.Value, "board-digest", cancellationToken);
                foreach (var note in notes) note.EmailedAt = now;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Board digest to {User} failed.", group.Key);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return sent;
    }
}

// Board notifications every five minutes, starting a minute after start-up.
public sealed class BoardNotificationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<BoardNotificationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<IBoardNotificationService>();
                var now = DateTimeOffset.UtcNow;
                await notifications.SweepAsync(now, stoppingToken);
                await notifications.SendDigestsAsync(now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Board notification sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
