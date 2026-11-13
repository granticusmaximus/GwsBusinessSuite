using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// How much a story can be trusted (World News trust plan, Phase 1, 13-11-26): noise kept out,
// sources counted by independence rather than by outlet, and one honest label per story.

// 1.1: press releases, SEO listicles and service pages are not news; they are dropped at ingest so
// they never pad a story's coverage.
public static partial class NewsNoise
{
    private static readonly string[] PressReleaseHosts =
    [
        "prnewswire.com", "globenewswire.com", "businesswire.com", "accesswire.com", "newsfilecorp.com",
        "einpresswire.com", "openpr.com", "prweb.com", "newswire.com", "issuewire.com", "send2press.com",
    ];

    private static readonly string[] PressReleaseOutlets =
    [
        "PR Newswire", "GlobeNewswire", "Business Wire", "ACCESSWIRE", "Newsfile", "EIN Presswire", "openPR", "PRWeb",
    ];

    public static bool IsNoise(WorldArticle article) => Reason(article) is not null;

    // Why the report isn't news, or null when it is.
    public static string? Reason(WorldArticle article)
    {
        var host = Host(article.Url);
        if (host is not null && PressReleaseHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal))) return "press release";
        if (PressReleaseOutlets.Any(o => article.Outlet.Contains(o, StringComparison.OrdinalIgnoreCase))) return "press release";
        if (Listicle().IsMatch(article.Title)) return "listicle";
        if (Service().IsMatch(article.Title)) return "service page";
        return null;
    }

    private static string? Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    // "Top 10 ...", "The 15 best ...", "7 ways to ...", "21 things ...".
    [GeneratedRegex(@"^(the\s+)?(top\s+\d+|\d+\s+(best|ways|things|reasons|tips|tricks|places|foods|signs)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Listicle();

    // Horoscopes, lottery numbers, deals, where-to-watch and live-stream pages.
    [GeneratedRegex(@"\b(horoscope|lottery results|winning numbers|promo code|coupon|best deals|deal of the day|where to watch|how to watch|live stream(ing)? free)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Service();
}

// One independent origin of a story's reports and the outlets that carried it.
public sealed record SourceOrigin(string Name, string Kind, IReadOnlyList<string> Outlets);

public sealed record StoryIndependence(int Outlets, IReadOnlyList<SourceOrigin> Origins)
{
    public int Independent => Origins.Count;
}

// 1.2: 26 outlets running the same Reuters copy are one source, not 26. A report's origin is the
// wire agency it credits, else the group that owns its outlet, else the outlet itself; reports with
// the same text (EventDocument's copy key) share the first one's origin.
public static partial class SourceIndependence
{
    private static readonly (string Name, Regex Credit)[] Wires =
    [
        ("Reuters", ReutersCredit()),
        ("Associated Press", ApCredit()),
        ("AFP", AfpCredit()),
        ("dpa", DpaCredit()),
        ("EFE", EfeCredit()),
        ("ANSA", AnsaCredit()),
    ];

    // Outlets one company or government controls, so their reports aren't independent of each other.
    private static readonly Dictionary<string, string> Owners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Wall Street Journal"] = "News Corp", ["New York Post"] = "News Corp", ["The Times"] = "News Corp", ["The Australian"] = "News Corp", ["Sky News Australia"] = "News Corp",
        ["Fox News"] = "Fox Corporation", ["Fox Business"] = "Fox Corporation",
        ["CNN"] = "Warner Bros. Discovery",
        ["NBC News"] = "Comcast (NBCUniversal)", ["MSNBC"] = "Comcast (NBCUniversal)", ["CNBC"] = "Comcast (NBCUniversal)", ["Sky News"] = "Comcast (NBCUniversal)",
        ["ABC News (US)"] = "Disney", ["CBS News"] = "Paramount",
        ["The Washington Post"] = "Nash Holdings",
        ["The Guardian"] = "Guardian Media Group", ["The Observer"] = "Guardian Media Group",
        ["Daily Mail"] = "DMG Media", ["Mail Online"] = "DMG Media", ["The i Paper"] = "DMG Media",
        ["Der Spiegel"] = "Spiegel-Verlag", ["Bild"] = "Axel Springer", ["Die Welt"] = "Axel Springer", ["Politico"] = "Axel Springer", ["Business Insider"] = "Axel Springer",
        ["Le Monde"] = "Le Monde Group", ["Le Figaro"] = "Dassault",
        ["TASS"] = "Russian state", ["RIA Novosti"] = "Russian state", ["РИА Новости"] = "Russian state", ["RT"] = "Russian state", ["RT en Español"] = "Russian state", ["Sputnik"] = "Russian state", ["Sputnik Mundo"] = "Russian state",
        ["Xinhua"] = "Chinese state", ["Xinhua News Agency"] = "Chinese state", ["新华网"] = "Chinese state", ["新华社"] = "Chinese state", ["People's Daily"] = "Chinese state", ["人民网"] = "Chinese state",
        ["人民日报"] = "Chinese state", ["CGTN"] = "Chinese state", ["CCTV"] = "Chinese state", ["央视新闻"] = "Chinese state", ["China Daily"] = "Chinese state", ["Global Times"] = "Chinese state", ["环球时报"] = "Chinese state",
        ["IRNA"] = "Iranian state", ["Press TV"] = "Iranian state",
        ["Anadolu Agency"] = "Turkish state", ["Anadolu Ajansı"] = "Turkish state", ["TRT World"] = "Turkish state", ["TRT Haber"] = "Turkish state",
    };

    public static StoryIndependence Assess(IReadOnlyList<WorldArticle> articles)
    {
        // Fact-checkers rate claims rather than report events, so they aren't counted as sources.
        var news = articles.Where(article => !NewsNoise.IsNoise(article) && !FactChecks.IsFactChecker(article.Outlet)
            && article.Outlet != WikipediaCurrentEvents.Outlet).ToList();
        // Each report's origin (a copy takes the first copy's), then one origin per outlet: its own
        // (or its owner's) if any report is its own work, else the wire it only repeated. So the
        // number of independent sources can never exceed the number of outlets.
        var originByCopy = new Dictionary<string, (string Name, string Kind)>(StringComparer.Ordinal);
        var originsByOutlet = new Dictionary<string, List<(string Name, string Kind)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var article in news.OrderBy(a => a.SeenAt))
        {
            var copy = CopyKey(article);
            if (!originByCopy.TryGetValue(copy, out var origin))
            {
                origin = OriginOf(article);
                originByCopy[copy] = origin;
            }
            if (!originsByOutlet.TryGetValue(article.Outlet, out var list)) originsByOutlet[article.Outlet] = list = [];
            list.Add(origin);
        }
        var outletsByOrigin = new Dictionary<string, (string Kind, HashSet<string> Outlets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (outlet, reportOrigins) in originsByOutlet)
        {
            var own = OwnOrigin(outlet);
            var chosen = reportOrigins.Any(o => string.Equals(o.Name, own.Name, StringComparison.OrdinalIgnoreCase)) ? own : reportOrigins[0];
            if (!outletsByOrigin.TryGetValue(chosen.Name, out var entry))
            {
                entry = (chosen.Kind, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                outletsByOrigin[chosen.Name] = entry;
            }
            entry.Outlets.Add(outlet);
        }
        var origins = outletsByOrigin
            .Select(pair => new SourceOrigin(pair.Key, pair.Value.Kind, pair.Value.Outlets.Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(origin => origin.Outlets.Count).ThenBy(origin => origin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StoryIndependence(news.Select(a => a.Outlet).Distinct(StringComparer.OrdinalIgnoreCase).Count(), origins);
    }

    // Owners found on Wikidata (OwnershipGraph), set by the host when it loads or refreshes them; the
    // hand list wins where both name an outlet.
    public static volatile IReadOnlyDictionary<string, string> ImportedOwners = new Dictionary<string, string>();

    private static string? OwnerOf(string outlet) =>
        Owners.TryGetValue(outlet.Trim(), out var owner) ? owner : ImportedOwners.TryGetValue(outlet.Trim(), out var imported) ? imported : null;

    private static (string Name, string Kind) OwnOrigin(string outlet) =>
        OwnerOf(outlet) is { } owner ? (owner, "owner") : (outlet, "outlet");

    public static (string Name, string Kind) OriginOf(WorldArticle article)
    {
        var text = article.Title + " " + article.Snippet;
        foreach (var (name, credit) in Wires)
        {
            if (credit.IsMatch(text)) return (name, "wire");
        }
        if (OwnerOf(article.Outlet) is { } owner) return (owner, "owner");
        return (article.Outlet, "outlet");
    }

    private static string CopyKey(WorldArticle a) =>
        a.Snippet is { Length: >= 80 } snippet ? "s:" + Letters(snippet) : "t:" + Letters(a.Title);

    private static string Letters(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // "told Reuters", "citing Reuters", "Reuters reported" too (A2, 13-11-26): the report rests on Reuters.
    [GeneratedRegex(@"\(Reuters\)|\bReuters\s*[-–—]|\|\s*Reuters\b|via Reuters\b|\b(told|according to|citing|cited by|reported by)\s+Reuters\b|\bReuters\s+(reported|reports)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReutersCredit();

    [GeneratedRegex(@"\(AP\)|\bAP\s*[-–—]\s|\(Associated Press\)|\bAssociated Press\s*[-–—]|via AP\b|\b(?i:told|according to|citing|reported by)\s+(?i:the\s+)?(AP|Associated Press)\b|\b[Tt]he Associated Press\s+(reported|reports)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ApCredit();

    [GeneratedRegex(@"\(AFP\)|\bAFP\s*[-–—]\s|via AFP\b|Agence France-Presse\s*[-–—]|\b(?i:told|according to|citing|reported by)\s+AFP\b|\bAFP\s+(reported|reports)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AfpCredit();

    [GeneratedRegex(@"\(dpa\)|\bdpa\s*[-–—]\s", RegexOptions.CultureInvariant)]
    private static partial Regex DpaCredit();

    [GeneratedRegex(@"\(EFE\)|\bEFE\s*[-–—]\s", RegexOptions.CultureInvariant)]
    private static partial Regex EfeCredit();

    [GeneratedRegex(@"\(ANSA\)|\bANSA\s*[-–—]\s", RegexOptions.CultureInvariant)]
    private static partial Regex AnsaCredit();
}

public enum StoryConfidenceLevel
{
    SingleSource,
    Reported,
    Confirmed,
    Disputed,
}

public sealed record StoryConfidence(StoryConfidenceLevel Level, string Reason)
{
    public string Label => Level switch
    {
        StoryConfidenceLevel.Confirmed => "Confirmed",
        StoryConfidenceLevel.Disputed => "Disputed",
        StoryConfidenceLevel.Reported => "Reported",
        _ => "Single source",
    };
}

// 1.3: one honest label per story. Disputed wins (conflicting figures, or a fact-check that says
// the claim is false); Confirmed needs a primary source or three independent ones; Reported is two;
// Single source is one, however many outlets repeated it.
public static class StoryConfidenceRules
{
    // The version of every rule behind a label (independence, confidence, the gate). Each label is
    // stamped with it (A7), so a later change of rules can be traced and old labels re-scored.
    public const string RulesVersion = "2026-11-13.1";
    public const int ConfirmedIndependent = 3;
    // A4: a story still on one source this long after its first report is called unconfirmed.
    public static readonly TimeSpan RumourClock = TimeSpan.FromHours(12);

    public static StoryConfidence Assess(StoryIndependence independence, string? primarySource = null, int disputedFigures = 0, string? factCheckVerdict = null,
        DateTimeOffset? firstSeen = null, DateTimeOffset? now = null)
    {
        if (factCheckVerdict is not null)
            return new(StoryConfidenceLevel.Disputed, $"A fact-check rates it {factCheckVerdict}.");
        if (disputedFigures > 0)
            return new(StoryConfidenceLevel.Disputed, disputedFigures == 1 ? "Outlets give conflicting figures." : $"Outlets give conflicting figures on {disputedFigures} points.");
        if (primarySource is not null)
            return new(StoryConfidenceLevel.Confirmed, $"Confirmed by {primarySource}.");
        return independence.Independent switch
        {
            >= ConfirmedIndependent => new(StoryConfidenceLevel.Confirmed, $"{independence.Independent} independent sources."),
            2 => new(StoryConfidenceLevel.Reported, "2 independent sources."),
            _ when firstSeen is { } first && now is { } at && at - first >= RumourClock =>
                new(StoryConfidenceLevel.SingleSource, $"Still one source after {(int)(at - first).TotalHours} hours: unconfirmed."),
            _ => new(StoryConfidenceLevel.SingleSource, independence.Outlets > 1
                ? $"One source, repeated by {independence.Outlets} outlets."
                : "One outlet so far."),
        };
    }

    // 1.4: an alert goes out only once a story is past a single source.
    public static bool MayAlert(StoryIndependence independence, string? primarySource = null) =>
        primarySource is not null || independence.Independent >= 2;
}

// 1.8/1.9: a headline that changed after it was first seen. Kind is "correction" when the new
// headline says so, "update" for a live-story update, else "edit" (a quiet change).
public sealed record ArticleEdit(string ArticleId, string Before, string After, string Kind, DateTimeOffset SeenAt);

public static partial class ArticleEdits
{
    // Changed beyond case, spacing and punctuation.
    public static bool IsChanged(string before, string after) => Letters(before) != Letters(after) && Letters(after).Length > 0;

    public static string KindOf(string headline) =>
        Correction().IsMatch(headline) ? "correction" : Update().IsMatch(headline) ? "update" : "edit";

    private static string Letters(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"^\s*(correction|corrected|clarification|retraction)\b|\(corrected\)|\[corrected\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Correction();

    [GeneratedRegex(@"^\s*(update[d]?|live|breaking)\b[:\s]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Update();
}

// What the story page shows about trust: independent sources, the label, the edits, and what
// confirmed or disputed it.
public sealed record StoryTrustSummary(StoryIndependence Independence, StoryConfidence Confidence, IReadOnlyList<ArticleEdit> Edits,
    string? PrimarySource, string? FactCheckVerdict, IReadOnlyList<FactCheck>? FactChecks = null, IReadOnlyList<CitedSource>? CitedSources = null,
    string RulesVersion = StoryConfidenceRules.RulesVersion);

// 1.7: a fact-checker's report in a story. Verdict is the rating its headline or description gives
// ("False", "Misleading"), or null when it gives none (FactCheck.org rarely does).
public sealed record FactCheck(string Outlet, string Title, string Url, string? Verdict);

public static partial class FactChecks
{
    public static readonly IReadOnlySet<string> Outlets = OutletFeeds.Outlets.Where(outlet => outlet.IsFactCheck)
        .Select(outlet => outlet.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsFactChecker(string outlet) => Outlets.Contains(outlet);

    public static IReadOnlyList<FactCheck> In(IEnumerable<WorldArticle> articles) =>
        articles.Where(article => IsFactChecker(article.Outlet))
            .Select(article => new FactCheck(article.Outlet, article.Title, article.Url, VerdictOf(article.Title + " " + article.Snippet)))
            .ToList();

    // The first "this is wrong" verdict, which makes the story Disputed.
    public static string? DisputingVerdict(IEnumerable<FactCheck> checks) => checks.Select(check => check.Verdict).FirstOrDefault(verdict => verdict is not null);

    public static string? VerdictOf(string text)
    {
        var match = Verdict().Match(text);
        return match.Success ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Value.ToLowerInvariant()) : null;
    }

    [GeneratedRegex(@"\b(pants on fire|mostly false|false|fake|fabricated|misleading|not true|no evidence|unproven|miscaptioned|altered|satire|out of context)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Verdict();
}

// 1.10: what a person types, as an FTS5 query. Plain words must all appear (any order); "quoted
// phrases", AND, OR, NOT and prefix* work as in a search engine. Anything FTS5 would reject
// (stray quotes, column filters, other operators) is quoted away, so a search never errors.
public static partial class FullTextQuery
{
    public static string? From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = new List<string>();
        foreach (Match match in Token().Matches(text))
        {
            var token = match.Value;
            if (token.StartsWith('"'))
            {
                var phrase = token.Trim('"').Replace("\"", string.Empty).Trim();
                if (phrase.Length > 0) parts.Add($"\"{phrase}\"");
            }
            else if (token is "AND" or "OR" or "NOT")
            {
                if (parts.Count > 0 && parts[^1] is not ("AND" or "OR" or "NOT")) parts.Add(token);
            }
            else
            {
                var prefix = token.EndsWith('*');
                var word = new string(token.Where(char.IsLetterOrDigit).ToArray());
                if (word.Length > 0) parts.Add($"\"{word}\"{(prefix ? "*" : string.Empty)}");
            }
        }
        while (parts.Count > 0 && parts[^1] is "AND" or "OR" or "NOT") parts.RemoveAt(parts.Count - 1);
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    [GeneratedRegex("\"[^\"]*\"?|\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}

// 1.12: a country whose coverage has fallen far below its normal level, which can mean an internet
// shutdown or a press blackout as much as a quiet day. Recent is the last six hours' reports;
// Usual is the average six hours over the week before. Only countries with steady coverage count.
public sealed record CoverageSilence(string Country, int Recent, double Usual)
{
    public int PercentOfUsual => Usual <= 0 ? 0 : (int)Math.Round(100 * Recent / Usual);
}

public static class CoverageSilenceRules
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);
    public static readonly TimeSpan Baseline = TimeSpan.FromDays(7);
    public const double MinimumUsual = 20;
    public const double SilentShare = 0.2;

    // hourly: (country, hour start, reports). Countries that went quiet, the quietest first.
    public static IReadOnlyList<CoverageSilence> Find(IEnumerable<(string Country, DateTimeOffset Hour, int Reports)> hourly, DateTimeOffset now)
    {
        var recentStart = now - Window;
        var baselineStart = recentStart - Baseline;
        var windows = Baseline / Window;
        return hourly.GroupBy(row => row.Country, StringComparer.OrdinalIgnoreCase)
            .Select(country => new CoverageSilence(country.Key,
                country.Where(row => row.Hour >= recentStart).Sum(row => row.Reports),
                country.Where(row => row.Hour >= baselineStart && row.Hour < recentStart).Sum(row => row.Reports) / windows))
            .Where(silence => silence.Usual >= MinimumUsual && silence.Recent <= silence.Usual * SilentShare)
            .OrderBy(silence => silence.Recent / silence.Usual)
            .ToList();
    }
}

// 1.13: an official record of the event itself: a USGS earthquake, a GDACS/NHC disaster, a WHO
// outbreak notice. When a story's type, place and time match one, it is Confirmed by that source.
public sealed record PrimarySignal(string Source, string Kind, string Title, double Latitude, double Longitude, DateTimeOffset? ObservedAt, string Url);

public static partial class PrimarySources
{
    // How far apart a story's place and the record may be: close for a story placed at a city, wide
    // for one placed by country (a country's centre can be far from where it happened).
    public const double CityKilometres = 300;
    public const double CountryKilometres = 1500;
    public static readonly TimeSpan MaxGap = TimeSpan.FromHours(48);

    // WHO's own notice in the story's reports is primary by itself.
    public static string? InReports(IEnumerable<WorldArticle> articles) =>
        articles.Any(article => article.Outlet == WhoOutbreakNews.Outlet) ? "the World Health Organization" : null;

    // The hazard kind a headline is about, in the hazard layers' terms, or null.
    public static string? KindOf(string headline)
    {
        if (Quake().IsMatch(headline)) return "earthquake";
        if (Storm().IsMatch(headline)) return "cyclone";
        if (Flood().IsMatch(headline)) return "flood";
        if (Volcano().IsMatch(headline)) return "volcano";
        if (Fire().IsMatch(headline)) return "wildfire";
        return null;
    }

    public static PrimarySignal? Match(string headline, double latitude, double longitude, DateTimeOffset firstSeen, IEnumerable<PrimarySignal> signals,
        double maxKilometres = CityKilometres)
    {
        if (KindOf(headline) is not { } kind) return null;
        return signals
            .Where(signal => signal.Kind.Contains(kind, StringComparison.OrdinalIgnoreCase)
                || (kind == "cyclone" && signal.Kind.Contains("storm", StringComparison.OrdinalIgnoreCase)))
            .Where(signal => signal.ObservedAt is not { } at || (at - firstSeen).Duration() <= MaxGap)
            .Select(signal => (Signal: signal, Distance: Kilometres(latitude, longitude, signal.Latitude, signal.Longitude)))
            .Where(candidate => candidate.Distance <= maxKilometres)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Signal)
            .FirstOrDefault();
    }

    public static double Kilometres(double lat1, double lon1, double lat2, double lon2)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    [GeneratedRegex(@"\b(earthquakes?|quakes?|tremors?|seismic)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Quake();

    [GeneratedRegex(@"\b(cyclones?|hurricanes?|typhoons?|tropical storms?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Storm();

    [GeneratedRegex(@"\b(floods?|flooding|flash floods?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Flood();

    [GeneratedRegex(@"\b(volcano(es)?|volcanic|eruptions?|erupts?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Volcano();

    [GeneratedRegex(@"\b(wildfires?|bushfires?|forest fires?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Fire();
}

// 1.14: a surging story is searched for directly (GDELT, every language), so its coverage fills
// in within minutes. The query is the headline's distinctive words, at most five.
public static class FollowUpQueries
{
    public const int MaxPerRead = 3;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "against", "amid", "among", "says", "said", "over", "under", "with", "from", "into", "that", "this",
        "their", "there", "they", "were", "will", "would", "could", "what", "when", "where", "which", "while", "have", "has", "been",
        "more", "most", "than", "then", "also", "just", "news", "live", "update", "updates", "latest", "breaking", "report", "reports",
        "new", "first", "people", "year", "years", "week", "today",
    };

    public static string? From(string headline)
    {
        var words = headline.Split([' ', ',', ':', ';', '-', '–', '—', '"', '\'', '(', ')', '?', '!', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length >= 4 && !Common.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        return words.Count >= 2 ? string.Join(' ', words) : null;
    }
}

// 1.15: the feeds a homepage offers. <link rel="alternate" type="application/rss+xml|atom+xml"> in
// its head first, then the usual places newsrooms put them.
public static partial class FeedDiscovery
{
    public static readonly string[] CommonPaths = ["/feed", "/rss", "/rss.xml", "/feed.xml", "/atom.xml", "/news-sitemap.xml", "/sitemap-news.xml", "/sitemaps/news.xml"];

    public static IReadOnlyList<string> Candidates(string html, Uri page)
    {
        var found = new List<string>();
        foreach (Match link in LinkTag().Matches(html))
        {
            var tag = link.Value;
            if (!AlternateFeed().IsMatch(tag)) continue;
            var href = Href().Match(tag);
            if (href.Success && Uri.TryCreate(page, System.Net.WebUtility.HtmlDecode(href.Groups[1].Value), out var url) && url.Scheme is "https" or "http")
                found.Add(url.AbsoluteUri);
        }
        found.AddRange(CommonPaths.Select(path => new Uri(page, path).AbsoluteUri));
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"rel\s*=\s*[""']?alternate[""']?[^>]*type\s*=\s*[""']?application/(rss|atom)\+xml|type\s*=\s*[""']?application/(rss|atom)\+xml[^>]*rel\s*=\s*[""']?alternate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlternateFeed();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Href();
}

// 1.18: an outlet's record over the last week, built from evidence: how much it filed, how often it
// was first to a story, and how often its headlines changed after publication (corrections apart).
public sealed record OutletRecord(string Outlet, int Reports, int FirstReports, int HeadlineChanges, int Corrections)
{
    public double FirstShare => Reports == 0 ? 0 : (double)FirstReports / Reports;
}

// The sources page's view of the crawler (World News trust plan 1.15-1.18).
public sealed record CrawlFeed(NewsOutlet Outlet, FeedHealth? Health, bool Paused, bool Added)
{
    public string State => Paused ? "paused"
        : Health is null ? "not read yet"
        : Health.LastStatus is { } status && FeedSchedule.IsRefusal(status) ? "refused"
        : Health.Failures > 0 ? "failing"
        : Health.SkipUntil is not null ? "quiet"
        : "ok";
}

public sealed record CrawlRegion(string Region, int Outlets, int Reports);

public sealed record CrawlDashboard(int ReportsLastHour, int ReportsLastDay, int NewStoriesLastDay, IReadOnlyList<CrawlFeed> Feeds,
    IReadOnlyList<CrawlRegion> Regions, IReadOnlyList<OutletRecord> Records, IReadOnlyList<CoverageSilence> Silences);

public sealed record FeedCandidate(string Url, string Kind, int Items, string? Sample);

// 1.19: Wikipedia's Current events portal, an early signal (its editors often log events before
// the wires spread). Each entry becomes a report from "Wikipedia Current events" linking the source
// it cites; it isn't counted as an independent source (it summarises others).
public static partial class WikipediaCurrentEvents
{
    public const string Outlet = "Wikipedia Current events";
    public const string Url = "https://en.wikipedia.org/api/rest_v1/page/html/Portal%3ACurrent_events";

    public static IReadOnlyList<WorldArticle> Parse(string html, DateTimeOffset now)
    {
        var articles = new List<WorldArticle>();
        foreach (var day in html.Split("current-events-main vevent", StringSplitOptions.None).Skip(1))
        {
            var date = DayStart().Match(day) is { Success: true } m && DateTimeOffset.TryParse(m.Groups[1].Value + "T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : now;
            foreach (Match item in LeafItem().Matches(day))
            {
                var body = item.Groups[1].Value;
                var source = ExternalLink().Matches(body).Select(link => System.Net.WebUtility.HtmlDecode(link.Groups[1].Value))
                    .FirstOrDefault(href => Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                        && !uri.Host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase));
                if (source is null) continue;
                var text = SpaceBeforePunctuation().Replace(Spaces().Replace(System.Net.WebUtility.HtmlDecode(Tags().Replace(SourceCredit().Replace(body, " "), " ")), " "), "$1").Trim();
                if (text.Length < 20) continue;
                var title = text.Length > 300 ? text[..300].TrimEnd() + "…" : text;
                articles.Add(new WorldArticle(ArticleNormalizer.IdFor(source), source, title, Outlet, "English", OutletFeeds.International,
                    date < now ? date : now, "wikipedia:current-events"));
            }
        }
        return articles;
    }

    [GeneratedRegex(@"dtstart[^>]*>(\d{4}-\d{2}-\d{2})<", RegexOptions.CultureInvariant)]
    private static partial Regex DayStart();

    // An entry with no list nested inside it, citing at least one outside source.
    [GeneratedRegex(@"<li>((?:(?!<li>|<ul>|</li>).)*?rel=""mw:ExtLink[^""]*""(?:(?!<li>|<ul>).)*?)</li>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex LeafItem();

    [GeneratedRegex(@"rel=""mw:ExtLink[^""]*""[^>]*href=""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalLink();

    // "(Reuters)" and the like at the end of an entry.
    [GeneratedRegex(@"<a[^>]*rel=""mw:ExtLink[^""]*""[^>]*>.*?</a>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SourceCredit();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([,.;:)])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuation();
}

// A1/A2 (13-11-26): who the coverage relies on. "According to the health ministry", "police said
// in a statement": the sources reports quote, counted by how many outlets lean on each. Thirty
// outlets quoting one ministry are one source for that claim, however independent they are.
public sealed record CitedSource(string Name, int Outlets, string FirstOutlet, DateTimeOffset FirstSeen);

public static partial class CitedSources
{
    public static IReadOnlyList<CitedSource> Find(IEnumerable<WorldArticle> articles, int max = 8)
    {
        var bySource = new Dictionary<string, (HashSet<string> Outlets, string First, DateTimeOffset At)>(StringComparer.OrdinalIgnoreCase);
        foreach (var article in articles.OrderBy(a => a.SeenAt))
        {
            foreach (var name in NamesIn(article.Title + ". " + article.Snippet))
            {
                if (!bySource.TryGetValue(name, out var entry)) bySource[name] = entry = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), article.Outlet, article.SeenAt);
                entry.Outlets.Add(article.Outlet);
            }
        }
        return bySource.Select(pair => new CitedSource(pair.Key, pair.Value.Outlets.Count, pair.Value.First, pair.Value.At))
            .OrderByDescending(source => source.Outlets).ThenBy(source => source.FirstSeen)
            .Take(max).ToList();
    }

    public static IEnumerable<string> NamesIn(string text)
    {
        foreach (Match match in AccordingTo().Matches(text)) if (Clean(match.Groups["who"].Value) is { } name) yield return name;
        foreach (Match match in Statement().Matches(text)) if (Clean(match.Groups["who"].Value) is { } name) yield return name;
    }

    // "the Health Ministry" -> "health ministry"; nothing for pronouns and fillers.
    private static string? Clean(string raw)
    {
        var name = Regex.Replace(raw.Trim(), @"^(the|a|an|local|senior|top)\s+", string.Empty, RegexOptions.IgnoreCase).Trim().TrimEnd('.', ',', ';');
        if (name.Length < 2 || name.Length > 60) return null;
        if (Filler.Contains(name)) return null;
        return name.ToLowerInvariant();
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "he", "she", "they", "it", "we", "him", "her", "them", "reports", "a report", "report", "data", "figures", "estimates", "sources", "media reports", "some", "many",
    };

    // "according to [the] X" where X is a capitalised name or a known role.
    [GeneratedRegex(@"\baccording to (?<who>(?:the\s+)?(?:[A-Z][\w'’&.-]*(?:\s+(?:of|for|and)\s+|\s+)?){1,6}|(?:the\s+)?(?:local\s+)?(?:police|officials|authorities|witnesses|residents|rescuers|doctors|the military|the army|(?:health|interior|foreign|defen[cs]e|finance)\s+ministry|government))", RegexOptions.CultureInvariant)]
    private static partial Regex AccordingTo();

    // "[The] X said in a statement".
    [GeneratedRegex(@"(?<who>(?:[Tt]he\s+)?(?:[A-Z][\w'’&.-]*\s+){1,5}|(?:[Tt]he\s+)?(?:police|officials|authorities|military|army|ministry|government)\s+)said in a statement", RegexOptions.CultureInvariant)]
    private static partial Regex Statement();
}

// A7: a story's label at one moment, stamped with the rules that made it. Hash chains each snapshot
// to the one before it (A8): rewriting or deleting any snapshot breaks every hash after it.
public sealed record LabelSnapshot(long Id, string EventId, string Level, string Reason, int Independent, int Outlets, string RulesVersion,
    DateTimeOffset TakenAt, string PreviousHash, string Hash);

public static class LabelLedger
{
    public const string Genesis = "genesis";

    public static string HashOf(string previousHash, string eventId, string level, string reason, int independent, int outlets, string rulesVersion, DateTimeOffset takenAt)
    {
        var canonical = string.Join('\u001f', previousHash, eventId, level, reason, independent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            outlets.ToString(System.Globalization.CultureInfo.InvariantCulture), rulesVersion, takenAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    // Null when every snapshot hashes to what it says and points at the one before it; otherwise the
    // id of the first one that doesn't.
    public static long? FirstBroken(IEnumerable<LabelSnapshot> snapshotsInOrder)
    {
        var previous = Genesis;
        foreach (var snapshot in snapshotsInOrder)
        {
            if (snapshot.PreviousHash != previous
                || snapshot.Hash != HashOf(snapshot.PreviousHash, snapshot.EventId, snapshot.Level, snapshot.Reason, snapshot.Independent, snapshot.Outlets, snapshot.RulesVersion, snapshot.TakenAt))
                return snapshot.Id;
            previous = snapshot.Hash;
        }
        return null;
    }
}

// The label a story would get now from its reports alone (the snapshots' rule: no network checks),
// so every snapshot is reproducible from the stored reports.
public static class StoryLabel
{
    public static StoryConfidence For(IReadOnlyList<WorldArticle> articles, DateTimeOffset firstSeen, DateTimeOffset now, out StoryIndependence independence)
    {
        independence = SourceIndependence.Assess(articles);
        return StoryConfidenceRules.Assess(independence, PrimarySources.InReports(articles), CoverageClaims.For(articles).Count(f => f.Disputed),
            FactChecks.DisputingVerdict(FactChecks.In(articles)), firstSeen, now);
    }
}

// A7: how well first labels held up. For each first label: how many stories, how many still carry
// it or a stronger one, how many became Disputed, and how many had a correction among their reports.
public sealed record CalibrationRow(string Level, int Stories, int Held, int BecameDisputed, int Corrected)
{
    public int HeldPercent => Stories == 0 ? 0 : (int)Math.Round(100.0 * Held / Stories);
}

public static class Calibration
{
    private static int Rank(string level) => level switch { "Confirmed" => 3, "Reported" => 2, "SingleSource" => 1, _ => 0 };

    // outcomes: each story's first label, its latest label, and whether a correction appeared.
    public static IReadOnlyList<CalibrationRow> Score(IEnumerable<(string First, string Latest, bool Corrected)> outcomes) =>
        outcomes.GroupBy(o => o.First)
            .Select(group => new CalibrationRow(group.Key, group.Count(),
                group.Count(o => o.Latest != "Disputed" && Rank(o.Latest) >= Rank(o.First)),
                group.Count(o => o.Latest == "Disputed" && o.First != "Disputed"),
                group.Count(o => o.Corrected)))
            .OrderByDescending(row => Rank(row.Level))
            .ToList();
}

// A9: a challenge to a story's label, and what came of it.
public sealed record LabelChallenge(string Id, string EventId, string Username, string Note, string? EvidenceUrl, DateTimeOffset CreatedAt,
    string Status, string? ResolvedBy, string? Resolution, DateTimeOffset? ResolvedAt);

public static class LabelChallengeStatuses
{
    public const string Open = "open";
    public const string Upheld = "upheld";
    public const string Rejected = "rejected";
}

// A5: whose voice the coverage carries. For each region: its share of the stories (by where they
// happen), its share of the reports (by where the outlet is based), and how often a story about it
// was first reported by an outlet from it.
public sealed record BalanceRow(string Region, int Stories, double StoryShare, double ReportShare, int LedLocally)
{
    public int LocalLeadPercent => Stories == 0 ? 0 : (int)Math.Round(100.0 * LedLocally / Stories);
}

public static class CoverageBalance
{
    public static IReadOnlyList<BalanceRow> Compute(IEnumerable<(string StoryRegion, string LeadRegion)> stories, IReadOnlyDictionary<string, int> reportsByRegion)
    {
        var list = stories.ToList();
        var totalReports = Math.Max(1, reportsByRegion.Values.Sum());
        var totalStories = Math.Max(1, list.Count);
        return list.Select(s => s.StoryRegion).Concat(reportsByRegion.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(region =>
            {
                var about = list.Where(s => string.Equals(s.StoryRegion, region, StringComparison.OrdinalIgnoreCase)).ToList();
                return new BalanceRow(region, about.Count, (double)about.Count / totalStories, (double)reportsByRegion.GetValueOrDefault(region) / totalReports,
                    about.Count(s => string.Equals(s.LeadRegion, region, StringComparison.OrdinalIgnoreCase)));
            })
            .OrderByDescending(row => row.StoryShare)
            .ToList();
    }
}

// A6: of the events an official record says happened, how many World News surfaced, and how fast.
public sealed record RecallResult(string Source, int Records, int Found, TimeSpan? MedianDelay, IReadOnlyList<string> Missed)
{
    public int FoundPercent => Records == 0 ? 0 : (int)Math.Round(100.0 * Found / Records);
}

public static class Recall
{
    public static RecallResult Score(string source, string kind, IReadOnlyList<PrimarySignal> records,
        IReadOnlyList<(string Headline, double Latitude, double Longitude, DateTimeOffset FirstSeen)> stories)
    {
        var delays = new List<TimeSpan>();
        var missed = new List<string>();
        foreach (var record in records)
        {
            var match = stories
                .Where(story => PrimarySources.KindOf(story.Headline) == kind
                    && record.ObservedAt is { } at && story.FirstSeen >= at - TimeSpan.FromHours(1) && story.FirstSeen <= at + PrimarySources.MaxGap
                    && PrimarySources.Kilometres(story.Latitude, story.Longitude, record.Latitude, record.Longitude) <= PrimarySources.CountryKilometres)
                .OrderBy(story => story.FirstSeen)
                .Select(story => (TimeSpan?)(story.FirstSeen - record.ObservedAt!.Value))
                .FirstOrDefault();
            if (match is { } delay) delays.Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
            else missed.Add(record.Title);
        }
        delays.Sort();
        return new RecallResult(source, records.Count, delays.Count, delays.Count == 0 ? null : delays[delays.Count / 2], missed);
    }
}

// A10: the rules, in words, and every change to them.
public static class TrustMethodology
{
    public static readonly IReadOnlyList<(string Version, string Change)> Changelog =
    [
        ("2026-11-13.1", "First published rules: independence by wire credit, attribution, identical text and owner; one origin per outlet; Confirmed at 3 independent sources or an official record; Disputed on conflicting figures or a false-type fact-check; unconfirmed after 12 hours on one source; alerts need 2 independent sources or an official record."),
    ];

    public static readonly IReadOnlyList<string> Rules =
    [
        "A report's origin is the wire it credits or rests on (\"(Reuters)\", \"told AP\", \"citing AFP\"), else the group that owns its outlet, else the outlet. Reports with identical text share an origin.",
        "Each outlet counts once: as itself (or its owner) if any of its reports is its own work, otherwise as the wire it repeated. Independent sources can never outnumber outlets.",
        "Fact-checkers and Wikipedia's Current events are shown but never counted as sources; press releases, listicles and service pages are kept out entirely.",
        "Confirmed: an official record of the event (USGS, GDACS, NOAA, NASA, WHO) or three or more independent sources. Reported: two. Single source: one, however many outlets repeated it.",
        "Disputed outranks everything: outlets giving conflicting figures, or a fact-checker rating the claim false or misleading.",
        "A story still on one independent source 12 hours after its first report is labelled unconfirmed.",
        "No alert (surge, watch, watchlist) goes out for a story on one independent source.",
        "Labels come from counted evidence and these rules, never from AI. AI writes summaries only, and they are marked as such.",
    ];
}

// Everything the methodology page shows (A5-A10).
public sealed record TrustReport(DateTimeOffset At, string RulesVersion, IReadOnlyList<BalanceRow> Balance, RecallResult? Recall,
    IReadOnlyList<CalibrationRow> Calibration, int Snapshots, long? LedgerBrokenAt, IReadOnlyList<LabelChallenge> Challenges);

// A3 (13-11-26): who owns which outlet, from Wikidata (free SPARQL, no key), on top of the hand list.
// Outlets that share any current owner join one group; a group is named by its most common owner.
public static class OwnershipGraph
{
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<(string Outlet, string Owner)> rows)
    {
        var list = rows.Where(r => r.Outlet.Length > 0 && r.Owner.Length > 0).Distinct().ToList();
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && !string.Equals(p, x, StringComparison.OrdinalIgnoreCase)) x = p;
            return x;
        }
        void Union(string a, string b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var ra = Find(a);
            var rb = Find(b);
            if (!string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase)) parent[ra] = rb;
        }
        foreach (var (outlet, owner) in list) Union("o:" + outlet, "w:" + owner);
        var groups = list.GroupBy(r => Find("o:" + r.Outlet), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Owner).OrderByDescending(o => o.Count()).ThenBy(o => o.Key, StringComparer.Ordinal).First().Key,
                StringComparer.OrdinalIgnoreCase);
        // An outlet alone with its owner is still named by that owner, so its reports read as the owner's.
        return list.Select(r => r.Outlet).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(outlet => outlet, outlet => groups[Find("o:" + outlet)], StringComparer.OrdinalIgnoreCase);
    }

    // "rss.nytimes.com" -> "nytimes.com", "news.bbc.co.uk" -> "bbc.co.uk": enough to tell one outlet's site from another's.
    public static string? RootDomain(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return null;
        var host = Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri) ? uri.Host : urlOrHost;
        var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
        if (labels.Length < 2) return null;
        var secondLevel = labels[^2];
        var keep = labels.Length >= 3 && labels[^1].Length == 2 && secondLevel is "co" or "com" or "org" or "net" or "gov" or "ac" or "or" or "ne" ? 3 : 2;
        return string.Join('.', labels[^keep..]);
    }
}
