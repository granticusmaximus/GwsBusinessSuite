using GwsBusinessSuite.Domain.Common;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GwsBusinessSuite.Infrastructure.Data;

// Community activity feed entries for moments that can happen from several screens at once, so
// one save hook beats instrumenting each caller: an article or page going live, a support ticket
// opened or resolved, a booking made. (Wiki saves and new CRM contacts still record explicitly in
// WikiService/CrmService.) Added to the same SaveChanges, so the feed can't drift from the data.
public sealed class ActivityFeedSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) Record(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) Record(context);
        return ValueTask.FromResult(result);
    }

    internal static void Record(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var events = new List<ActivityEvent>();
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
        {
            switch (entry.Entity)
            {
                case Article article when BecameValue(entry, nameof(Article.Status), ArticleStatuses.Published):
                    events.Add(Event(article, "published the article", article.Title, $"/admin/article-editor/{article.Id}"));
                    break;
                case CmsPage page when page.Region is null && BecameValue(entry, nameof(CmsPage.Status), CmsPageStatuses.Published):
                    events.Add(Event(page, "published the page", page.Title, $"/admin/pages/{page.Id}/edit"));
                    break;
                case SupportTicket ticket when entry.State == EntityState.Added:
                    events.Add(Event(ticket, "opened the support ticket", ticket.Subject, "/admin/support"));
                    break;
                case SupportTicket ticket when BecameValue(entry, nameof(SupportTicket.Status), SupportTicketStatuses.Resolved):
                    events.Add(Event(ticket, "resolved the support ticket", ticket.Subject, "/admin/support"));
                    break;
                case Booking booking when entry.State == EntityState.Added:
                    events.Add(Event(booking, "received a booking from", booking.AttendeeName, "/admin/scheduling"));
                    break;
            }
        }

        if (events.Count > 0) context.AddRange(events);
    }

    // True when the property is now `value` and wasn't before (or the row is new with it).
    private static bool BecameValue(EntityEntry entry, string property, string value)
    {
        var current = entry.Property(property).CurrentValue as string;
        if (!string.Equals(current, value, StringComparison.Ordinal)) return false;
        return entry.State == EntityState.Added
            || !string.Equals(entry.Property(property).OriginalValue as string, value, StringComparison.Ordinal);
    }

    private static ActivityEvent Event(AuditableEntity entity, string verb, string label, string url) => new()
    {
        Verb = verb,
        TargetLabel = label,
        TargetUrl = url,
        CreatedBy = string.IsNullOrWhiteSpace(entity.UpdatedBy) ? (string.IsNullOrWhiteSpace(entity.CreatedBy) ? "system" : entity.CreatedBy) : entity.UpdatedBy
    };
}
