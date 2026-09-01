using System.ComponentModel.DataAnnotations;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Scheduling;

public sealed record BookingTypeView(
    Guid Id,
    string Title,
    string Slug,
    string Description,
    int DurationMinutes,
    int BufferMinutes,
    string? OwnerUsername,
    bool IsActive,
    IReadOnlyList<BookingAvailabilityWindow> Availability);

public sealed class BookingTypeEditorModel
{
    public Guid? Id { get; set; }
    [Required]
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DurationMinutes { get; set; } = 30;
    public int BufferMinutes { get; set; }
    public string? OwnerUsername { get; set; }
    public bool IsActive { get; set; } = true;
    public List<BookingAvailabilityWindow> Availability { get; set; } = [];
}

// A bookable window on the public page - not yet reserved. StartsAt/EndsAt are UTC.
public sealed record BookingSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record BookingView(
    Guid Id,
    Guid BookingTypeId,
    string BookingTypeTitle,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AttendeeName,
    string AttendeeEmail,
    string Notes,
    string Status,
    DateTimeOffset CreatedAt,
    Guid? ContactId);

public interface IBookingService
{
    Task<IReadOnlyList<BookingTypeView>> ListBookingTypesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<BookingTypeView?> GetBookingTypeBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<BookingTypeView> SaveBookingTypeAsync(BookingTypeEditorModel editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteBookingTypeAsync(Guid bookingTypeId, CancellationToken cancellationToken = default);

    // Computes free slots from the type's recurring weekly Availability minus its own already-
    // booked (non-cancelled) slots - daysAhead is capped by the implementation to keep this cheap.
    Task<IReadOnlyList<BookingSlot>> GetAvailableSlotsAsync(
        Guid bookingTypeId, DateTimeOffset fromUtc, int daysAhead, CancellationToken cancellationToken = default);

    // Null if the requested slot is no longer free (already booked by someone else, or has
    // slipped into the past) - the caller re-shows availability rather than treating this as
    // an exceptional failure.
    Task<BookingView?> CreateBookingAsync(
        Guid bookingTypeId, DateTimeOffset startsAtUtc, string attendeeName, string attendeeEmail, string notes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookingView>> ListBookingsAsync(Guid? bookingTypeId = null, CancellationToken cancellationToken = default);

    // For the per-Contact detail page's Bookings section - every booking linked to this Contact
    // (ContactId is set on booking, a loose reference - see Booking.ContactId), newest first.
    Task<IReadOnlyList<BookingView>> ListBookingsForContactAsync(Guid contactId, CancellationToken cancellationToken = default);
    Task<BookingView?> GetBookingByManageTokenAsync(string token, CancellationToken cancellationToken = default);
    Task<bool> CancelBookingAsync(Guid bookingId, string performedBy, CancellationToken cancellationToken = default);
    Task<bool> CancelBookingByManageTokenAsync(string token, CancellationToken cancellationToken = default);
}

public interface IBookingEmailSender
{
    Task SendConfirmationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, string manageUrl, CancellationToken cancellationToken = default);
    Task SendCancellationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, CancellationToken cancellationToken = default);
}
