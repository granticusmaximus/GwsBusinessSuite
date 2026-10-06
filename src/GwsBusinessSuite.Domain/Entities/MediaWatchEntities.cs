using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// Single row: how long fetched articles are kept, and the topic digest email.
public sealed class NewsWatchSettings : AuditableEntity
{
    public int RetentionHours { get; set; } = NewsRetention.DefaultHours;
    public bool DigestEnabled { get; set; }
    public string? DigestRecipient { get; set; }
    public string DigestFrequency { get; set; } = NewsDigestFrequencies.Daily;
    public int DigestHourLocal { get; set; } = 7;
    // 0 = Sunday ... 6 = Saturday; only used for the weekly digest.
    public int DigestDayOfWeek { get; set; } = 1;
    public DateTimeOffset? LastDigestSentAt { get; set; }
}

public static class NewsRetention
{
    public const int DefaultHours = 24;
    public static readonly int[] AllowedHours = [24, 48, 72, 168];
}

public static class NewsDigestFrequencies
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
}

// One admin has opened this article. Keyed by a hash of the URL, not the NewsItem id, because the
// same story can sit in several topics and rows come and go as retention prunes them.
public sealed class NewsReadMark : AuditableEntity
{
    public required string Username { get; set; }
    public required string UrlHash { get; set; }
    public DateTimeOffset ReadAt { get; set; }
}

// An article an admin kept. A copy, so it survives after the feed's retention removes the item.
public sealed class SavedNewsArticle : AuditableEntity
{
    public required string Username { get; set; }
    public required string UrlHash { get; set; }
    public required string Url { get; set; }
    public required string Title { get; set; }
    public string Source { get; set; } = string.Empty;
    public string TopicName { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public Guid? ClippedWikiPageId { get; set; }
}

// Per topic per day: how many new articles arrived and which title terms they used. Kept for
// 60 days, independent of article retention, so the Trends view has history to compare against.
public sealed class NewsTrendDay : AuditableEntity
{
    // A WatchedTopic id, or "top" for the shared All News pool.
    public required string TopicKey { get; set; }
    // yyyy-MM-dd in UTC.
    public required string Day { get; set; }
    public int ArticleCount { get; set; }
    public string TermCountsJson { get; set; } = "{}";
}
