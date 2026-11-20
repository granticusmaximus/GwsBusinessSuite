namespace GwsBusinessSuite.Application.Overwatch;

// Focus one layer (19-11-26, Holo Wireframe): every other layer dims to DimAlpha, on the web globe
// and the Mac map alike. One catalog says which web data sources (tactical-globe.js 'tg-*') and which
// Mac map operations make up each layer, so both apps dim the same things.
public sealed record FocusLayer(string Key, string Label, IReadOnlyList<string> WebSources, IReadOnlyList<string> NativeOperations);

public static class OverwatchFocus
{
    public const double DimAlpha = 0.25;
    public const string Operation = NativeOverwatchOperations.FocusLayer;

    public static IReadOnlyList<FocusLayer> All { get; } =
    [
        new("cameras", "Cameras", ["tg-cameras"], [NativeOverwatchOperations.SetCameraPins]),
        new("incidents", "Incidents", ["tg-incidents"], [NativeOverwatchOperations.SetTrafficIncidents]),
        new("signs", "Message signs", ["tg-signs"], [NativeOverwatchOperations.SetMessageSigns]),
        new("road-weather", "Road temperatures", ["tg-road-weather"], [NativeOverwatchOperations.SetRoadWeather]),
        new("world-news", "World news", ["tg-world-news", "tg-news-overlay", "tg-news-shading"], [NativeOverwatchOperations.SetWorldNews, NativeOverwatchOperations.SetNewsShading]),
        new("conflict", "Conflict", ["tg-conflict"], [NativeOverwatchOperations.SetConflict]),
        new("convergence", "Convergence", ["tg-convergence"], [NativeOverwatchOperations.SetConvergence]),
        new("travel", "Travel advice", ["tg-travel", "tg-travel-shading"], [NativeOverwatchOperations.SetTravelAdvice]),
        new("humanitarian", "Humanitarian", ["tg-humanitarian"], [NativeOverwatchOperations.SetHumanitarian]),
        new("hazards", "Hazards", ["tg-hazards"], [NativeOverwatchOperations.SetHazards]),
        new("weather-warnings", "World warnings", ["tg-weather-warnings"], [NativeOverwatchOperations.SetWeatherWarnings]),
        new("internet", "Internet outages", ["tg-internet"], [NativeOverwatchOperations.SetInternetOutages]),
        new("fires", "Fire hotspots", ["tg-fires"], [NativeOverwatchOperations.SetFireHotspots]),
        new("airports", "Airports", ["tg-airports"], [NativeOverwatchOperations.SetAirportStatus]),
        new("satellites", "Satellites", ["tg-satellites"], [NativeOverwatchOperations.SetSatellites]),
        new("chokepoints", "Chokepoints", ["tg-chokepoints"], [NativeOverwatchOperations.SetChokepoints]),
    ];

    public static FocusLayer? Find(string? key) => All.FirstOrDefault(layer => layer.Key == key);
}

// At most three callout boxes at a time (19-11-26, Holo Wireframe): the most serious items in view
// first, then the biggest. Both apps draw the text the server puts on the item.
public static class OverwatchCallouts
{
    public const int Max = 3;

    public static IReadOnlySet<T> Pick<T>(IEnumerable<T> items, Func<T, int> severity, Func<T, double> weight) where T : notnull =>
        items.OrderByDescending(severity).ThenByDescending(weight).Take(Max).ToHashSet();

    public static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "major" => 2,
        "moderate" => 1,
        _ => 0,
    };
}
