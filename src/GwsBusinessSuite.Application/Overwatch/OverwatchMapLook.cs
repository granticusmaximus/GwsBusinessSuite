namespace GwsBusinessSuite.Application.Overwatch;

// One look for both Overwatch maps (19-11-26, Grant: the web and Mac Overwatch identical; his option
// B, Holo Wireframe). The web globe (tactical-globe.js, HOLO) and the Mac app's MapKit globe draw the
// same thing from these numbers: a black-green globe, faint land with glowing coastlines from
// /data/overwatch-land.json, a 15-degree grid, and Esri's dark street map, greyed and under a green
// wash, fading in as the camera comes down. NativeShapesTests keeps the web file equal to this.
public static class OverwatchMapLook
{
    public const string Green = "#59f07a"; // Night Ops green (20-11-26)
    public const string Bright = "#b8ffd6";
    public const string Globe = "#04160c";
    public const double LandAlpha = 0.07;
    public const double CoastAlpha = 0.9;
    public const double GridAlpha = 0.14;
    public const int GridStep = 15;

    public const string StreetTiles = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}";
    public const string LandPath = "/data/overwatch-land.json";
    public const int StreetMaximumZoom = 16;
    public const double StreetsBelowMetres = 400_000;
    public const double StreetsFullMetres = 60_000;
    public const double StreetAlpha = 0.95;
    public const double StreetBrightness = 1.6;
    public const double WashAlpha = 0.32;

    // Night Ops zoom stages (20-11-26): orbit, regional, approach, ground (zoomStage in tactical-globe.js).
    public const double OrbitAboveMetres = 2_000_000;
    public const double RegionalAboveMetres = 50_000;
    public const double ApproachAboveMetres = 5_000;

    public static string Stage(double heightMetres) => heightMetres switch
    {
        > OrbitAboveMetres => "orbit",
        > RegionalAboveMetres => "regional",
        > ApproachAboveMetres => "approach",
        _ => "ground",
    };

    // The sun's azimuth and elevation in degrees at a place and time, for the HUD (Night Ops phase 4,
    // 20-11-26; sunPosition in tactical-globe.js). The usual low-precision formula, within about a
    // degree, which is all a readout needs.
    public static (double Azimuth, double Elevation) SunPosition(DateTimeOffset when, double latitude, double longitude)
    {
        const double Rad = Math.PI / 180;
        var d = when.ToUnixTimeMilliseconds() / 86400000.0 - 10957.5;
        var g = (357.529 + 0.98560028 * d) * Rad;
        var q = 280.459 + 0.98564736 * d;
        var l = (q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Rad;
        var e = (23.439 - 0.00000036 * d) * Rad;
        var ra = Math.Atan2(Math.Cos(e) * Math.Sin(l), Math.Cos(l));
        var dec = Math.Asin(Math.Sin(e) * Math.Sin(l));
        var gmst = ((18.697374558 + 24.06570982441908 * d) % 24 + 24) % 24;
        var ha = (gmst * 15 + longitude) * Rad - ra;
        var la = latitude * Rad;
        var el = Math.Asin(Math.Sin(la) * Math.Sin(dec) + Math.Cos(la) * Math.Cos(dec) * Math.Cos(ha));
        var az = Math.Atan2(-Math.Sin(ha), Math.Tan(dec) * Math.Cos(la) - Math.Sin(la) * Math.Cos(ha));
        return ((az / Rad + 360) % 360, el / Rad);
    }

    // Night mode (Night Ops phase 5/6, 20-11-26): the sun at the centre of the view sets day, dusk
    // (below the horizon) or night (below civil twilight, -6 degrees), as data-ow-night on the web.
    // The web dims its map with a CSS brightness filter of 1 - the dim; the Mac map, which is native
    // and outside that CSS, lays black over itself at the same strength.
    public const double DuskDim = 0.18;
    public const double NightDim = 0.38;

    public static string Night(double sunElevation) => sunElevation < -6 ? "night" : sunElevation < 0 ? "dusk" : "day";

    public static double Dim(string night) => night switch
    {
        "night" => NightDim,
        "dusk" => DuskDim,
        _ => 0,
    };

    // Looks (Night Ops x Apple, 21-11-26): Standard, Night Ops, and Holo Earth draw the holo globe
    // (Holo Earth layers the glowing coastlines, 15-degree grid, and subtle wash over real aerial
    // imagery on the web); Satellite shows real aerial imagery (Esri World_Imagery on the web, Apple's
    // satellite map on the Mac) with the holo land, grid and streets hidden (setLook in tactical-globe.js).
    // Anything unknown is Night Ops.
    public const string SatelliteLook = "satellite";
    public const string HoloEarthLook = "holo-earth";

    public static string Look(string? look) => look is "standard" or "night-ops" or HoloEarthLook or SatelliteLook ? look : "night-ops";

    public static bool ShowsHolo(string? look) => Look(look) != SatelliteLook;

    // How much of the street map shows at a camera height: none above StreetsBelowMetres, all of it
    // below StreetsFullMetres (holoStreetShare in tactical-globe.js).
    public static double StreetShare(double heightMetres) =>
        Math.Clamp((StreetsBelowMetres - heightMetres) / (StreetsBelowMetres - StreetsFullMetres), 0, 1);
}
