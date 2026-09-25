using CodeHollow.FeedReader;
using CodeHollow.FeedReader.Feeds;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// defend.network/feed.xml is a real, live, no-key RSS 2.0 feed - confirmed directly (31KB,
// well-formed, real briefings) before building this. Uses the same CodeHollow.FeedReader
// package NewsIntelligenceService/PodcastDirectoryService already depend on for RSS, rather
// than introducing a second feed-parsing library.
public sealed class DefendNetworkFeedService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<DefendNetworkFeedService> logger) : IDefendNetworkFeedService
{
    private const string FeedUrl = "https://defend.network/feed.xml";
    private const string CacheKey = "defend-network:feed";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public async Task<IReadOnlyList<ThreatBriefing>> GetRecentBriefingsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<ThreatBriefing>? cached) && cached is not null)
            return cached;

        try
        {
            using var client = httpClientFactory.CreateClient();
            var content = await client.GetStringAsync(FeedUrl, cancellationToken);
            var feed = FeedReader.ReadFromString(content);

            var briefings = feed.Items
                .Select(ToBriefing)
                .OrderByDescending(b => b.PublishedAt ?? DateTimeOffset.MinValue)
                .ToList();

            cache.Set(CacheKey, (IReadOnlyList<ThreatBriefing>)briefings, CacheDuration);
            return briefings;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch defend.network feed");
            return [];
        }
    }

    private static ThreatBriefing ToBriefing(FeedItem item)
    {
        string? severity = null;
        var tags = new List<string>();

        // The generic FeedItem.Categories list flattens every <category> element regardless of
        // its "domain" attribute - defend.network uses a bare (no-domain) <category> for
        // severity and domain-attributed ones for threat-type/industry tags, so read the raw
        // RSS element directly to keep that distinction, matching the existing
        // NewsIntelligenceService <source>-extraction precedent for reading raw feed XML.
        if (item.SpecificItem is Rss20FeedItem rss && rss.Element is not null)
        {
            foreach (var category in rss.Element.Elements("category"))
            {
                var value = category.Value.Trim();
                if (value.Length == 0) continue;

                if (category.Attribute("domain") is null)
                    severity ??= value;
                else
                    tags.Add(value);
            }
        }

        return new ThreatBriefing(
            Title: item.Title ?? string.Empty,
            Description: item.Description ?? string.Empty,
            Url: item.Link ?? string.Empty,
            PublishedAt: item.PublishingDate,
            Severity: severity,
            Tags: tags);
    }
}
