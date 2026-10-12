using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelMeetingNotesService(IAppDbContext dbContext, TimeProvider timeProvider) : ISentinelMeetingNotesService
{
    public async Task<IReadOnlyList<SentinelMeetingTarget>> ListTargetsAsync(string? query, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var term = (query ?? string.Empty).Trim().ToLower();
        // Dates compared in memory: SQLite can't compare DateTimeOffset.
        var bookings = (await dbContext.Bookings.AsNoTracking()
                .Where(b => b.CancelledAt == null)
                .Where(b => term.Length == 0 || b.AttendeeName.ToLower().Contains(term) || b.AttendeeEmail.ToLower().Contains(term))
                .Select(b => new { b.Id, b.AttendeeName, b.StartsAt }).ToListAsync(cancellationToken))
            .Where(b => b.StartsAt >= now.AddDays(-7) && b.StartsAt <= now.AddDays(7))
            .OrderBy(b => Math.Abs((b.StartsAt - now).TotalMinutes)).Take(20)
            .Select(b => new SentinelMeetingTarget(SentinelMeetingTargetKinds.Booking, b.Id, $"Meeting with {b.AttendeeName}", b.StartsAt.ToLocalTime().ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        var contacts = (await dbContext.Contacts.AsNoTracking()
                .Where(c => c.TrashedAt == null)
                .Where(c => term.Length == 0 || c.FullName.ToLower().Contains(term) || (c.Email ?? "").ToLower().Contains(term) || (c.Company ?? "").ToLower().Contains(term))
                .Select(c => new { c.Id, c.FullName, c.Company, c.Email, c.CreatedAt }).ToListAsync(cancellationToken))
            .OrderByDescending(c => c.CreatedAt).Take(30)
            .Select(c => new SentinelMeetingTarget(SentinelMeetingTargetKinds.Contact, c.Id, c.FullName, string.IsNullOrWhiteSpace(c.Company) ? c.Email ?? string.Empty : c.Company));
        return bookings.Concat(contacts).ToList();
    }

    public async Task LinkAsync(Guid pageId, Guid? contactId, Guid? bookingId, CancellationToken cancellationToken = default)
    {
        if (contactId is null && bookingId is null) return;
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken)
            ?? throw new KeyNotFoundException("The note page no longer exists.");
        if (bookingId is { } bid)
        {
            var booking = await dbContext.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, cancellationToken)
                ?? throw new ArgumentException("That booking no longer exists.");
            page.LinkedBookingId = booking.Id;
            contactId ??= booking.ContactId;
        }
        if (contactId is { } cid)
        {
            if (!await dbContext.Contacts.AnyAsync(c => c.Id == cid && c.TrashedAt == null, cancellationToken)) throw new ArgumentException("That contact no longer exists.");
            page.LinkedContactId = cid;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
