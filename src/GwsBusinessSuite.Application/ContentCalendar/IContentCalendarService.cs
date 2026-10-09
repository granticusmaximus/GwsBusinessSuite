namespace GwsBusinessSuite.Application.ContentCalendar;

public static class ContentCalendarKinds
{
    public const string Post = "post";
    public const string Page = "page";
    public const string Social = "social";
    public const string AlertEmail = "alert-email";
    public const string Drip = "drip";
    public const string LiveShow = "live";

    // What visitors see go out - the gap warning only counts these.
    public static readonly string[] Publishing = [Post, Page, Social];

    public static readonly string[] All = [Post, Page, Social, AlertEmail, Drip, LiveShow];

    public static string Label(string kind) => kind switch
    {
        Post => "Posts",
        Page => "Pages",
        Social => "Social",
        AlertEmail => "Article alerts",
        Drip => "Drip emails",
        LiveShow => "Live shows",
        _ => kind
    };
}

// One thing going out (or that went out). Upcoming is true for anything still in the future.
// CanReschedule marks the scheduled items the calendar can move: posts, pages and social posts
// not yet published - emails follow their campaign's own timing and live shows aren't scheduled.
public sealed record ContentCalendarItem(
    string Kind,
    Guid Id,
    string Title,
    DateTimeOffset At,
    string Status,
    string Link,
    bool Upcoming,
    bool CanReschedule);

public interface IContentCalendarService
{
    Task<IReadOnlyList<ContentCalendarItem>> GetItemsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);

    // Moves a scheduled post, page or social post. Throws InvalidOperationException when the item
    // has already gone out or the new time isn't in the future.
    Task RescheduleAsync(string kind, Guid id, DateTimeOffset newTimeUtc, string actor, CancellationToken cancellationToken = default);
}
