using GwsBusinessSuite.Application.CameraIntel;
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
        var corrections = store.Placements();
        var growth = store.SituationGrowth(DateTimeOffset.UtcNow - SituationSurge.Window);
        // Place and filter on the overview alone; only the few that survive need their latest
        // event, summary and sources read from the store.
        return store.TopSituations(since, 300)
            .Where(s => s.OutletCount >= MinimumOutlets && (kinds is null || kinds.Contains(KindOf(s))))
            .Select(s => (Situation: s, Location: Locate(s, corrections)))
            .Where(item => item.Location is { } location && bounds.Contains(location.Latitude, location.Longitude))
            .OrderByDescending(item => item.Situation.OutletCount)
            .ThenByDescending(item => item.Situation.LastSeen)
            .Take(Math.Min(limit, MaximumSignals))
            .Select(item => Project(item.Situation, item.Location!, growth))
            .ToList();
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
