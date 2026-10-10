using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelReminderService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null) : ISentinelReminderService
{
    public const string NotificationKind = "reminder";
    private const int MaxLabelLength = 120;
    private const int MaxPerSweep = 200;
    // One sweep at a time in this process, so two overlapping ticks can't both send a reminder.
    private static readonly SemaphoreSlim SweepLock = new(1, 1);

    public async Task<SentinelReminderView> CreateAsync(
        Guid wikiPageId,
        Guid wikiBlockId,
        string mentionValue,
        string label,
        string username,
        CancellationToken cancellationToken = default)
    {
        var owner = Normalize(username);
        if (!await IsActiveUserAsync(owner, cancellationToken)) throw new UnauthorizedAccessException("Sign in to set reminders.");

        var page = await dbContext.WikiPages.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wikiPageId && item.TrashedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("The page no longer exists.");
        if (!await CanViewAsync(wikiPageId, owner, cancellationToken))
        {
            throw new UnauthorizedAccessException("You don't have access to this page.");
        }
        if (WikiBlockJson.ParseBlocks(page.BlocksJson).All(block => block.Id != wikiBlockId))
        {
            throw new InvalidOperationException("That line hasn't been saved yet - wait a moment and try again.");
        }

        var zone = timeProvider.LocalTimeZone;
        if (!SentinelDateExpressions.TryReadMentionValue(mentionValue, zone, out var date, out var instant))
        {
            throw new InvalidOperationException("That isn't a date Sentinel recognises.");
        }
        var due = SentinelDateExpressions.ReminderInstant(date, instant, zone);
        if (due <= timeProvider.GetUtcNow()) throw new InvalidOperationException("That time has already passed.");

        var dueUnix = due.ToUnixTimeSeconds();
        var existing = await dbContext.PageReminders.FirstOrDefaultAsync(reminder =>
            reminder.WikiPageId == wikiPageId && reminder.WikiBlockId == wikiBlockId && reminder.OwnerUsername == owner
            && reminder.DueUnix == dueUnix && reminder.SentUnix == null && reminder.CancelledUnix == null, cancellationToken);
        if (existing is not null) return ToView(existing);

        var trimmedLabel = (label ?? string.Empty).Trim();
        var reminder = new PageReminder
        {
            WikiPageId = wikiPageId,
            WikiBlockId = wikiBlockId,
            OwnerUsername = owner,
            Label = trimmedLabel.Length == 0 ? due.ToString("u") : trimmedLabel.Length > MaxLabelLength ? trimmedLabel[..MaxLabelLength] : trimmedLabel,
            DueUnix = dueUnix,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = owner
        };
        await dbContext.PageReminders.AddAsync(reminder, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(reminder);
    }

    public async Task<IReadOnlyList<SentinelReminderView>> ListScheduledForPageAsync(
        Guid wikiPageId, string username, CancellationToken cancellationToken = default)
    {
        var owner = Normalize(username);
        var reminders = await dbContext.PageReminders.AsNoTracking()
            .Where(reminder => reminder.WikiPageId == wikiPageId && reminder.OwnerUsername == owner
                && reminder.SentUnix == null && reminder.CancelledUnix == null)
            .OrderBy(reminder => reminder.DueUnix)
            .ToListAsync(cancellationToken);
        return reminders.Select(ToView).ToList();
    }

    public async Task CancelAsync(Guid reminderId, string username, CancellationToken cancellationToken = default)
    {
        var owner = Normalize(username);
        var reminder = await dbContext.PageReminders.FirstOrDefaultAsync(item => item.Id == reminderId, cancellationToken);
        if (reminder is null || reminder.OwnerUsername != owner || reminder.SentUnix is not null || reminder.CancelledUnix is not null) return;
        reminder.CancelledUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> SendDueAsync(CancellationToken cancellationToken = default)
    {
        await SweepLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var nowUnix = now.ToUnixTimeSeconds();
            var due = await dbContext.PageReminders
                .Where(reminder => reminder.SentUnix == null && reminder.CancelledUnix == null && reminder.DueUnix <= nowUnix)
                .OrderBy(reminder => reminder.DueUnix)
                .Take(MaxPerSweep)
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var pageIds = due.Select(reminder => reminder.WikiPageId).Distinct().ToList();
            var pages = await dbContext.WikiPages.AsNoTracking()
                .Where(page => pageIds.Contains(page.Id) && page.TrashedAt == null)
                .Select(page => new { page.Id, page.Title })
                .ToDictionaryAsync(page => page.Id, page => page.Title, cancellationToken);
            var activeUsers = (await dbContext.AppUsers.AsNoTracking()
                    .Where(user => user.IsActive)
                    .Select(user => user.Username)
                    .ToListAsync(cancellationToken))
                .Select(Normalize)
                .ToHashSet();

            var sent = 0;
            foreach (var reminder in due)
            {
                // Checked now, not when it was set: a page can be deleted or access removed since.
                if (!pages.TryGetValue(reminder.WikiPageId, out var title)
                    || !activeUsers.Contains(reminder.OwnerUsername)
                    || !await CanViewAsync(reminder.WikiPageId, reminder.OwnerUsername, cancellationToken))
                {
                    reminder.CancelledUnix = nowUnix;
                    continue;
                }

                reminder.SentUnix = nowUnix;
                await dbContext.SentinelNotifications.AddAsync(new SentinelNotification
                {
                    Username = reminder.OwnerUsername,
                    Kind = NotificationKind,
                    WikiPageId = reminder.WikiPageId,
                    WikiBlockId = reminder.WikiBlockId,
                    Message = $"Reminder: {reminder.Label} - on \"{(string.IsNullOrWhiteSpace(title) ? "Untitled" : title)}\"",
                    CreatedAt = now,
                    CreatedBy = "system"
                }, cancellationToken);
                sent++;
            }

            // The sent stamps and their notifications are saved together, so a restart can't
            // send a reminder twice or lose one.
            await dbContext.SaveChangesAsync(cancellationToken);
            return sent;
        }
        finally
        {
            SweepLock.Release();
        }
    }

    private async Task<bool> CanViewAsync(Guid wikiPageId, string username, CancellationToken cancellationToken) =>
        accessService is null
        || await accessService.CanAccessAsync(wikiPageId, isDatabase: false, username, SentinelAccessLevels.View, cancellationToken);

    private async Task<bool> IsActiveUserAsync(string username, CancellationToken cancellationToken)
    {
        var usernames = await dbContext.AppUsers.AsNoTracking().Where(user => user.IsActive).Select(user => user.Username).ToListAsync(cancellationToken);
        return usernames.Any(user => Normalize(user) == username);
    }

    private SentinelReminderView ToView(PageReminder reminder) => new(
        reminder.Id,
        reminder.WikiPageId,
        reminder.WikiBlockId,
        reminder.Label,
        DateTimeOffset.FromUnixTimeSeconds(reminder.DueUnix),
        reminder.SentUnix is not null ? SentinelReminderStates.Sent
            : reminder.CancelledUnix is not null ? SentinelReminderStates.Cancelled
            : SentinelReminderStates.Scheduled);

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}

// Checks for due reminders every minute.
public sealed class SentinelReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SentinelReminderBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ISentinelReminderService>().SendDueAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Sent {Count} Sentinel reminders", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sentinel reminder sweep failed");
            }
        }
    }
}
