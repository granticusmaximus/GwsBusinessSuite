namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public sealed record AffiliateClickClassification(bool Passed, string? Reason)
{
    public static readonly AffiliateClickClassification Ok = new(true, null);
}

// Verified against production data: 6,050 recorded affiliate clicks against 1,322 total
// pageviews (4.6 clicks per pageview on a site averaging 1.1 pages per session) - a ratio
// only explainable by unfiltered crawler/bot traffic being counted as real clicks. This runs
// before a click is counted, not before the redirect: a filtered hit still reaches its
// destination, it just never inflates a click count a real advertiser might see.
public static class AffiliateClickFilter
{
    // Substrings of declared bot/crawler user-agents, matched case-insensitively. This is a
    // known-crawler denylist (the same category MRC-style ad-viewability guidance filters),
    // not an attempt to detect every automated client - a determined scraper can still fake a
    // browser UA, which is a cost/reward tradeoff this simple check accepts.
    private static readonly string[] BotUserAgentMarkers =
    [
        "bot", "spider", "crawl", "slurp", "curl/", "wget/", "python-requests",
        "python-urllib", "go-http-client", "java/", "libwww-perl", "headlesschrome",
        "phantomjs", "facebookexternalhit", "preview", "monitor", "uptimerobot",
        "pingdom", "ahrefs", "semrush", "mj12bot", "dotbot", "petalbot", "bytespider",
        "gptbot", "ccbot", "claudebot", "yandex", "baiduspider"
    ];

    public static AffiliateClickClassification Classify(string? userAgent, string? prefetchHeader)
    {
        if (!string.IsNullOrWhiteSpace(prefetchHeader))
        {
            return new AffiliateClickClassification(false, "Prefetch request");
        }

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new AffiliateClickClassification(false, "Missing user-agent");
        }

        var lowered = userAgent.ToLowerInvariant();
        foreach (var marker in BotUserAgentMarkers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
            {
                return new AffiliateClickClassification(false, $"User-agent matched known bot marker '{marker}'");
            }
        }

        return AffiliateClickClassification.Ok;
    }
}
