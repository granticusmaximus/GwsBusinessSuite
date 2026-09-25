namespace GwsBusinessSuite.Application.ThreatIntel;

public interface IDefendNetworkFeedService
{
    // Parses defend.network's real, free, no-key RSS feed (feed.xml). Returns an empty list
    // (never throws) on any HTTP/parse failure.
    Task<IReadOnlyList<ThreatBriefing>> GetRecentBriefingsAsync(CancellationToken cancellationToken = default);
}

public sealed record ThreatBriefing(
    string Title,
    string Description,
    string Url,
    DateTimeOffset? PublishedAt,
    string? Severity,
    IReadOnlyList<string> Tags);
