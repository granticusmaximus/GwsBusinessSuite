using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentCalendar;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Everything scheduled to go out, from every module that schedules: posts and pages (a future
// PublishedAt is how both are scheduled), social posts (ScheduledFor), article-alert emails
// (ArticleAnnouncement.DueAt), drip emails (each active enrollment's NextSendAt, grouped per
// campaign per day) and past live shows. Where a module has a Unix-seconds column the range is
// filtered in SQL; the rest are small tables filtered in memory (SQLite can't compare a
// DateTimeOffset column).
public sealed class ContentCalendarService(IAppDbContextFactory dbContextFactory, TimeProvider timeProvider) : IContentCalendarService
{
    public async Task<IReadOnlyList<ContentCalendarItem>> GetItemsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var from = fromUtc.ToUnixTimeSeconds();
        var to = toUtc.ToUnixTimeSeconds();
        bool InRange(DateTimeOffset at) => at >= fromUtc && at < toUtc;
        var items = new List<ContentCalendarItem>();

        var articles = await db.Articles.AsNoTracking()
            .Where(a => a.TrashedAt == null && a.Status == ArticleStatuses.Published
                        && a.PublishedAtUnixSeconds != null && a.PublishedAtUnixSeconds >= from && a.PublishedAtUnixSeconds < to)
            .Select(a => new { a.Id, a.Title, a.PublishedAt })
            .ToListAsync(cancellationToken);
        foreach (var article in articles)
        {
            var upcoming = article.PublishedAt > now;
            items.Add(new(ContentCalendarKinds.Post, article.Id, article.Title, article.PublishedAt!.Value,
                upcoming ? "Scheduled" : "Published", $"/admin/article-editor/{article.Id}", upcoming, upcoming));
        }

        var pages = await db.CmsPages.AsNoTracking()
            .Where(p => p.TrashedAt == null && p.Region == null && p.Status == CmsPageStatuses.Published && p.PublishedAt != null)
            .Select(p => new { p.Id, p.Title, p.PublishedAt })
            .ToListAsync(cancellationToken);
        foreach (var page in pages.Where(p => InRange(p.PublishedAt!.Value)))
        {
            var upcoming = page.PublishedAt > now;
            items.Add(new(ContentCalendarKinds.Page, page.Id, page.Title, page.PublishedAt!.Value,
                upcoming ? "Scheduled" : "Published", $"/admin/pages/{page.Id}/edit", upcoming, upcoming));
        }

        var socialPosts = await db.SocialPosts.AsNoTracking()
            .Where(s => s.ScheduledFor != null || s.PublishedAt != null)
            .Select(s => new { s.Id, s.Title, s.Status, s.ScheduledFor, s.PublishedAt })
            .ToListAsync(cancellationToken);
        foreach (var post in socialPosts)
        {
            var at = post.PublishedAt ?? post.ScheduledFor!.Value;
            if (!InRange(at)) continue;
            var scheduled = post.Status == SocialPostStatuses.Scheduled && post.PublishedAt is null;
            items.Add(new(ContentCalendarKinds.Social, post.Id, post.Title, at, post.Status, "/admin/growth",
                at > now, scheduled && at > now));
        }

        var announcements = await db.ArticleAnnouncements.AsNoTracking()
            .Where(a => a.DueAtUnixSeconds >= from && a.DueAtUnixSeconds < to)
            .Select(a => new { a.Id, a.CampaignId, a.ArticleTitle, a.Status, a.DueAt, a.CompletedAt, a.DeliveredCount })
            .ToListAsync(cancellationToken);
        foreach (var announcement in announcements)
        {
            var at = announcement.CompletedAt ?? announcement.DueAt;
            items.Add(new(ContentCalendarKinds.AlertEmail, announcement.Id, $"Alert: {announcement.ArticleTitle}", at,
                announcement.Status == ArticleAnnouncementStatuses.Sent ? $"Sent to {announcement.DeliveredCount}" : announcement.Status,
                $"/admin/email-campaigns/alerts/{announcement.CampaignId}", at > now, false));
        }

        // Drip sends: only what's still due (past sends are in each contact's history). One item
        // per campaign per day, so a large list doesn't fill a day with identical rows.
        var activeCampaigns = await db.EmailCampaigns.AsNoTracking()
            .Where(c => c.Status == EmailCampaignStatuses.Active && c.Kind == EmailCampaignKinds.Sequence)
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        if (activeCampaigns.Count > 0)
        {
            var campaignIds = activeCampaigns.Keys.ToList();
            var dueSends = (await db.EmailCampaignEnrollments.AsNoTracking()
                    .Where(e => campaignIds.Contains(e.CampaignId) && e.Status == EmailCampaignEnrollmentStatuses.Active && e.NextSendAt != null)
                    .Select(e => new { e.CampaignId, e.NextSendAt })
                    .ToListAsync(cancellationToken))
                .Where(e => InRange(e.NextSendAt!.Value) && e.NextSendAt > now);
            foreach (var group in dueSends.GroupBy(e => (e.CampaignId, Day: e.NextSendAt!.Value.ToLocalTime().Date)))
            {
                var first = group.Min(e => e.NextSendAt!.Value);
                var count = group.Count();
                items.Add(new(ContentCalendarKinds.Drip, group.Key.CampaignId,
                    $"{activeCampaigns[group.Key.CampaignId]}: {count} email{(count == 1 ? "" : "s")}", first, "Due", "/admin/email-campaigns", true, false));
            }
        }

        var shows = await db.LiveShowSessions.AsNoTracking().Select(s => new { s.Id, s.Title, s.StartedAt, s.Status }).ToListAsync(cancellationToken);
        foreach (var show in shows.Where(s => InRange(s.StartedAt)))
        {
            items.Add(new(ContentCalendarKinds.LiveShow, show.Id, show.Title, show.StartedAt, show.Status, "/admin/live-show", false, false));
        }

        return items.OrderBy(i => i.At).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task RescheduleAsync(string kind, Guid id, DateTimeOffset newTimeUtc, string actor, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (newTimeUtc <= now) throw new InvalidOperationException("Pick a time in the future.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        switch (kind)
        {
            case ContentCalendarKinds.Post:
            {
                var article = await db.Articles.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                              ?? throw new InvalidOperationException("That post no longer exists.");
                if (article.Status != ArticleStatuses.Published || article.PublishedAt is not { } at || at <= now)
                    throw new InvalidOperationException("Only a scheduled post that hasn't gone live can be moved.");
                // PublishedAtUnixSeconds follows automatically (ApplicationDbContext.SaveChanges).
                article.PublishedAt = newTimeUtc;
                article.UpdatedAt = now;
                article.UpdatedBy = actor;
                break;
            }
            case ContentCalendarKinds.Page:
            {
                var page = await db.CmsPages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                           ?? throw new InvalidOperationException("That page no longer exists.");
                if (page.Status != CmsPageStatuses.Published || page.PublishedAt is not { } at || at <= now)
                    throw new InvalidOperationException("Only a scheduled page that hasn't gone live can be moved.");
                // ScheduledPublishTriggerPending stays set, so the scheduled-publish sweep fires the
                // page's "published" automations at the new time instead.
                page.PublishedAt = newTimeUtc;
                page.UpdatedAt = now;
                page.UpdatedBy = actor;
                break;
            }
            case ContentCalendarKinds.Social:
            {
                var post = await db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                           ?? throw new InvalidOperationException("That social post no longer exists.");
                if (post.Status != SocialPostStatuses.Scheduled || post.PublishedAt is not null || post.ScheduledFor is not { } at || at <= now)
                    throw new InvalidOperationException("Only a scheduled social post that hasn't gone out can be moved.");
                post.ScheduledFor = newTimeUtc;
                post.UpdatedAt = now;
                post.UpdatedBy = actor;
                break;
            }
            default:
                throw new InvalidOperationException("Only posts, pages and social posts can be moved from the calendar.");
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
