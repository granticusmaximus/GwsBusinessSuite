using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Infrastructure.Services;

// A deliberately conservative projection of World News onto Overwatch. Article.Country is the
// publishing edition/outlet country, not proof of where the event happened, so it is never used
// as a coordinate. A situation is mapped only when its own headline explicitly names a country
// we can place. The marker remains country-level and is labelled as reported news, not an
// operational incident.
public sealed record WorldNewsMapSignal(
    string SituationId,
    string Title,
    string Latest,
    string Country,
    double Latitude,
    double Longitude,
    int EventCount,
    int OutletCount,
    int ReportCount,
    DateTimeOffset LastSeen,
    string? Summary,
    IReadOnlyList<WorldNewsMapSource> Sources);

public sealed record WorldNewsMapSource(string Outlet, string Title, string Url);
public sealed record WorldNewsCountryLocation(string Country, double Latitude, double Longitude);

public sealed partial class WorldNewsMapService(WorldNewsService news)
{
    public const int MinimumOutlets = 3;
    public const int MaximumSignals = 50;

    private sealed record CountryPoint(string Name, double Latitude, double Longitude, string[] Aliases);

    private static readonly CountryPoint[] Countries =
    [
        new("United States", 39.8, -98.6, ["United States", "U.S.", "US"]),
        new("United Kingdom", 54.0, -2.0, ["United Kingdom", "Britain", "British", "UK"]),
        new("Canada", 56.1, -106.3, ["Canada", "Canadian"]),
        new("Australia", -25.3, 133.8, ["Australia", "Australian"]),
        new("India", 20.6, 78.9, ["India", "Indian"]),
        new("Nigeria", 9.1, 8.7, ["Nigeria", "Nigerian"]),
        new("South Africa", -30.6, 22.9, ["South Africa", "South African"]),
        new("Kenya", 0.0, 37.9, ["Kenya", "Kenyan"]),
        new("Philippines", 12.9, 121.8, ["Philippines", "Philippine", "Filipino"]),
        new("Pakistan", 30.4, 69.3, ["Pakistan", "Pakistani"]),
        new("France", 46.2, 2.2, ["France", "French"]),
        new("Germany", 51.2, 10.5, ["Germany", "German"]),
        new("Italy", 41.9, 12.6, ["Italy", "Italian"]),
        new("Spain", 40.5, -3.7, ["Spain", "Spanish"]),
        new("Portugal", 39.4, -8.2, ["Portugal", "Portuguese"]),
        new("Netherlands", 52.1, 5.3, ["Netherlands", "Dutch"]),
        new("Sweden", 60.1, 18.6, ["Sweden", "Swedish"]),
        new("Norway", 60.5, 8.5, ["Norway", "Norwegian"]),
        new("Poland", 51.9, 19.1, ["Poland", "Polish"]),
        new("Czechia", 49.8, 15.5, ["Czechia", "Czech Republic", "Czech"]),
        new("Hungary", 47.2, 19.5, ["Hungary", "Hungarian"]),
        new("Romania", 45.9, 24.9, ["Romania", "Romanian"]),
        new("Greece", 39.1, 21.8, ["Greece", "Greek"]),
        new("Ukraine", 48.4, 31.2, ["Ukraine", "Ukrainian"]),
        new("Russia", 61.5, 105.3, ["Russia", "Russian"]),
        new("Turkey", 39.0, 35.2, ["Turkey", "Turkish", "Türkiye"]),
        new("Israel", 31.0, 34.9, ["Israel", "Israeli"]),
        new("Saudi Arabia", 23.9, 45.1, ["Saudi Arabia", "Saudi"]),
        new("Egypt", 26.8, 30.8, ["Egypt", "Egyptian"]),
        new("United Arab Emirates", 23.4, 53.8, ["United Arab Emirates", "UAE", "Emirati"]),
        new("Brazil", -14.2, -51.9, ["Brazil", "Brazilian"]),
        new("Mexico", 23.6, -102.6, ["Mexico", "Mexican"]),
        new("Argentina", -38.4, -63.6, ["Argentina", "Argentine", "Argentinian"]),
        new("Colombia", 4.6, -74.3, ["Colombia", "Colombian"]),
        new("Japan", 36.2, 138.3, ["Japan", "Japanese"]),
        new("South Korea", 35.9, 127.8, ["South Korea", "South Korean"]),
        new("China", 35.9, 104.2, ["China", "Chinese"]),
        new("Taiwan", 23.7, 121.0, ["Taiwan", "Taiwanese"]),
        new("Indonesia", -0.8, 113.9, ["Indonesia", "Indonesian"]),
        new("Thailand", 15.9, 100.9, ["Thailand", "Thai"]),
        new("Vietnam", 14.1, 108.3, ["Vietnam", "Vietnamese"])
    ];

    // How far back the globe layer, Brief me and World News's "Show on Overwatch" link all look.
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // Country names are proper nouns, so matching is case-sensitive: "US" must not catch "us",
    // nor "Polish" "polish". One pattern per country, compiled once.
    private static readonly (CountryPoint Country, Regex Pattern)[] Patterns = Countries
        .Select(country => (country, new Regex(
            $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", country.Aliases.OrderByDescending(alias => alias.Length).Select(Regex.Escape))})(?![\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled)))
        .ToArray();

    public IReadOnlyList<WorldNewsMapSignal> Signals(BoundingBox bounds, DateTimeOffset since, int limit = MaximumSignals)
    {
        if (limit <= 0) return [];
        // Place and filter on the overview alone; only the few that survive need their latest
        // event, summary and sources read from the store.
        return news.Store.TopSituations(since, 300)
            .Where(s => s.OutletCount >= MinimumOutlets)
            .Select(s => (Situation: s, Location: Locate(s.Title, s.Latest)))
            .Where(item => item.Location is { } location && bounds.Contains(location.Latitude, location.Longitude))
            .OrderByDescending(item => item.Situation.OutletCount)
            .ThenByDescending(item => item.Situation.LastSeen)
            .Take(Math.Min(limit, MaximumSignals))
            .Select(item => Project(item.Situation, item.Location!))
            .ToList();
    }

    // The situation a World News link points at, or null when the layer wouldn't show it.
    public WorldNewsMapSignal? Signal(string situationId, DateTimeOffset since) =>
        news.Store.Situation(situationId) is { } situation && Place(situation, since) is { } location
            ? Project(situation, location)
            : null;

    // Where the layer would put a situation, or null when it stays off the globe.
    public static WorldNewsCountryLocation? Place(GwsBusinessSuite.WorldNewsKit.SituationOverview situation, DateTimeOffset since) =>
        situation.OutletCount >= MinimumOutlets && situation.LastSeen >= since ? Locate(situation.Title, situation.Latest) : null;

    private WorldNewsMapSignal Project(GwsBusinessSuite.WorldNewsKit.SituationOverview situation, WorldNewsCountryLocation location)
    {
        var latestEvent = news.Store.EventsForSituation(situation.Id).OrderByDescending(e => e.LastSeen).FirstOrDefault();
        var summary = latestEvent is null ? null : news.Store.LatestSummary(latestEvent.Id)?.Text;
        var sources = latestEvent is null ? [] : news.Store.ArticlesForEvent(latestEvent.Id)
            .DistinctBy(a => a.Outlet, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(a => a.SeenAt)
            .Take(4)
            .Select(a => new WorldNewsMapSource(a.Outlet, a.Title, a.Url))
            .ToList();
        return new WorldNewsMapSignal(situation.Id, situation.Title, situation.Latest, location.Country,
            location.Latitude, location.Longitude, situation.EventCount, situation.OutletCount,
            situation.ArticleCount, situation.LastSeen, summary, sources);
    }

    public static WorldNewsCountryLocation? Locate(string title, string latest)
    {
        var point = MatchCountry($"{title} {latest}");
        return point is null ? null : new WorldNewsCountryLocation(point.Name, point.Latitude, point.Longitude);
    }

    // The longest mention wins: "South African" names South Africa even beside a passing "US".
    private static CountryPoint? MatchCountry(string text) => Patterns
        .Select(item => (item.Country, Longest: item.Pattern.Matches(text).Select(match => match.Length).DefaultIfEmpty(0).Max()))
        .Where(item => item.Longest > 0)
        .OrderByDescending(item => item.Longest)
        .Select(item => item.Country)
        .FirstOrDefault();
}
