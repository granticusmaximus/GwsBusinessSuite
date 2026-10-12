namespace GwsBusinessSuite.Application.Wiki;

// Meeting notes linked to the calendar and CRM (Sentinel item 28). The Mac app's Notes tab picks a
// booking or contact; the saved note page records it and appears in the contact's History.
public sealed record SentinelMeetingTarget(string Kind, Guid Id, string Label, string Detail);

public interface ISentinelMeetingNotesService
{
    // Bookings from a week back to a week ahead, then contacts (matching `query` when given).
    Task<IReadOnlyList<SentinelMeetingTarget>> ListTargetsAsync(string? query, CancellationToken cancellationToken = default);

    // Records what the page is about. A booking brings its contact along. Unknown ids are refused.
    Task LinkAsync(Guid pageId, Guid? contactId, Guid? bookingId, CancellationToken cancellationToken = default);
}

public static class SentinelMeetingTargetKinds
{
    public const string Booking = "booking";
    public const string Contact = "contact";
}
