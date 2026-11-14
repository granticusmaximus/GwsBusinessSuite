using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GwsBusinessSuite.WorldNewsKit;

// One newsroom's own feed. Country is where the outlet is based; BBC language services are
// "United Kingdom" whatever language they write in.
public sealed record NewsOutlet(string Name, string Language, string Country, string Url)
{
    public string Feed => "outlet:" + Url;
}

// Outlets' own RSS feeds, which Google News can't give: the real article URL and the outlet's
// short description of each story, which the event summaries are written from. Reads RSS 2.0,
// RSS 1.0 (RDF, DW's feeds) and Atom.
public static partial class OutletFeeds
{
    // Where a newsroom that belongs to no country is based (UN News, WHO). Region: Other.
    public const string International = "International";

    // Every feed answered with stories on 21-10-26. Left out after that check: Euronews French
    // and German (HTTP 406), CBC (no answer), El Mundo (timed out), Folha (no items).
    // Taken out 05-11-26: France 24 (all four languages) and The Times of Israel answer the
    // server 403 Access Denied, so their refusal page was being parsed as RSS on every read.
    // Added 07-11-26, each answering with that day's stories: the US outlets that only came
    // through Google News, then the ones below them. Names are as Google News gives them (or
    // OutletNames maps them), so both routes count as one outlet. Left out that day: CNN (its feed
    // stopped in April 2023), USA Today (402), Al Arabiya and Arab News (403 to the app), Kyodo and
    // Mail & Guardian (404).
    public static readonly IReadOnlyList<NewsOutlet> Outlets =
    [
        new("The New York Times", "English", "United States", "https://rss.nytimes.com/services/xml/rss/nyt/World.xml"),
        new("The New York Times", "English", "United States", "https://rss.nytimes.com/services/xml/rss/nyt/US.xml"),
        new("The Washington Post", "English", "United States", "https://feeds.washingtonpost.com/rss/world"),
        new("The Wall Street Journal", "English", "United States", "https://feeds.content.dowjones.io/public/rss/RSSWorldNews"),
        new("CBS News", "English", "United States", "https://www.cbsnews.com/latest/rss/world"),
        new("NBC News", "English", "United States", "https://feeds.nbcnews.com/nbcnews/public/world"),
        new("ABC News (US)", "English", "United States", "https://abcnews.go.com/abcnews/internationalheadlines"),
        new("Fox News", "English", "United States", "https://moxie.foxnews.com/google-publisher/world.xml"),
        new("PBS News", "English", "United States", "https://www.pbs.org/newshour/feeds/rss/world"),
        new("Politico", "English", "United States", "https://rss.politico.com/politics-news.xml"),
        new("The Hill", "English", "United States", "https://thehill.com/news/feed/"),
        new("Axios", "English", "United States", "https://api.axios.com/feed/"),
        new("Los Angeles Times", "English", "United States", "https://www.latimes.com/world-nation/rss2.0.xml"),
        new("Bloomberg", "English", "United States", "https://feeds.bloomberg.com/politics/news.rss"),
        new("The Christian Science Monitor", "English", "United States", "https://rss.csmonitor.com/feeds/world"),
        new("ProPublica", "English", "United States", "https://feeds.propublica.org/propublica/main"),
        new("New York Post", "English", "United States", "https://nypost.com/world-news/feed/"),
        new("CNBC", "English", "United States", "https://www.cnbc.com/id/100727362/device/rss/rss.html"),
        new("Infobae", "Spanish", "Argentina", "https://www.infobae.com/arc/outboundfeeds/rss/"),
        new("G1", "Portuguese", "Brazil", "https://g1.globo.com/rss/g1/mundo/"),
        new("The Globe and Mail", "English", "Canada", "https://www.theglobeandmail.com/arc/outboundfeeds/rss/category/world/"),
        new("Africanews", "English", "Republic of the Congo", "https://www.africanews.com/feed/rss"),
        new("The Moscow Times", "English", "Netherlands", "https://www.themoscowtimes.com/rss/news"),
        new("CGTN", "English", "China", "https://www.cgtn.com/subscribe/rss/section/world.xml"),
        new("UN News", "English", International, "https://news.un.org/feed/subscribe/en/news/all/rss.xml"),
        new("Al Jazeera", "English", "Qatar", "https://www.aljazeera.com/xml/rss/all.xml"),
        new("BBC News", "English", "United Kingdom", "https://feeds.bbci.co.uk/news/world/rss.xml"),
        new("The Guardian", "English", "United Kingdom", "https://www.theguardian.com/world/rss"),
        new("NPR", "English", "United States", "https://feeds.npr.org/1004/rss.xml"),
        new("DW", "English", "Germany", "https://rss.dw.com/rdf/rss-en-all"),
        new("RFI", "English", "France", "https://www.rfi.fr/en/rss"),
        new("Euronews", "English", "France", "https://www.euronews.com/rss"),
        new("Yonhap", "English", "South Korea", "https://en.yna.co.kr/RSS/news.xml"),
        new("The Times of India", "English", "India", "https://timesofindia.indiatimes.com/rssfeeds/296589292.cms"),
        new("The Hindu", "English", "India", "https://www.thehindu.com/news/international/feeder/default.rss"),
        new("Dawn", "English", "Pakistan", "https://www.dawn.com/feeds/world"),
        new("South China Morning Post", "English", "Hong Kong", "https://www.scmp.com/rss/91/feed"),
        new("The Straits Times", "English", "Singapore", "https://www.straitstimes.com/news/world/rss.xml"),
        new("CNA", "English", "Singapore", "https://www.channelnewsasia.com/rssfeeds/8395884"),
        new("The Japan Times", "English", "Japan", "https://www.japantimes.co.jp/feed/"),
        new("ABC News (Australia)", "English", "Australia", "https://www.abc.net.au/news/feed/51120/rss.xml"),
        new("RNZ", "English", "New Zealand", "https://www.rnz.co.nz/rss/world.xml"),
        new("Daily Maverick", "English", "South Africa", "https://www.dailymaverick.co.za/dmrss/"),
        new("Premium Times", "English", "Nigeria", "https://www.premiumtimesng.com/feed"),
        new("The Kyiv Independent", "English", "Ukraine", "https://kyivindependent.com/news-archive/rss/"),
        new("Anadolu Agency", "English", "Turkey", "https://www.aa.com.tr/en/rss/default?cat=guncel"),
        new("Hürriyet Daily News", "English", "Turkey", "https://www.hurriyetdailynews.com/rss/world"),
        new("TASS", "English", "Russia", "https://tass.com/rss/v2.xml"),
        new("Al Jazeera Arabic", "Arabic", "Qatar", "https://www.aljazeera.net/aljazeerarss/a7c186be-1baa-4bd4-9d80-a84db769f779/73d0e1b4-532f-45ef-b135-bfdff8b8cab9"),
        new("BBC Arabic", "Arabic", "United Kingdom", "https://feeds.bbci.co.uk/arabic/rss.xml"),
        new("RFI", "French", "France", "https://www.rfi.fr/fr/rss"),
        new("Le Monde", "French", "France", "https://www.lemonde.fr/international/rss_full.xml"),
        new("Le Figaro", "French", "France", "https://www.lefigaro.fr/rss/figaro_international.xml"),
        new("DW", "Spanish", "Germany", "https://rss.dw.com/rdf/rss-sp-all"),
        new("BBC Mundo", "Spanish", "United Kingdom", "https://feeds.bbci.co.uk/mundo/rss.xml"),
        new("El País", "Spanish", "Spain", "https://feeds.elpais.com/mrss-s/pages/ep/site/elpais.com/section/internacional/portada"),
        new("La Vanguardia", "Spanish", "Spain", "https://www.lavanguardia.com/rss/internacional.xml"),
        new("Clarín", "Spanish", "Argentina", "https://www.clarin.com/rss/mundo/"),
        new("DW", "German", "Germany", "https://rss.dw.com/rdf/rss-de-all"),
        new("Der Spiegel", "German", "Germany", "https://www.spiegel.de/ausland/index.rss"),
        new("tagesschau", "German", "Germany", "https://www.tagesschau.de/xml/rss2/"),
        new("ANSA", "Italian", "Italy", "https://www.ansa.it/sito/notizie/mondo/mondo_rss.xml"),
        new("la Repubblica", "Italian", "Italy", "https://www.repubblica.it/rss/esteri/rss2.0.xml"),
        new("NOS", "Dutch", "Netherlands", "https://feeds.nos.nl/nosnieuwsbuitenland"),
        new("SVT Nyheter", "Swedish", "Sweden", "https://www.svt.se/nyheter/utrikes/rss.xml"),
        new("NRK", "Norwegian", "Norway", "https://www.nrk.no/toppsaker.rss"),
        new("DW", "Russian", "Germany", "https://rss.dw.com/rdf/rss-ru-all"),
        new("BBC Russian", "Russian", "United Kingdom", "https://feeds.bbci.co.uk/russian/rss.xml"),
        new("Meduza", "Russian", "Latvia", "https://meduza.io/rss/all"),
        new("BBC Ukrainian", "Ukrainian", "United Kingdom", "https://feeds.bbci.co.uk/ukrainian/rss.xml"),
        new("Ukrainska Pravda", "Ukrainian", "Ukraine", "https://www.pravda.com.ua/rss/"),
        new("BBC Turkish", "Turkish", "United Kingdom", "https://feeds.bbci.co.uk/turkce/rss.xml"),
        new("BBC Persian", "Persian", "United Kingdom", "https://feeds.bbci.co.uk/persian/rss.xml"),
        new("BBC Urdu", "Urdu", "United Kingdom", "https://feeds.bbci.co.uk/urdu/rss.xml"),
        new("BBC Hindi", "Hindi", "United Kingdom", "https://feeds.bbci.co.uk/hindi/rss.xml"),
        new("BBC Chinese", "Chinese", "United Kingdom", "https://feeds.bbci.co.uk/zhongwen/simp/rss.xml"),
        new("NHK", "Japanese", "Japan", "https://www3.nhk.or.jp/rss/news/cat6.xml"),
        new("BBC Japanese", "Japanese", "United Kingdom", "https://feeds.bbci.co.uk/japanese/rss.xml"),
        new("Yonhap", "Korean", "South Korea", "https://www.yna.co.kr/rss/news.xml"),
        new("BBC Korean", "Korean", "United Kingdom", "https://feeds.bbci.co.uk/korean/rss.xml"),
        new("BBC Vietnamese", "Vietnamese", "United Kingdom", "https://feeds.bbci.co.uk/vietnamese/rss.xml"),
        new("BBC Indonesia", "Indonesian", "United Kingdom", "https://feeds.bbci.co.uk/indonesia/rss.xml"),
        new("BBC News Brasil", "Portuguese", "United Kingdom", "https://feeds.bbci.co.uk/portuguese/rss.xml"),
        new("BBC Swahili", "Swahili", "United Kingdom", "https://feeds.bbci.co.uk/swahili/rss.xml"),
        // Regional Outlets: Middle East & North Africa
        new("The National", "English", "United Arab Emirates", "https://www.thenationalnews.com/arc/outboundfeeds/rss/"),
        new("Ahram Online", "English", "Egypt", "https://english.ahram.org.eg/rss/World.aspx"),
        new("Roya News", "English", "Jordan", "https://en.royanews.tv/rss/world"),
        // Regional Outlets: Asia-Pacific & South Asia
        new("Focus Taiwan", "English", "Taiwan", "https://focustaiwan.tw/rss/all.xml"),
        new("Taipei Times", "English", "Taiwan", "https://www.taipeitimes.com/xml/index.rss"),
        new("The Diplomat", "English", International, "https://thediplomat.com/feed/"),
        new("Bernama", "English", "Malaysia", "https://www.bernama.com/en/rss/general.php"),
        new("Philippine News Agency", "English", "Philippines", "https://www.pna.gov.ph/rss/national"),
        // Regional Outlets: Europe & Americas
        new("EFE", "Spanish", "Spain", "https://efe.com/feed/"),
        new("PAP", "English", "Poland", "https://www.pap.pl/en/rss.xml"),
        new("Civil.ge", "English", "Georgia", "https://civil.ge/feed"),
        new("Balkan Insight", "English", "Bosnia and Herzegovina", "https://balkaninsight.com/feed/"),
        new("El Tiempo", "Spanish", "Colombia", "https://www.eltiempo.com/rss/mundo.xml"),
        new("El Universal", "Spanish", "Mexico", "https://www.eluniversal.com.mx/rss/mundo.xml"),
        new("MercoPress", "English", "Uruguay", "https://en.mercopress.com/rss/"),
    ];

    // Bytes rather than a string, so the XML declaration picks the encoding (some feeds are
    // ISO-8859-1). A DOCTYPE is skipped, not fetched. Dates later than now are taken as now.
    public static IReadOnlyList<WorldArticle> Parse(Stream xml, NewsOutlet outlet, DateTimeOffset now)
    {
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var articles = new List<WorldArticle>();
        foreach (var item in XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            var link = Link(item);
            var title = ArticleNormalizer.CleanTitle(Child(item, "title")?.Value ?? string.Empty);
            if (link is null || title.Length == 0) continue;
            var published = ParseDate(Child(item, "pubDate", "date", "published", "updated")?.Value);
            var seenAt = published is { } at && at < now ? at : now;
            var snippet = ArticleNormalizer.CleanSnippet(Child(item, "description", "summary", "content")?.Value, title);
            articles.Add(new WorldArticle(ArticleNormalizer.IdFor(link), link, title, outlet.Name, outlet.Language, outlet.Country,
                seenAt, outlet.Feed, Snippet: snippet));
        }
        return articles;
    }

    // RFC 822 as feeds really write it: the weekday is often wrong or missing, the zone is a name
    // (GMT, EDT), "Z" or "+0300". ISO 8601 (dc:date, Atom) goes to the normal parser.
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var rfc = Rfc822().Match(value.Trim());
        if (rfc.Success)
        {
            var zone = rfc.Groups["zone"].Value.ToUpperInvariant();
            TimeSpan? offset = zone switch
            {
                "" or "GMT" or "UT" or "UTC" or "Z" => TimeSpan.Zero,
                "EST" => TimeSpan.FromHours(-5), "EDT" => TimeSpan.FromHours(-4),
                "CST" => TimeSpan.FromHours(-6), "CDT" => TimeSpan.FromHours(-5),
                "MST" => TimeSpan.FromHours(-7), "MDT" => TimeSpan.FromHours(-6),
                "PST" => TimeSpan.FromHours(-8), "PDT" => TimeSpan.FromHours(-7),
                _ when zone.Length == 5 && zone[0] is '+' or '-' && int.TryParse(zone[1..], out var hhmm) =>
                    (zone[0] == '-' ? -1 : 1) * new TimeSpan(hhmm / 100, hhmm % 100, 0),
                _ => null
            };
            var time = rfc.Groups["time"].Value;
            if (offset is not null && DateTime.TryParseExact($"{rfc.Groups["date"].Value} {(time.Length <= 5 ? time + ":00" : time)}",
                    "d MMM yyyy H:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                return new DateTimeOffset(local, offset.Value);
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    // RSS puts the URL in <link>'s text; Atom in href, where rel="alternate" (or no rel) is the page.
    private static string? Link(XElement item)
    {
        var links = item.Elements().Where(e => e.Name.LocalName == "link").ToList();
        var url = links.Select(l => l.Value.Trim()).FirstOrDefault(v => v.Length > 0)
            ?? links.Where(l => (string?)l.Attribute("rel") is null or "alternate").Select(l => (string?)l.Attribute("href")).FirstOrDefault();
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    private static XElement? Child(XElement item, params string[] names) =>
        names.Select(name => item.Elements().FirstOrDefault(e => e.Name.LocalName == name && e.Value.Trim().Length > 0))
            .FirstOrDefault(e => e is not null);

    [GeneratedRegex(@"^(?:[A-Za-z]{3},?\s*)?(?<date>\d{1,2}\s+[A-Za-z]{3}\s+\d{4})\s+(?<time>\d{1,2}:\d{2}(?::\d{2})?)\s*(?<zone>[A-Za-z]{1,3}|[+-]\d{4})?$")]
    private static partial Regex Rfc822();
}
