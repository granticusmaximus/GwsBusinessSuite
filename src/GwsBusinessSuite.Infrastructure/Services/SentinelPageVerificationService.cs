using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelPageVerificationService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null) : ISentinelPageVerificationService
{
    public async Task<SentinelPageVerificationView?> GetAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        return page is null ? null : ToView(page);
    }

    public async Task<SentinelPageVerificationView> VerifyAsync(Guid pageId, int days, string username, CancellationToken cancellationToken = default)
    {
        if (!SentinelPageVerificationRules.PeriodDays.Contains(days))
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Choose one of the offered verification periods.");
        }

        var page = await LoadForEditAsync(pageId, username, cancellationToken);
        var now = timeProvider.GetUtcNow();
        page.VerifiedBy = Normalize(username);
        page.VerifiedAtUnix = now.ToUnixTimeSeconds();
        page.VerifiedUntilUnix = now.AddDays(days).ToUnixTimeSeconds();
        page.VerificationLapseNotifiedUnix = null;
        // Verifying makes you answerable for the page when nobody else is.
        page.OwnerUsername ??= page.VerifiedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(page);
    }

    public async Task<SentinelPageVerificationView> UnverifyAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        var page = await LoadForEditAsync(pageId, username, cancellationToken);
        page.VerifiedBy = null;
        page.VerifiedAtUnix = null;
        page.VerifiedUntilUnix = null;
        page.VerificationLapseNotifiedUnix = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(page);
    }

    public async Task<SentinelPageVerificationView> SetOwnerAsync(Guid pageId, string? ownerUsername, string username, CancellationToken cancellationToken = default)
    {
        var page = await LoadForEditAsync(pageId, username, cancellationToken);
        if (string.IsNullOrWhiteSpace(ownerUsername))
        {
            page.OwnerUsername = null;
        }
        else
        {
            var owner = Normalize(ownerUsername);
            var activeUsers = await dbContext.AppUsers.AsNoTracking()
                .Where(user => user.IsActive)
                .Select(user => user.Username)
                .ToListAsync(cancellationToken);
            if (!activeUsers.Any(user => Normalize(user) == owner))
            {
                throw new InvalidOperationException($"\"{ownerUsername}\" isn't an active user.");
            }
            page.OwnerUsername = owner;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(page);
    }

    public async Task<IReadOnlyList<SentinelNeedsReviewItem>> ListNeedsReviewAsync(
        string username,
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return [];
        var now = timeProvider.GetUtcNow();
        var nowUnix = now.ToUnixTimeSeconds();
        var staleBefore = now.AddDays(-SentinelPageVerificationRules.StaleAfterDays);

        // Only tracked pages (an owner or a verification) are candidates; the edit-date test is
        // done in memory because SQLite can't compare DateTimeOffset.
        var candidates = await dbContext.WikiPages.AsNoTracking()
            .Where(page => page.TrashedAt == null && page.NotionArchivedAt == null && page.SystemKey == null)
            .Where(page => (page.VerifiedUntilUnix != null && page.VerifiedUntilUnix <= nowUnix)
                || (page.VerifiedUntilUnix == null && page.OwnerUsername != null))
            .Select(page => new { page.Id, page.Title, page.OwnerUsername, page.VerifiedUntilUnix, page.CreatedAt, page.UpdatedAt })
            .ToListAsync(cancellationToken);

        var items = candidates
            .Select(page => page.VerifiedUntilUnix is { } until
                ? new SentinelNeedsReviewItem(page.Id, page.Title, SentinelNeedsReviewReasons.Expired, page.OwnerUsername,
                    DateTimeOffset.FromUnixTimeSeconds(until))
                : (page.UpdatedAt ?? page.CreatedAt) < staleBefore
                    ? new SentinelNeedsReviewItem(page.Id, page.Title, SentinelNeedsReviewReasons.NeverVerified, page.OwnerUsername,
                        page.UpdatedAt ?? page.CreatedAt)
                    : null)
            .OfType<SentinelNeedsReviewItem>()
            .ToList();

        if (accessService is not null && items.Count > 0)
        {
            var accessible = await accessService.GetAccessibleTargetsAsync(
                items.Select(item => new SentinelAccessTarget(item.PageId, IsDatabase: false)).ToList(),
                username, SentinelAccessLevels.View, cancellationToken);
            items = items.Where(item => accessible.Contains(new SentinelAccessTarget(item.PageId, IsDatabase: false))).ToList();
        }

        var me = Normalize(username);
        return items
            .OrderBy(item => item.Reason == SentinelNeedsReviewReasons.Expired ? 0 : 1)
            .ThenBy(item => item.OwnerUsername == me ? 0 : 1)
            .ThenBy(item => item.Since)
            .Take(maxResults)
            .ToList();
    }

    public async Task<int> NotifyLapsedAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var nowUnix = now.ToUnixTimeSeconds();
        var lapsed = await dbContext.WikiPages
            .Where(page => page.TrashedAt == null && page.VerifiedUntilUnix != null && page.VerifiedUntilUnix <= nowUnix
                && page.VerificationLapseNotifiedUnix == null)
            .ToListAsync(cancellationToken);
        if (lapsed.Count == 0) return 0;

        var activeUsers = (await dbContext.AppUsers.AsNoTracking()
                .Where(user => user.IsActive)
                .Select(user => user.Username)
                .ToListAsync(cancellationToken))
            .Select(Normalize)
            .ToHashSet();
        var sent = 0;
        foreach (var page in lapsed)
        {
            page.VerificationLapseNotifiedUnix = nowUnix;
            var recipient = page.OwnerUsername ?? page.VerifiedBy;
            if (recipient is null || !activeUsers.Contains(recipient)) continue;

            await dbContext.SentinelNotifications.AddAsync(new SentinelNotification
            {
                Username = recipient,
                Kind = SentinelPageVerificationRules.ExpiredNotificationKind,
                WikiPageId = page.Id,
                Message = $"Verification of \"{page.Title}\" has expired. Check it's still accurate and verify it again.",
                CreatedAt = now,
                CreatedBy = "system"
            }, cancellationToken);
            sent++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return sent;
    }

    // Loads a page for a change, which needs edit access to it.
    private async Task<WikiPage> LoadForEditAsync(Guid pageId, string username, CancellationToken cancellationToken)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The page no longer exists.");
        if (accessService is not null
            && !await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.Edit, cancellationToken))
        {
            throw new UnauthorizedAccessException("You need edit access to change this page's verification.");
        }
        return page;
    }

    private SentinelPageVerificationView ToView(WikiPage page) => new(
        page.Id,
        page.OwnerUsername,
        page.VerifiedBy,
        SentinelPageVerificationRules.FromUnix(page.VerifiedAtUnix),
        SentinelPageVerificationRules.FromUnix(page.VerifiedUntilUnix),
        SentinelPageVerificationRules.State(page.VerifiedUntilUnix, timeProvider.GetUtcNow()));

    // Same rule as the rest of Sentinel's username handling (notifications, My work).
    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}

// Sends "verification expired" bells. Hourly is plenty: verification periods are 30+ days.
public sealed class SentinelPageVerificationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SentinelPageVerificationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ISentinelPageVerificationService>().NotifyLapsedAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Sent {Count} page verification expiry notifications", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Page verification sweep failed");
            }
        }
    }
}
