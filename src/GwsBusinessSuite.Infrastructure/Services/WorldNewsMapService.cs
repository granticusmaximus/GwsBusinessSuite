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

    public IReadOnlyList<WorldNewsMapSignal> Signals(BoundingBox bounds, DateTimeOffset since, int limit = MaximumSignals)
    {
        if (limit <= 0) return [];
        return news.Store.TopSituations(since, 300)
            .Where(s => s.OutletCount >= MinimumOutlets)
            .Select(Project)
            .OfType<WorldNewsMapSignal>()
            .Where(s => bounds.Contains(s.Latitude, s.Longitude))
            .OrderByDescending(s => s.OutletCount)
            .ThenByDescending(s => s.LastSeen)
            .Take(Math.Min(limit, MaximumSignals))
            .ToList();
    }

    private WorldNewsMapSignal? Project(GwsBusinessSuite.WorldNewsKit.SituationOverview situation)
    {
        var location = Locate(situation.Title, situation.Latest);
        if (location is null) return null;
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

    private static CountryPoint? MatchCountry(string text) => Countries
        .SelectMany(country => country.Aliases.Select(alias => (Country: country, Alias: alias)))
        .Where(item => Named(item.Alias).IsMatch(text))
        .OrderByDescending(item => item.Alias.Length)
        .Select(item => item.Country)
        .FirstOrDefault();

    private static Regex Named(string name) => new($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
