namespace GwsBusinessSuite.Web;

// Overwatch's layer health panel (13-11-26): every layer and where its data comes from. Names match
// the ones layer failures are reported under (Canonical maps the variants).
public static class OverwatchLayerCatalog
{
    public static readonly IReadOnlyList<(string Name, string Source)> Layers =
    [
        ("cameras", "state DOTs, 511s, city feeds"),
        ("incidents", "state DOT and 511 feeds"),
        ("radar", "RainViewer worldwide, NOAA HRRR in the US"),
        ("weather", "Open-Meteo"),
        ("alerts", "NWS (US)"),
        ("road temps", "state road weather stations"),
        ("signs", "state message signs"),
        ("city traffic", "HPMS, NYC speeds (TomTom with a key)"),
        ("world news", "World News (this app)"),
        ("convergence", "this app, from the other layers"),
        ("conflict", "GDELT and UCDP"),
        ("travel advice", "US State Department"),
        ("humanitarian", "IFRC GO"),
        ("hazards", "USGS, NIFC, NOAA, GDACS, NASA EONET"),
        ("power outages", "US DOE ODIN"),
        ("internet outages", "Georgia Tech IODA"),
        ("fire hotspots", "NASA FIRMS"),
        ("aircraft", "OpenSky Network"),
        ("air", "Open-Meteo air quality"),
        ("airports", "FAA NAS status"),
        ("aurora", "NOAA SWPC"),
        ("satellites", "CelesTrak"),
        ("ships", "Finland's open AIS (aisstream with a key)"),
        ("chokepoints", "IMF PortWatch"),
        ("3D", "AWS Terrain Tiles (Cesium ion with a token)"),
    ];

    private static readonly Dictionary<string, string> Variants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hazard"] = "hazards", ["fire hotspot"] = "fire hotspots", ["internet outage"] = "internet outages",
        ["power outage"] = "power outages", ["airport status"] = "airports", ["air quality"] = "air",
        ["travel"] = "travel advice",
    };

    public static string Canonical(string layer) => Variants.GetValueOrDefault(layer) ?? layer;
}
