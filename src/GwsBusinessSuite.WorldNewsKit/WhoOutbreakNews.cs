using System.Globalization;
using System.Text.Json;

namespace GwsBusinessSuite.WorldNewsKit;

// WHO's Disease Outbreak News (07-11-26) as reports from "World Health Organization": the agency's
// own account of an outbreak, beside the outlets' in a story. Its public JSON API needs no key;
// the latest 20. Each item's page is /emergencies/disease-outbreak-news/item/{UrlName}.
// Newest by DateCreated: ordered by PublicationDateAndTime, the API left out that week's items at
// most page sizes (07-11-26: $top=3 had 06-11's, $top=5, 10 and 20 started at 23-10).
public static class WhoOutbreakNews
{
    public const string Outlet = "World Health Organization";
    public const string Feed = "who:disease-outbreak-news";
    public const string Url = "https://www.who.int/api/news/diseaseoutbreaknews?$orderby=DateCreated%20desc&$top=20";
    private const string ItemUrl = "https://www.who.int/emergencies/disease-outbreak-news/item/";

    public static IReadOnlyList<WorldArticle> Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var articles = new List<WorldArticle>();
        foreach (var item in items.EnumerateArray())
        {
            var slug = Text(item, "UrlName");
            var title = ArticleNormalizer.CleanTitle(
                item.TryGetProperty("UseOverrideTitle", out var useOverride) && useOverride.ValueKind == JsonValueKind.True && Text(item, "OverrideTitle").Length > 0
                    ? Text(item, "OverrideTitle")
                    : Text(item, "Title"));
            if (slug.Length == 0 || title.Length == 0) continue;
            var url = ItemUrl + Uri.EscapeDataString(slug);
            var published = DateTimeOffset.TryParse(Text(item, "PublicationDateAndTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) && at < now ? at : now;
            articles.Add(new WorldArticle(ArticleNormalizer.IdFor(url), url, title, Outlet, "English", OutletFeeds.International,
                published, Feed, Snippet: ArticleNormalizer.CleanSnippet(Text(item, "Summary"), title)));
        }
        return articles;
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;
}
