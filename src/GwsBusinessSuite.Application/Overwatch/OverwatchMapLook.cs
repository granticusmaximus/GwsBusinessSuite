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

    // How much of the street map shows at a camera height: none above StreetsBelowMetres, all of it
    // below StreetsFullMetres (holoStreetShare in tactical-globe.js).
    public static double StreetShare(double heightMetres) =>
        Math.Clamp((StreetsBelowMetres - heightMetres) / (StreetsBelowMetres - StreetsFullMetres), 0, 1);
}
