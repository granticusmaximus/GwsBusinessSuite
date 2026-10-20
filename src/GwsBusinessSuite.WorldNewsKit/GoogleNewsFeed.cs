using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GwsBusinessSuite.WorldNewsKit;

public sealed record NewsEdition(string Country, string Language, string Hl, string Gl, string Ceid)
{
    public string Url => $"https://news.google.com/rss?hl={Hl}&gl={Gl}&ceid={Ceid}";
}

// Google News' front page for one country, in its own language (verified live 20-10-26 for
// France, Japan and Turkey: about 30 stories each). Each story's description lists the other
// outlets covering it, so one request gives several reports per story; they share a GroupHint.
// Links are Google redirects, fine for reading but not for pulling article text.
public static partial class GoogleNewsFeed
{
    public static readonly IReadOnlyList<NewsEdition> Editions =
    [
        new("United States", "English", "en-US", "US", "US:en"),
        new("United Kingdom", "English", "en-GB", "GB", "GB:en"),
        new("Canada", "English", "en-CA", "CA", "CA:en"),
        new("Australia", "English", "en-AU", "AU", "AU:en"),
        new("India", "English", "en-IN", "IN", "IN:en"),
        new("Nigeria", "English", "en-NG", "NG", "NG:en"),
        new("South Africa", "English", "en-ZA", "ZA", "ZA:en"),
        new("Kenya", "English", "en-KE", "KE", "KE:en"),
        new("Philippines", "English", "en-PH", "PH", "PH:en"),
        new("Pakistan", "English", "en-PK", "PK", "PK:en"),
        new("France", "French", "fr", "FR", "FR:fr"),
        new("Germany", "German", "de", "DE", "DE:de"),
        new("Italy", "Italian", "it", "IT", "IT:it"),
        new("Spain", "Spanish", "es", "ES", "ES:es"),
        new("Portugal", "Portuguese", "pt-PT", "PT", "PT:pt-150"),
        new("Netherlands", "Dutch", "nl", "NL", "NL:nl"),
        new("Sweden", "Swedish", "sv", "SE", "SE:sv"),
        new("Norway", "Norwegian", "no", "NO", "NO:no"),
        new("Poland", "Polish", "pl", "PL", "PL:pl"),
        new("Czechia", "Czech", "cs", "CZ", "CZ:cs"),
        new("Hungary", "Hungarian", "hu", "HU", "HU:hu"),
        new("Romania", "Romanian", "ro", "RO", "RO:ro"),
        new("Greece", "Greek", "el", "GR", "GR:el"),
        new("Ukraine", "Ukrainian", "uk", "UA", "UA:uk"),
        new("Russia", "Russian", "ru", "RU", "RU:ru"),
        new("Turkey", "Turkish", "tr", "TR", "TR:tr"),
        new("Israel", "Hebrew", "he", "IL", "IL:he"),
        new("Saudi Arabia", "Arabic", "ar", "SA", "SA:ar"),
        new("Egypt", "Arabic", "ar", "EG", "EG:ar"),
        new("United Arab Emirates", "Arabic", "ar", "AE", "AE:ar"),
        new("Brazil", "Portuguese", "pt-BR", "BR", "BR:pt-419"),
        new("Mexico", "Spanish", "es-419", "MX", "MX:es-419"),
        new("Argentina", "Spanish", "es-419", "AR", "AR:es-419"),
        new("Colombia", "Spanish", "es-419", "CO", "CO:es-419"),
        new("Japan", "Japanese", "ja", "JP", "JP:ja"),
        new("South Korea", "Korean", "ko", "KR", "KR:ko"),
        new("China", "Chinese", "zh-CN", "CN", "CN:zh-Hans"),
        new("Taiwan", "Chinese", "zh-TW", "TW", "TW:zh-Hant"),
        new("Indonesia", "Indonesian", "id", "ID", "ID:id"),
        new("Thailand", "Thai", "th", "TH", "TH:th"),
        new("Vietnam", "Vietnamese", "vi", "VN", "VN:vi"),
    ];

    public static IReadOnlyList<WorldArticle> Parse(string xml, NewsEdition edition)
    {
        var articles = new List<WorldArticle>();
        var feed = $"google-news:{edition.Ceid}";
        foreach (var item in XDocument.Parse(xml).Descendants("item"))
        {
            var link = item.Element("link")?.Value.Trim();
            if (string.IsNullOrEmpty(link)) continue;
            var seenAt = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var published) ? published : DateTimeOffset.UtcNow;
            var group = $"gn:{edition.Ceid}:{item.Element("guid")?.Value ?? link}";

            var related = RelatedReport().Matches(item.Element("description")?.Value ?? string.Empty);
            if (related.Count > 0)
            {
                foreach (Match report in related)
                {
                    var title = ArticleNormalizer.CleanTitle(report.Groups["title"].Value);
                    if (title.Length == 0) continue;
                    var url = System.Net.WebUtility.HtmlDecode(report.Groups["href"].Value);
                    articles.Add(new WorldArticle(ArticleNormalizer.IdFor(url), url, title,
                        ArticleNormalizer.CleanTitle(report.Groups["outlet"].Value), edition.Language, edition.Country, seenAt, feed, group));
                }
                continue;
            }

            var outlet = ArticleNormalizer.CleanTitle(item.Element("source")?.Value ?? string.Empty);
            var headline = ArticleNormalizer.CleanTitle(item.Element("title")?.Value ?? string.Empty);
            if (outlet.Length > 0 && headline.EndsWith(" - " + outlet, StringComparison.Ordinal))
                headline = headline[..^(outlet.Length + 3)];
            if (headline.Length == 0) continue;
            articles.Add(new WorldArticle(ArticleNormalizer.IdFor(link), link, headline, outlet, edition.Language, edition.Country, seenAt, feed, group));
        }
        return articles;
    }

    // <li><a href="…">Headline</a>&nbsp;&nbsp;<font color="#6f6f6f">Outlet</font></li>
    [GeneratedRegex("""<a href="(?<href>[^"]+)"[^>]*>(?<title>.*?)</a>(?:&nbsp;|\s)*<font[^>]*>(?<outlet>.*?)</font>""", RegexOptions.Singleline)]
    private static partial Regex RelatedReport();
}
