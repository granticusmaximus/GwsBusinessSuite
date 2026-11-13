using System.Text.Json;

namespace GwsBusinessSuite.Application.Overwatch;

/// <summary>
/// Versioned command envelope shared by the web Overwatch and a future embedded native renderer.
/// Payload JSON uses the same camel-case shape passed to tactical-globe.js.
/// </summary>
public sealed record NativeOverwatchViewport(
    string ContractVersion,
    DateTimeOffset GeneratedAtUtc,
    NativeOverwatchViewportRequest Request,
    IReadOnlyList<NativeOverwatchCommand> Commands);

public sealed record NativeOverwatchViewportRequest(
    NativeOverwatchBounds Bounds,
    NativeOverwatchControls Controls,
    DateTimeOffset? AtUtc = null);

public sealed record NativeOverwatchBounds(double North, double South, double East, double West)
{
    public bool IsValid =>
        double.IsFinite(North) && double.IsFinite(South) && double.IsFinite(East) && double.IsFinite(West)
        && North is >= -90 and <= 90 && South is >= -90 and <= 90 && North >= South
        && East is >= -180 and <= 180 && West is >= -180 and <= 180;
}

/// <summary>
/// Mirrors all Overwatch layer switches and data filters. A false switch is represented by that
/// layer's clear command, so native clients can apply a response atomically.
/// </summary>
public sealed record NativeOverwatchControls(
    bool Radar = false,
    bool WeatherAlerts = false,
    bool ThreeDCities = false,
    bool RoadWeather = false,
    bool TrafficIncidents = false,
    bool MessageSigns = false,
    bool CityTraffic = false,
    bool LiveTrafficFlow = false,
    bool WorldNews = false,
    bool WorldNewsShading = false,
    bool Convergence = false,
    bool Conflict = false,
    bool ConflictConfirmed = true,
    bool ConflictReported = true,
    bool TravelAdvisories = false,
    bool FireHotspots = false,
    bool Humanitarian = false,
    bool Hazards = false,
    bool Aircraft = false,
    bool AirQuality = false,
    bool Ships = false,
    bool PowerOutages = false,
    bool InternetOutages = false,
    bool Airports = false,
    bool Aurora = false,
    bool Satellites = false,
    bool HideUnhealthyCameras = false,
    int WorldNewsHours = 24,
    IReadOnlyList<string>? WorldNewsKinds = null,
    IReadOnlyList<string>? HiddenIncidentCategories = null);

public sealed record NativeOverwatchCommand(string Operation, JsonElement Payload, JsonElement? Options = null);

/// <summary>
/// The complete public command vocabulary. Keep these names aligned with tactical-globe.js; a
/// native client must never infer a payload from UI state or call a public feed directly.
/// </summary>
public static class NativeOverwatchOperations
{
    public const string SetCameraPins = "setCameraPins";
    public const string SetWeatherSnapshot = "setWeatherSnapshot";
    public const string SetRadarVisible = "setRadarVisible";
    public const string SetRadarTimeline = "setRadarTimeline";
    public const string SetHourlyForecast = "setHourlyForecast";
    public const string SetWeatherAlerts = "setWeatherAlerts";
    public const string ClearWeatherAlerts = "clearWeatherAlerts";
    public const string SetCameraAlertHighlights = "setCameraAlertHighlights";
    public const string SetTrafficIncidents = "setTrafficIncidents";
    public const string ClearTrafficIncidents = "clearTrafficIncidents";
    public const string SetWorldNews = "setWorldNews";
    public const string ClearWorldNews = "clearWorldNews";
    public const string SetNewsShading = "setNewsShading";
    public const string ClearNewsShading = "clearNewsShading";
    public const string SetMapNotes = "setMapNotes";
    public const string SetThreeD = "setThreeD";
    public const string SetNightLights = "setNightLights";
    public const string SetDriveFollow = "setDriveFollow";
    public const string SetSelectModeEnabled = "setSelectModeEnabled";
    public const string SetTimeMachine = "setTimeMachine";
    public const string ClearTimeMachine = "clearTimeMachine";
    public const string SetRoadWeather = "setRoadWeather";
    public const string ClearRoadWeather = "clearRoadWeather";
    public const string SetMessageSigns = "setMessageSigns";
    public const string ClearMessageSigns = "clearMessageSigns";
    public const string SetRoadTraffic = "setRoadTraffic";
    public const string SetTrafficFlow = "setTrafficFlow";
    public const string SetAircraft = "setAircraft";
    public const string SetAirQuality = "setAirQuality";
    public const string SetPowerOutages = "setPowerOutages";
    public const string SetInternetOutages = "setInternetOutages";
    public const string ClearInternetOutages = "clearInternetOutages";
    public const string SetConvergence = "setConvergence";
    public const string ClearConvergence = "clearConvergence";
    public const string SetAirportStatus = "setAirportStatus";
    public const string ClearAirportStatus = "clearAirportStatus";
    public const string SetChokepoints = "setChokepoints";
    public const string ClearChokepoints = "clearChokepoints";
    public const string SetAurora = "setAurora";
    public const string ClearAurora = "clearAurora";
    public const string SetSatellites = "setSatellites";
    public const string ClearSatellites = "clearSatellites";
    public const string SetTravelAdvice = "setTravelAdvice";
    public const string ClearTravelAdvice = "clearTravelAdvice";
    public const string SetFireHotspots = "setFireHotspots";
    public const string ClearFireHotspots = "clearFireHotspots";
    public const string SetConflict = "setConflict";
    public const string ClearConflict = "clearConflict";
    public const string SetHumanitarian = "setHumanitarian";
    public const string ClearHumanitarian = "clearHumanitarian";
    public const string SetHazards = "setHazards";
    public const string ClearHazards = "clearHazards";
    public const string SetShips = "setShips";
    public const string SetCoverageMarkers = "setCoverageMarkers";
    public const string ClearCoverageMarkers = "clearCoverageMarkers";
    public const string SetRoute = "setRoute";
    public const string ClearRoute = "clearRoute";
    public const string SetRouteHighlights = "setRouteHighlights";
    public const string SetRouteConditions = "setRouteConditions";
    public const string SetStreetViewMarker = "setStreetViewMarker";
    public const string ClearStreetViewMarker = "clearStreetViewMarker";

    public static IReadOnlyList<string> All { get; } =
    [
        SetCameraPins, SetWeatherSnapshot, SetRadarVisible, SetRadarTimeline, SetHourlyForecast,
        SetWeatherAlerts, ClearWeatherAlerts, SetCameraAlertHighlights, SetTrafficIncidents,
        ClearTrafficIncidents, SetWorldNews, ClearWorldNews, SetNewsShading, ClearNewsShading,
        SetMapNotes, SetThreeD, SetNightLights, SetDriveFollow, SetSelectModeEnabled, SetTimeMachine, ClearTimeMachine, SetRoadWeather, ClearRoadWeather,
        SetMessageSigns, ClearMessageSigns, SetRoadTraffic, SetTrafficFlow, SetAircraft,
        SetAirQuality, SetPowerOutages, SetInternetOutages, ClearInternetOutages, SetConvergence,
        ClearConvergence, SetAirportStatus, ClearAirportStatus, SetChokepoints, ClearChokepoints, SetAurora, ClearAurora,
        SetSatellites, ClearSatellites, SetTravelAdvice, ClearTravelAdvice, SetFireHotspots,
        ClearFireHotspots, SetConflict, ClearConflict, SetHumanitarian, ClearHumanitarian,
        SetHazards, ClearHazards, SetShips, SetCoverageMarkers, ClearCoverageMarkers, SetRoute,
        ClearRoute, SetRouteHighlights, SetRouteConditions, SetStreetViewMarker, ClearStreetViewMarker
    ];

    /// <summary>
    /// Layers whose pins open a window when clicked, as on the Cesium globe. The Mac's native map
    /// hit-tests only these, so a click near a ship or an air-quality dot can't steal the click
    /// from the camera or incident under it. tactical-globe.js's nativeSelectionHandlers has the
    /// same keys.
    /// </summary>
    public static IReadOnlySet<string> Selectable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SetCameraPins, SetWorldNews, SetNewsShading, SetAircraft, SetRoadWeather, SetMessageSigns,
        SetTrafficIncidents, SetConvergence, SetSatellites, SetHazards, SetInternetOutages,
        SetAirportStatus, SetChokepoints, SetTravelAdvice, SetFireHotspots, SetConflict, SetHumanitarian, SetMapNotes
    };
}
