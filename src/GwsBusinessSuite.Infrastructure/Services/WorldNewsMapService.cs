using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RoadTraffic;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// A deliberately conservative projection of World News onto Overwatch. Article.Country is the
// publishing edition/outlet country, not proof of where the event happened, so it is never used
// as a coordinate. A situation is mapped only when its own headline (or, to sharpen a country to
// a city, its summary) names a place we can locate. Every marker says how precise it is and is
// labelled as reported news, not an operational incident.
public sealed record WorldNewsMapSignal(
    string SituationId,
    string Title,
    string Latest,
    WorldNewsLocation Location,
    int EventCount,
    int OutletCount,
    int ReportCount,
    DateTimeOffset LastSeen,
    string? Summary,
    IReadOnlyList<WorldNewsMapSource> Sources,
    string Kind = SituationKinds.Other,
    // New events in the last 48 hours (World News's "Active"); otherwise Quiet.
    bool Active = true,
    // Set when coverage is surging (SituationSurge), saying how.
    string? Surge = null)
{
    public string Country => Location.Country;
    public double Latitude => Location.Latitude;
    public double Longitude => Location.Longitude;
}

public sealed record WorldNewsMapSource(string Outlet, string Title, string Url);
public sealed record WorldNewsRouteItem(WorldNewsMapSignal Signal, string Reason);
// A country's share of the placed stories, for Overwatch's country shading.
public sealed record WorldNewsCountryTotal(string Code, string Name, int Outlets, IReadOnlyList<(string Id, string Title)> Stories);
// One edition country reporting a story, for the coverage lines.
public sealed record WorldNewsCoverage(string Country, double Latitude, double Longitude, int Outlets);
// One event of a situation in the order they broke, placed where its own headline says.
public sealed record WorldNewsTimelineStep(string Title, DateTimeOffset FirstSeen, int Outlets, WorldNewsLocation Location);

public sealed class WorldNewsMapService(WorldNewsStore store)
{
    public const int MinimumOutlets = 3;
    public const int MaximumSignals = 50;

    // How far back Brief me and World News's "Show on Overwatch" link look; the layer itself can
    // look back further (Windows).
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static readonly IReadOnlyList<int> WindowHours = [24, 72, 168];

    public IReadOnlyList<WorldNewsMapSignal> Signals(BoundingBox bounds, DateTimeOffset since,
        IReadOnlyCollection<string>? kinds = null, int limit = MaximumSignals)
    {
        if (limit <= 0) return [];
        var growth = store.SituationGrowth(DateTimeOffset.UtcNow - SituationSurge.Window);
        return Placed(since, kinds)
            .Where(item => bounds.Contains(item.Location.Latitude, item.Location.Longitude))
            .OrderByDescending(item => item.Situation.OutletCount)
            .ThenByDescending(item => item.Situation.LastSeen)
            .Take(Math.Min(limit, MaximumSignals))
            .Select(item => Project(item.Situation, item.Location, growth))
            .ToList();
    }

    // How close a city-level story has to be to a watched route, and how far back trips look
    // (a rail strike or a closed border matters for days).
    public const double RouteMiles = 25;
    public static readonly TimeSpan RouteWindow = TimeSpan.FromDays(3);
    // A country-level story only says the country is affected; for a route that's worth knowing
    // about strikes, closures and unrest, not a disaster somewhere else in the country.
    private static readonly HashSet<string> CountryWideKinds = [SituationKinds.Transport, SituationKinds.Conflict];

    // Stories along a watched route: city-level ones near the path, and country-wide transport
    // or unrest stories in a country it passes through.
    public IReadOnlyList<WorldNewsRouteItem> AlongRoute(IReadOnlyList<RoutePoint> path, DateTimeOffset since, int limit = 10)
    {
        if (path.Count == 0) return [];
        var step = Math.Max(1, path.Count / 200);
        var crossed = path.Where((_, i) => i % step == 0 || i == path.Count - 1)
            .Select(point => WorldNewsPlaces.CountryAt(point.Latitude, point.Longitude))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var near = new List<(SituationOverview Situation, WorldNewsLocation Location, string Reason, double Miles)>();
        foreach (var (situation, location) in Placed(since, SituationKinds.Operational))
        {
            if (location.Precision != WorldNewsPrecision.Country)
            {
                var miles = path.Min(point => RoadTrafficSummary.Miles(location.Latitude, location.Longitude, point.Latitude, point.Longitude));
                if (miles <= RouteMiles) near.Add((situation, location, miles < 1 ? "on the route" : $"{miles:0} mi from the route", miles));
            }
            else if (crossed.Contains(location.CountryCode) && CountryWideKinds.Contains(KindOf(situation)))
            {
                near.Add((situation, location, $"in {location.Country}, which the route crosses", double.MaxValue));
            }
        }
        var growth = store.SituationGrowth(DateTimeOffset.UtcNow - SituationSurge.Window);
        return near.OrderBy(item => item.Miles).ThenByDescending(item => item.Situation.OutletCount).Take(limit)
            .Select(item => new WorldNewsRouteItem(Project(item.Situation, item.Location, growth), item.Reason))
            .ToList();
    }

    // Outlets on the stories placed in each country (city-level ones count for their country), for
    // shading. Stories lists a few of each country's biggest for its panel.
    public IReadOnlyList<WorldNewsCountryTotal> CountryTotals(DateTimeOffset since, IReadOnlyCollection<string>? kinds, int storiesEach = 5) =>
        Placed(since, kinds)
            .GroupBy(item => item.Location.CountryCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WorldNewsCountryTotal(group.Key, group.First().Location.Country, group.Sum(item => item.Situation.OutletCount),
                group.OrderByDescending(item => item.Situation.OutletCount).Take(storiesEach)
                    .Select(item => (item.Situation.Id, item.Situation.Title)).ToList()))
            .OrderByDescending(total => total.Outlets)
            .ToList();

    // Where a story's outlets are: their edition countries, at each country's label point.
    public IReadOnlyList<WorldNewsCoverage> Coverage(string situationId) =>
        store.SituationCoverage(situationId)
            .Select(row => (Row: row, Country: WorldNewsPlaces.Instance.FindCountry(row.Country)))
            .Where(item => item.Country is not null)
            .Select(item => new WorldNewsCoverage(item.Country!.Name, item.Country.Latitude, item.Country.Longitude, item.Row.Outlets))
            .ToList();

    // A situation's events oldest first, each placed by its own headline or, failing that, where
    // the situation is.
    public IReadOnlyList<WorldNewsTimelineStep> Timeline(string situationId, WorldNewsLocation fallback) =>
        store.EventsForSituation(situationId)
            .OrderBy(e => e.FirstSeen)
            .Select(e => new WorldNewsTimelineStep(e.Headline ?? e.Title, e.FirstSeen, e.OutletCount,
                fallback.Precision == WorldNewsPrecision.Corrected ? fallback : Locate(e.Headline ?? e.Title, e.Title) ?? fallback))
            .ToList();

    // Every situation the layer could show, with where it goes. Placing needs only the overview;
    // the latest event, summary and sources are read later, for the few that are kept.
    private IEnumerable<(SituationOverview Situation, WorldNewsLocation Location)> Placed(DateTimeOffset since, IReadOnlyCollection<string>? kinds)
    {
        var corrections = store.Placements();
        foreach (var situation in store.TopSituations(since, 300))
        {
            if (situation.OutletCount < MinimumOutlets || (kinds is not null && !kinds.Contains(KindOf(situation)))) continue;
            if (Locate(situation, corrections) is { } location) yield return (situation, location);
        }
    }

    // The situation a World News link points at, or null when the layer wouldn't show it.
    public WorldNewsMapSignal? Signal(string situationId, DateTimeOffset since) =>
        store.Situation(situationId) is { } situation && Place(situation, since) is { } location
            ? Project(situation, location, store.SituationGrowth(DateTimeOffset.UtcNow - SituationSurge.Window))
            : null;

    // A situation the last read hasn't classified yet (straight after a deploy) goes by its words.
    public static string KindOf(SituationOverview situation) =>
        situation.Kind ?? SituationKinds.FromWords($"{situation.Title} {situation.Latest}");

    // Where the layer would put a situation, or null when it stays off the globe.
    public WorldNewsLocation? Place(SituationOverview situation, DateTimeOffset since) =>
        situation.OutletCount >= MinimumOutlets && situation.LastSeen >= since ? Locate(situation, store.Placements()) : null;

    // Where the headlines alone put it, ignoring any correction (for "Wrong place?").
    public static WorldNewsLocation? Named(SituationOverview situation) => Locate(situation.Title, situation.Latest);

    public SituationPlacement? Correction(string situationId) => store.Placements().GetValueOrDefault(situationId);

    // "Wrong place?" on World News: pin to a country and optionally one of its cities. Returns what
    // to tell the person, or null once saved.
    public string? Correct(string situationId, string? countryCode, string? city, string username, DateTimeOffset now)
    {
        if (WorldNewsPlaces.Instance.Country(countryCode) is not { } country) return "Pick a country.";
        var found = string.IsNullOrWhiteSpace(city) ? null : WorldNewsPlaces.Instance.FindCity(country.Code, city);
        if (!string.IsNullOrWhiteSpace(city) && found is null)
            return $"No city called \"{city.Trim()}\" in {country.Name} is in the place list (cities of 50,000+ people). Leave the city blank to place it at the country.";
        store.SetPlacement(new SituationPlacement(situationId, country.Code, found?.Name, false, username, now));
        return null;
    }

    public void Hide(string situationId, string username, DateTimeOffset now) =>
        store.SetPlacement(new SituationPlacement(situationId, null, null, true, username, now));

    public void ClearCorrection(string situationId) => store.ClearPlacement(situationId);

    // A correction from World News wins over what the headlines name.
    private static WorldNewsLocation? Locate(SituationOverview situation, IReadOnlyDictionary<string, SituationPlacement> corrections)
    {
        if (!corrections.TryGetValue(situation.Id, out var fix)) return Named(situation);
        if (fix.Hidden || WorldNewsPlaces.Instance.Country(fix.CountryCode) is not { } country) return null;
        return WorldNewsPlaces.Instance.At(country, WorldNewsPlaces.Instance.FindCity(country.Code, fix.City), WorldNewsPrecision.Corrected);
    }

    private WorldNewsMapSignal Project(SituationOverview situation, WorldNewsLocation location,
        IReadOnlyDictionary<string, (int Outlets, int OutletsBefore)> growth)
    {
        var latestEvent = store.EventsForSituation(situation.Id).OrderByDescending(e => e.LastSeen).FirstOrDefault();
        var summary = latestEvent is null ? null : store.LatestSummary(latestEvent.Id)?.Text;
        var sources = latestEvent is null ? [] : store.ArticlesForEvent(latestEvent.Id)
            .DistinctBy(a => a.Outlet, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(a => a.SeenAt)
            .Take(4)
            .Select(a => new WorldNewsMapSource(a.Outlet, a.Title, a.Url))
            .ToList();
        // The summary often names the city the headline leaves out ("near Madrid, in Toledo province").
        if (location.Precision == WorldNewsPrecision.Country && summary is not null
            && Locate(situation.Title, situation.Latest, summary) is { City: not null } sharper && sharper.CountryCode == location.CountryCode)
            location = sharper;
        var (outlets, before) = growth.GetValueOrDefault(situation.Id);
        return new WorldNewsMapSignal(situation.Id, situation.Title, situation.Latest, location, situation.EventCount,
            situation.OutletCount, situation.ArticleCount, situation.LastSeen, summary, sources, KindOf(situation),
            situation.LastSeen >= DateTimeOffset.UtcNow - SituationLinker.QuietAfter,
            SituationSurge.IsSurging(outlets, before) ? SituationSurge.Message(outlets, before) : null);
    }

    public static WorldNewsLocation? Locate(string title, string latest, string? detail = null) =>
        WorldNewsPlaces.Instance.Locate($"{title} {latest}", detail);
}
