namespace GwsBusinessSuite.Application.DeveloperApi;

// "What needs me right now" at a glance - the menu-bar companion's status read (sentinel:read).
// Health: "ok" (no unread container alerts), "warning" (unread warnings), "critical" (an unread
// error). Counts are workspace-wide except the ones marked per-user, which are for the API key's
// owner.
public sealed record DeveloperApiStatusSummary(
    string Health,
    int UnreadHealthAlerts,
    int OpenTickets,
    int OverdueTickets,
    int DueFollowUps,
    int UnreadFormSubmissions,
    int PendingComments,
    int UnreadMessages,          // per-user
    int UnreadAreaAlerts,        // per-user
    DeveloperApiStatusBooking? NextBooking,
    DeveloperApiStatusTimer? RunningTimer, // per-user
    DateTimeOffset GeneratedAt)
{
    // Everything that wants attention, for a single number next to the menu-bar icon.
    public int AttentionCount => UnreadHealthAlerts + OverdueTickets + DueFollowUps + UnreadFormSubmissions
                                 + PendingComments + UnreadMessages + UnreadAreaAlerts;
}

public sealed record DeveloperApiStatusBooking(string Title, string AttendeeName, DateTimeOffset StartsAt);

public sealed record DeveloperApiStatusTimer(string ContactName, string Description, DateTimeOffset StartedAt);

public interface IDeveloperApiStatusService
{
    Task<DeveloperApiStatusSummary> GetSummaryAsync(string ownerUsername, CancellationToken cancellationToken = default);
}
