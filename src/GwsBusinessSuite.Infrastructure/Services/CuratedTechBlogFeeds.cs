namespace GwsBusinessSuite.Infrastructure.Services;

// A small, fixed set of well-known engineering blogs used as an additional source for
// Technical-type WatchedTopics (alongside Hacker News + dev.to). Unlike HN Algolia or
// dev.to tags, these feeds have no keyword-search API - each is fetched in full and
// matched against a topic's keywords by NewsIntelligenceService.FetchTechnicalArticlesAsync.
public static class CuratedTechBlogFeeds
{
    public static readonly IReadOnlyList<(string Name, string Url)> Feeds =
    [
        ("GitHub Engineering", "http://githubengineering.com/atom.xml"),
        ("Stripe Blog", "https://stripe.com/blog/feed.rss"),
        ("The Cloudflare Blog", "https://blog.cloudflare.com/rss/"),
        ("Uber Engineering Blog", "https://eng.uber.com/feed/"),
        ("Dropbox Tech", "https://dropbox.tech/feed"),
        ("Shopify Engineering", "https://shopifyengineering.myshopify.com/blogs/engineering.atom"),
        ("Slack Engineering", "https://slack.engineering/feed"),
        ("Engineering at Meta", "https://engineering.fb.com/feed/"),
        ("Spotify Engineering", "https://engineering.atspotify.com/feed/"),
        ("The Pragmatic Engineer", "https://blog.pragmaticengineer.com/rss/"),
        ("Airbnb Tech Blog", "https://medium.com/feed/airbnb-engineering"),
        ("Pinterest Engineering", "https://medium.com/feed/pinterest-engineering"),
        ("PayPal Technology Blog", "https://medium.com/feed/paypal-engineering"),
    ];
}
