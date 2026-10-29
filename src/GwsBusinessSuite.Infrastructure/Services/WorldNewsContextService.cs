using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Application.RoadTraffic;
using GwsBusinessSuite.Application.Weather;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// An official signal that matches a reported story in kind, place and time.
public sealed record WorldNewsBacking(string Source, string Text, string? Url);
public sealed record WorldNewsNearbyCamera(CameraFeed Camera, double Miles);
public sealed record WorldNewsNearby(IReadOnlyList<WorldNewsNearbyCamera> Cameras, IReadOnlyList<WeatherAlert> Alerts,
    IReadOnlyList<HazardFeature> Hazards, IReadOnlyList<PowerOutageArea> Outages);
// Nearby is null for a story placed only at a country: "near" a country's centre means nothing.
public sealed record WorldNewsStoryContext(IReadOnlyList<WorldNewsBacking> Backing, WorldNewsNearby? Nearby);

// Overwatch's own data around a reported story: official signals that back it up, what's near
// it, and recording the nearest cameras. The official feeds are USGS earthquakes (worldwide) and,
// in the US only, NIFC wildfires, NOAA flooding river gauges, NWS alerts and power outages.
public sealed partial class WorldNewsContextService(
    CameraDirectoryService cameras,
    INwsAlertsService weather,
    HazardLayerService hazards,
    PowerOutageService outages,
    IncidentCaptureService captures,
    ILogger<WorldNewsContextService> logger)
{
    public const double NearbyMiles = 25;
    // How far from a city-level story an official signal can be and still back it.
    public const double BackingMiles = 150;
    public const int MaxNearbyCameras = 5;
    public const double CaptureRadiusMiles = 10;
    private const int OutageBackingCustomers = 1000;

    // A story has to be about the same kind of thing as the signal: a quake doesn't back a train crash.
    private static readonly string[] QuakeWords = ["earthquake", "earthquakes", "quake", "quakes", "tremor", "aftershock", "aftershocks", "seismic", "tsunami"];
    private static readonly string[] FireWords = ["wildfire", "wildfires", "bushfire", "blaze", "fire", "fires"];
    private static readonly string[] FloodWords = ["flood", "floods", "flooding", "flooded"];
    private static readonly string[] WeatherWords = ["hurricane", "storm", "storms", "tornado", "tornadoes", "blizzard", "flood", "flooding",
        "snow", "heat", "heatwave", "typhoon", "cyclone", "wind", "winds", "ice", "fire"];
    private static readonly string[] OutageWords = ["outage", "outages", "blackout", "blackouts"];

    public async Task<WorldNewsStoryContext> ForAsync(WorldNewsMapSignal signal, CancellationToken ct = default)
    {
        var precise = signal.Location.City is not null;
        var lat = signal.Latitude;
        var lon = signal.Longitude;
        // A country-level story can only be matched by a quake whose USGS place names the country.
        var hazardList = await TryAsync("hazards", async () => (await hazards.GetHazardsAsync(
            precise ? Around(lat, lon, BackingMiles) : new BoundingBox(90, -90, 180, -180), ct)).Features) ?? [];
        var alertList = precise ? await TryAsync("weather alerts", () => weather.GetActiveAlertsAsync(Around(lat, lon, NearbyMiles), ct)) ?? [] : [];
        var outageList = precise ? (await TryAsync("power outages", () => outages.GetAsync(Around(lat, lon, BackingMiles), ct)))?.Areas ?? [] : [];

        var backing = Backing(signal, hazardList, alertList, outageList);

        WorldNewsNearby? nearby = null;
        if (precise)
        {
            var cameraList = await TryAsync("cameras", () => cameras.GetCamerasInBoundingBoxAsync(Around(lat, lon, NearbyMiles), ct)) ?? [];
            nearby = new WorldNewsNearby(
                cameraList.Select(c => new WorldNewsNearbyCamera(c, RoadTrafficSummary.Miles(lat, lon, c.Latitude, c.Longitude)))
                    .Where(c => c.Miles <= NearbyMiles).OrderBy(c => c.Miles).Take(MaxNearbyCameras).ToList(),
                alertList,
                hazardList.Where(h => RoadTrafficSummary.Miles(lat, lon, h.Latitude, h.Longitude) <= NearbyMiles).ToList(),
                outageList.Where(a => RoadTrafficSummary.Miles(lat, lon, a.Latitude, a.Longitude) <= NearbyMiles).ToList());
        }
        return new WorldNewsStoryContext(backing, nearby);
    }

    // The official signals that match the story in kind and place: a quake near a city-level
    // story, or one whose USGS place names a country-level story's country; a fire, flooding gauge,
    // NWS alert or big power outage near a city-level one.
    public static IReadOnlyList<WorldNewsBacking> Backing(WorldNewsMapSignal signal, IReadOnlyList<HazardFeature> hazardList,
        IReadOnlyList<WeatherAlert> alertList, IReadOnlyList<PowerOutageArea> outageList)
    {
        var precise = signal.Location.City is not null;
        var words = Words($"{signal.Title} {signal.Latest} {signal.Summary}");
        var (lat, lon) = (signal.Latitude, signal.Longitude);
        var backing = new List<WorldNewsBacking>();
        foreach (var hazard in hazardList.Where(h => h.ObservedAt is null || h.ObservedAt >= signal.LastSeen.AddDays(-2)))
        {
            var miles = RoadTrafficSummary.Miles(lat, lon, hazard.Latitude, hazard.Longitude);
            var near = precise && miles <= BackingMiles;
            var (fits, source) = hazard.Kind switch
            {
                HazardKinds.Earthquake => (words.Overlaps(QuakeWords) && (near || NamesCountry(hazard.Title, signal.Location.Country)), "USGS"),
                HazardKinds.Wildfire => (near && words.Overlaps(FireWords), "NIFC"),
                HazardKinds.River => (near && words.Overlaps(FloodWords), "NOAA river gauge"),
                _ => (false, string.Empty)
            };
            if (fits) backing.Add(new WorldNewsBacking(source, near ? $"{hazard.Title} · {miles:0} mi from {signal.Location.City}" : hazard.Title, hazard.Url));
        }
        // "Flood Warning" backs a story about flooding: compare word roots, weather words only.
        var weatherRoots = WeatherWords.Select(Root).ToHashSet(StringComparer.Ordinal);
        var storyRoots = words.Select(Root).Where(weatherRoots.Contains).ToHashSet(StringComparer.Ordinal);
        foreach (var alert in alertList.Where(a => Words(a.Event).Select(Root).Any(storyRoots.Contains)))
            backing.Add(new WorldNewsBacking("NWS", $"{alert.Event} · {alert.AreaDescription}", null));
        if (words.Overlaps(OutageWords))
        {
            foreach (var area in outageList.Where(a => a.CustomersOut >= OutageBackingCustomers)
                         .Where(a => RoadTrafficSummary.Miles(lat, lon, a.Latitude, a.Longitude) <= BackingMiles)
                         .OrderByDescending(a => a.CustomersOut).Take(3))
                backing.Add(new WorldNewsBacking("Power outages", $"{area.CustomersOut:N0} customers out in {area.Name}, {area.State}", null));
        }
        return backing;
    }

    // Records the cameras nearest a city-level story into Incident Captures, with its summary and
    // sources. Null when it isn't city-level or no public still camera is close enough.
    public Task<IncidentCapture?> CaptureAsync(string username, WorldNewsMapSignal signal, CancellationToken ct = default)
    {
        if (signal.Location.City is null) return Task.FromResult<IncidentCapture?>(null);
        var notes = string.Join("\n", new[] { signal.Summary }.Concat(signal.Sources.Select(s => $"{s.Outlet}: {s.Title} {s.Url}"))
            .Where(line => !string.IsNullOrWhiteSpace(line)));
        return captures.StartAsync(username, Guid.Empty, "World News", $"News: {signal.Title}", signal.Latitude, signal.Longitude, ct,
            CaptureRadiusMiles, notes, $"/admin/world-news?situation={Uri.EscapeDataString(signal.SituationId)}");
    }

    // USGS places read "12 km NE of Tokyo, Japan".
    private static bool NamesCountry(string title, string country) =>
        title.EndsWith(country, StringComparison.OrdinalIgnoreCase) || title.Contains($", {country}", StringComparison.OrdinalIgnoreCase);

    private static string Root(string word) => word switch
    {
        _ when word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal) => word[..^3],
        _ when word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) && word[^3] == 'o' => word[..^2],
        _ when word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal) => word[..^2],
        _ when word.Length > 3 && word.EndsWith('s') => word[..^1],
        _ => word
    };

    private static HashSet<string> Words(string? text) =>
        Word().Matches((text ?? string.Empty).ToLowerInvariant()).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static BoundingBox Around(double latitude, double longitude, double miles)
    {
        var dLat = miles / 69.0;
        var dLon = miles / (69.0 * Math.Max(0.2, Math.Cos(latitude * Math.PI / 180)));
        return new BoundingBox(Math.Min(90, latitude + dLat), Math.Max(-90, latitude - dLat), longitude + dLon, longitude - dLon);
    }

    // One feed failing leaves the others' context in place.
    private async Task<T?> TryAsync<T>(string what, Func<Task<T>> load) where T : class
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "World News context couldn't load {What}", what);
            return null;
        }
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();
}
