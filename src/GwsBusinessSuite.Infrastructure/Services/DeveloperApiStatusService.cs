using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Counts only, from rows every module already keeps. Anything compared by time is a small set
// (open tickets, upcoming bookings, contacts with a follow-up date) filtered in memory, since
// SQLite can't compare DateTimeOffset columns.
public sealed class DeveloperApiStatusService(IAppDbContextFactory dbContextFactory, IChatService chat, TimeProvider timeProvider)
    : IDeveloperApiStatusService
{
    public async Task<DeveloperApiStatusSummary> GetSummaryAsync(string ownerUsername, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var unreadSeverities = await db.DockerHealthAlerts.AsNoTracking().Where(a => !a.IsRead).Select(a => a.Severity).ToListAsync(cancellationToken);
        var health = unreadSeverities.Count == 0 ? "ok"
            : unreadSeverities.Any(s => s == DockerHealthAlertSeverity.Error) ? "critical"
            : "warning";

        var openTickets = await db.SupportTickets.AsNoTracking()
            .Where(t => !SupportTicketStatuses.Terminal.Contains(t.Status))
            .Select(t => new { t.FirstResponseDueAt, t.ResolutionDueAt, t.Status })
            .ToListAsync(cancellationToken);
        // Overdue = past its resolution target, or still Open (awaiting staff) past its first-response target.
        var overdue = openTickets.Count(t => t.ResolutionDueAt < now || (t.Status == SupportTicketStatuses.Open && t.FirstResponseDueAt < now));

        var followUps = (await db.Contacts.AsNoTracking().Where(c => c.TrashedAt == null && c.FollowUpDate != null)
            .Select(c => c.FollowUpDate).ToListAsync(cancellationToken)).Count(d => d <= now);

        var unreadForms = await db.FormSubmissions.AsNoTracking().CountAsync(f => !f.IsRead, cancellationToken);
        var pendingComments = await db.Comments.AsNoTracking().CountAsync(c => c.Status == CommentStatuses.Pending, cancellationToken);
        var unreadAreaAlerts = await db.OverwatchAreaAlerts.AsNoTracking().CountAsync(a => a.Username == ownerUsername && a.ReadAt == null, cancellationToken);
        var unreadMessages = string.IsNullOrWhiteSpace(ownerUsername) ? 0 : await chat.CountUnreadAsync(ownerUsername, cancellationToken);

        var upcoming = (await db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatuses.Confirmed)
                .Select(b => new { b.BookingTypeId, b.AttendeeName, b.StartsAt }).ToListAsync(cancellationToken))
            .Where(b => b.StartsAt > now)
            .MinBy(b => b.StartsAt);
        DeveloperApiStatusBooking? nextBooking = null;
        if (upcoming is not null)
        {
            var title = await db.BookingTypes.AsNoTracking().Where(t => t.Id == upcoming.BookingTypeId).Select(t => t.Title).FirstOrDefaultAsync(cancellationToken);
            nextBooking = new(title ?? "Meeting", upcoming.AttendeeName, upcoming.StartsAt);
        }

        DeveloperApiStatusTimer? timer = null;
        var running = await db.TimeEntries.AsNoTracking().FirstOrDefaultAsync(t => t.Username == ownerUsername && t.EndedAt == null, cancellationToken);
        if (running is not null)
        {
            var contact = await db.Contacts.AsNoTracking().Where(c => c.Id == running.ContactId).Select(c => c.FullName).FirstOrDefaultAsync(cancellationToken);
            timer = new(contact ?? "(deleted contact)", running.Description, running.StartedAt);
        }

        return new DeveloperApiStatusSummary(health, unreadSeverities.Count, openTickets.Count, overdue, followUps, unreadForms,
            pendingComments, unreadMessages, unreadAreaAlerts, nextBooking, timer, now);
    }
}
