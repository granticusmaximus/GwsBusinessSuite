using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Scheduling;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class BookingService(
    IAppDbContext db,
    IBookingEmailSender emailSender,
    TimeProvider timeProvider,
    // Optional, resolved by DI in production - see AutomationWorkflowService's own comment on
    // this same pattern for why it's nullable (existing tests new this class up directly).
    ISecurityAuditService? securityAudit = null) : IBookingService
{
    private const int MaxDaysAhead = 60;
    // A visitor can't book a slot starting less than this soon - avoids someone booking a
    // meeting that starts before anyone could plausibly see the confirmation email.
    private static readonly TimeSpan MinLeadTime = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<BookingTypeView>> ListBookingTypesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var query = db.BookingTypes.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(type => type.IsActive);
        }
        var types = await query.ToListAsync(cancellationToken);
        return types.OrderBy(type => type.Title).Select(ToView).ToList();
    }

    public async Task<BookingTypeView?> GetBookingTypeBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var type = await db.BookingTypes.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Slug == slug && item.IsActive, cancellationToken);
        return type is null ? null : ToView(type);
    }

    public async Task<BookingTypeView> SaveBookingTypeAsync(BookingTypeEditorModel editor, string performedBy, CancellationToken cancellationToken = default)
    {
        var title = editor.Title.Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("A title is required.", nameof(editor));
        }
        if (editor.DurationMinutes < 5)
        {
            throw new ArgumentException("Duration must be at least 5 minutes.", nameof(editor));
        }

        var now = timeProvider.GetUtcNow();
        BookingType type;
        if (editor.Id is Guid id)
        {
            type = await db.BookingTypes.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Booking type {id} was not found.");
        }
        else
        {
            type = new BookingType { Title = title, Slug = string.Empty, CreatedAt = now, CreatedBy = performedBy };
            db.BookingTypes.Add(type);
        }

        var requestedSlug = CreateSlug(string.IsNullOrWhiteSpace(editor.Slug) ? title : editor.Slug);
        type.Slug = await GetUniqueSlugAsync(requestedSlug, type.Id, cancellationToken);
        type.Title = title;
        type.Description = editor.Description.Trim();
        type.DurationMinutes = editor.DurationMinutes;
        type.BufferMinutes = Math.Max(0, editor.BufferMinutes);
        type.OwnerUsername = string.IsNullOrWhiteSpace(editor.OwnerUsername) ? null : editor.OwnerUsername.Trim();
        type.IsActive = editor.IsActive;
        type.AvailabilityJson = JsonSerializer.Serialize(editor.Availability, JsonOptions);
        type.UpdatedAt = now;
        type.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        return ToView(type);
    }

    public async Task DeleteBookingTypeAsync(Guid bookingTypeId, CancellationToken cancellationToken = default)
    {
        var type = await db.BookingTypes.FirstOrDefaultAsync(item => item.Id == bookingTypeId, cancellationToken);
        if (type is null) return;

        var bookings = await db.Bookings.Where(booking => booking.BookingTypeId == bookingTypeId).ToListAsync(cancellationToken);
        db.Bookings.RemoveRange(bookings);
        db.BookingTypes.Remove(type);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BookingSlot>> GetAvailableSlotsAsync(
        Guid bookingTypeId, DateTimeOffset fromUtc, int daysAhead, CancellationToken cancellationToken = default)
    {
        var type = await db.BookingTypes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == bookingTypeId, cancellationToken);
        if (type is null || !type.IsActive) return [];

        var availability = ParseAvailability(type.AvailabilityJson);
        if (availability.Count == 0) return [];

        var cappedDays = Math.Clamp(daysAhead, 1, MaxDaysAhead);
        var earliestStart = timeProvider.GetUtcNow().Add(MinLeadTime);
        if (fromUtc > earliestStart) earliestStart = fromUtc;

        var rangeEnd = fromUtc.AddDays(cappedDays);
        // SQLite/EF Core can't translate DateTimeOffset range comparisons - materialize this
        // type's confirmed bookings first, then filter the range client-side.
        var existingBookings = (await db.Bookings.AsNoTracking()
            .Where(booking => booking.BookingTypeId == bookingTypeId && booking.Status == BookingStatuses.Confirmed)
            .Select(booking => new { booking.StartsAt, booking.EndsAt })
            .ToListAsync(cancellationToken))
            .Where(booking => booking.StartsAt < rangeEnd && booking.EndsAt > fromUtc)
            .ToList();

        var slots = new List<BookingSlot>();
        for (var offset = 0; offset < cappedDays; offset++)
        {
            var date = DateOnly.FromDateTime(fromUtc.UtcDateTime.Date.AddDays(offset));
            foreach (var window in availability.Where(w => w.DayOfWeek == date.DayOfWeek))
            {
                var step = TimeSpan.FromMinutes(type.DurationMinutes + type.BufferMinutes);
                var windowStart = new DateTimeOffset(date.ToDateTime(window.Start), TimeSpan.Zero);
                var windowEnd = new DateTimeOffset(date.ToDateTime(window.End), TimeSpan.Zero);
                var slotStart = windowStart;
                while (slotStart.AddMinutes(type.DurationMinutes) <= windowEnd)
                {
                    var slotEnd = slotStart.AddMinutes(type.DurationMinutes);
                    if (slotStart >= earliestStart
                        && !existingBookings.Any(existing => slotStart < existing.EndsAt && slotEnd > existing.StartsAt))
                    {
                        slots.Add(new BookingSlot(slotStart, slotEnd));
                    }
                    slotStart += step;
                }
            }
        }

        return slots.OrderBy(slot => slot.StartsAt).ToList();
    }

    public async Task<BookingView?> CreateBookingAsync(
        Guid bookingTypeId, DateTimeOffset startsAtUtc, string attendeeName, string attendeeEmail, string notes,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = attendeeName.Trim();
        var trimmedEmail = attendeeEmail.Trim();
        if (trimmedName.Length == 0 || trimmedEmail.Length == 0)
        {
            throw new ArgumentException("Name and email are required.");
        }

        var type = await db.BookingTypes.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == bookingTypeId && item.IsActive, cancellationToken);
        if (type is null) return null;

        var endsAtUtc = startsAtUtc.AddMinutes(type.DurationMinutes);
        if (startsAtUtc < timeProvider.GetUtcNow().Add(MinLeadTime)) return null;

        // Re-checked here (not just trusted from the earlier GetAvailableSlotsAsync call the
        // UI made) to close the race between two visitors booking the same slot at once.
        // SQLite/EF Core can't translate DateTimeOffset range comparisons - materialize this
        // type's confirmed bookings first, then check the overlap client-side.
        var confirmedBookings = await db.Bookings.AsNoTracking()
            .Where(booking => booking.BookingTypeId == bookingTypeId && booking.Status == BookingStatuses.Confirmed)
            .Select(booking => new { booking.StartsAt, booking.EndsAt })
            .ToListAsync(cancellationToken);
        var overlaps = confirmedBookings.Any(booking => startsAtUtc < booking.EndsAt && endsAtUtc > booking.StartsAt);
        if (overlaps) return null;

        var contactId = await FindOrCreateContactAsync(trimmedName, trimmedEmail, cancellationToken);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var now = timeProvider.GetUtcNow();
        var booking = new Booking
        {
            BookingTypeId = bookingTypeId,
            ContactId = contactId,
            StartsAt = startsAtUtc,
            EndsAt = endsAtUtc,
            AttendeeName = trimmedName,
            AttendeeEmail = trimmedEmail,
            Notes = notes.Trim(),
            ManageTokenHash = HashToken(token),
            CreatedAt = now,
            CreatedBy = trimmedEmail
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(cancellationToken);

        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "BookingCreated", SecurityAuditOutcomes.Succeeded,
                TargetType: "Booking", TargetId: booking.Id.ToString(),
                Details: new Dictionary<string, string?> { ["bookingTypeId"] = bookingTypeId.ToString() }), cancellationToken);
        }

        var manageUrl = $"/book/manage/{token}";
        await emailSender.SendConfirmationAsync(trimmedEmail, trimmedName, type.Title, startsAtUtc, manageUrl, cancellationToken);

        return ToView(booking, type.Title);
    }

    public async Task<IReadOnlyList<BookingView>> ListBookingsAsync(Guid? bookingTypeId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Bookings.AsNoTracking().AsQueryable();
        if (bookingTypeId is { } id)
        {
            query = query.Where(booking => booking.BookingTypeId == id);
        }
        var bookings = await query.ToListAsync(cancellationToken);

        var typeIds = bookings.Select(booking => booking.BookingTypeId).Distinct().ToList();
        var typeTitles = await db.BookingTypes.AsNoTracking()
            .Where(type => typeIds.Contains(type.Id))
            .ToDictionaryAsync(type => type.Id, type => type.Title, cancellationToken);

        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column - sort client-side.
        return bookings
            .OrderByDescending(booking => booking.StartsAt)
            .Select(booking => ToView(booking, typeTitles.GetValueOrDefault(booking.BookingTypeId, "Unknown")))
            .ToList();
    }

    public async Task<IReadOnlyList<BookingView>> ListBookingsForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var bookings = await db.Bookings.AsNoTracking()
            .Where(booking => booking.ContactId == contactId)
            .ToListAsync(cancellationToken);

        var typeIds = bookings.Select(booking => booking.BookingTypeId).Distinct().ToList();
        var typeTitles = await db.BookingTypes.AsNoTracking()
            .Where(type => typeIds.Contains(type.Id))
            .ToDictionaryAsync(type => type.Id, type => type.Title, cancellationToken);

        return bookings
            .OrderByDescending(booking => booking.StartsAt)
            .Select(booking => ToView(booking, typeTitles.GetValueOrDefault(booking.BookingTypeId, "Unknown")))
            .ToList();
    }

    public async Task<BookingView?> GetBookingByManageTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token.Trim());
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(item => item.ManageTokenHash == hash, cancellationToken);
        if (booking is null) return null;

        var title = await db.BookingTypes.AsNoTracking()
            .Where(type => type.Id == booking.BookingTypeId)
            .Select(type => type.Title)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";
        return ToView(booking, title);
    }

    public async Task<bool> CancelBookingAsync(Guid bookingId, string performedBy, CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings.FirstOrDefaultAsync(item => item.Id == bookingId, cancellationToken);
        return await CancelInternalAsync(booking, performedBy, cancellationToken);
    }

    public async Task<bool> CancelBookingByManageTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hash = HashToken(token.Trim());
        var booking = await db.Bookings.FirstOrDefaultAsync(item => item.ManageTokenHash == hash, cancellationToken);
        return await CancelInternalAsync(booking, booking?.AttendeeEmail ?? "unknown", cancellationToken);
    }

    private async Task<bool> CancelInternalAsync(Booking? booking, string performedBy, CancellationToken cancellationToken)
    {
        if (booking is null || booking.Status == BookingStatuses.Cancelled) return false;

        var type = await db.BookingTypes.AsNoTracking()
            .Where(item => item.Id == booking.BookingTypeId)
            .Select(item => item.Title)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        var now = timeProvider.GetUtcNow();
        booking.Status = BookingStatuses.Cancelled;
        booking.CancelledAt = now;
        booking.UpdatedAt = now;
        booking.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        await emailSender.SendCancellationAsync(booking.AttendeeEmail, booking.AttendeeName, type, booking.StartsAt, cancellationToken);
        return true;
    }

    private async Task<Guid> FindOrCreateContactAsync(string name, string email, CancellationToken cancellationToken)
    {
        var existing = await db.Contacts
            .Where(contact => contact.TrashedAt == null && contact.Email != null)
            .FirstOrDefaultAsync(contact => contact.Email!.ToLower() == email.ToLower(), cancellationToken);
        if (existing is not null) return existing.Id;

        var contact = new Contact { FullName = name, Email = email, CreatedBy = "booking-page" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(cancellationToken);
        return contact.Id;
    }

    private async Task<string> GetUniqueSlugAsync(string requestedSlug, Guid currentTypeId, CancellationToken cancellationToken)
    {
        var baseSlug = string.IsNullOrWhiteSpace(requestedSlug) ? "booking" : requestedSlug;
        var existingSlugs = await db.BookingTypes.AsNoTracking()
            .Where(type => type.Id != currentTypeId)
            .Select(type => type.Slug)
            .ToListAsync(cancellationToken);
        if (!existingSlugs.Contains(baseSlug, StringComparer.OrdinalIgnoreCase)) return baseSlug;

        var suffix = 2;
        while (existingSlugs.Contains($"{baseSlug}-{suffix}", StringComparer.OrdinalIgnoreCase)) suffix++;
        return $"{baseSlug}-{suffix}";
    }

    private static string CreateSlug(string value)
    {
        var builder = new StringBuilder(value.Trim().Length);
        var previousWasDash = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasDash = false;
            }
            else if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }
        var normalized = builder.ToString().Trim('-');
        return normalized.Length == 0 ? "booking" : normalized;
    }

    private static List<BookingAvailabilityWindow> ParseAvailability(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<BookingAvailabilityWindow>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static BookingTypeView ToView(BookingType type) => new(
        type.Id, type.Title, type.Slug, type.Description, type.DurationMinutes, type.BufferMinutes,
        type.OwnerUsername, type.IsActive, ParseAvailability(type.AvailabilityJson));

    private static BookingView ToView(Booking booking, string bookingTypeTitle) => new(
        booking.Id, booking.BookingTypeId, bookingTypeTitle, booking.StartsAt, booking.EndsAt,
        booking.AttendeeName, booking.AttendeeEmail, booking.Notes, booking.Status, booking.CreatedAt,
        booking.ContactId);
}
