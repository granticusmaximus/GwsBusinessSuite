namespace GwsBusinessSuite.Application.Scheduling;

public sealed record BookingCalendarFeedView(
    Guid Id,
    string Name,
    string UrlHint,
    string? OwnerUsername,
    bool AllDayEventsBlock,
    int BusyCount,
    int SkippedCount,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError);

public sealed record BookingBusyTime(DateTimeOffset Start, DateTimeOffset End);

// Url is site-relative (/bookings/calendar/{token}.ics); null when the feed is off.
public sealed record BookingCalendarExportView(string? Url);

// Calendar sync both ways: busy times read from the owner's calendars hide booking slots, and a
// private iCal feed puts confirmed bookings into their calendar.
public interface IBookingCalendarService
{
    Task<IReadOnlyList<BookingCalendarFeedView>> ListFeedsAsync(CancellationToken cancellationToken = default);

    // Reads the calendar first, so a wrong address is refused rather than saved.
    Task<BookingCalendarFeedView> AddFeedAsync(string name, string address, string? ownerUsername, bool allDayEventsBlock,
        string performedBy, CancellationToken cancellationToken = default);

    Task RemoveFeedAsync(Guid feedId, CancellationToken cancellationToken = default);

    // A failed read keeps the last busy times and records the error.
    Task<BookingCalendarFeedView?> SyncFeedAsync(Guid feedId, CancellationToken cancellationToken = default);

    Task<int> SyncAllAsync(CancellationToken cancellationToken = default);

    // Busy times that apply to a booking type with this owner; all-day events are read as whole
    // days in the given zone.
    Task<IReadOnlyList<BookingBusyTime>> GetBusyAsync(string? ownerUsername, TimeZoneInfo zone, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    Task<BookingCalendarExportView> GetExportAsync(CancellationToken cancellationToken = default);

    // A new link each time; the old one stops working.
    Task<BookingCalendarExportView> CreateExportLinkAsync(string performedBy, CancellationToken cancellationToken = default);

    Task DisableExportAsync(string performedBy, CancellationToken cancellationToken = default);

    // The feed's iCal text, or null for an unknown or turned-off link.
    Task<string?> RenderExportAsync(string token, CancellationToken cancellationToken = default);
}
