namespace GwsBusinessSuite.Application.Overwatch;

// One look for both Overwatch maps (19-11-26, Grant: "the two views of Overwatch to be identical ...
// a military terminal with greens and blacks"). The web globe (tactical-globe.js) and the Mac app's
// MapKit view draw the same Esri World Imagery tiles the same way: darkened, contrast up, all colour
// taken out, then a phosphor-green wash on top. The steps follow Cesium's own imagery shader
// (brightness multiplies, contrast pivots on 0.5, then saturation, then gamma), so the Mac's Core
// Image chain gives the same pixels. NativeShapesTests checks the web file carries these numbers.
public static class OverwatchMapLook
{
    public const string ImageryTiles = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
    public const int MaximumZoom = 19;
    public const double Brightness = 0.55;
    public const double Contrast = 1.35;
    public const double Saturation = 0;
    public const double Gamma = 0.9;
    public const string WashHex = "#19ff7a";
    public const double WashAlpha = 0.26;
}
