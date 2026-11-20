using System.Text.Json;
using CoreGraphics;
using CoreImage;
using CoreLocation;
using Foundation;
using GwsBusinessSuite.Application.Overwatch;
using MapKit;
using UIKit;

namespace GwsBusinessSuite.App;

// The Mac map in Grant's option B, Holo Wireframe (19-11-26), identical to the web globe
// (tactical-globe.js applyHoloLook; the numbers are OverwatchMapLook's). Apple's globe stays (it's
// what keeps the Mac map round when zoomed out) under an opaque black-green veil; on top come the
// same land, coastlines and 15-degree grid as the web, and the same Esri dark street map, greyed and
// washed green, fading in as the camera comes down.
internal static class HoloMap
{
    public const string VeilTitle = "holo-veil";
    public const string LandTitle = "holo-land";
    public const string CoastGlowTitle = "holo-coast-glow";
    public const string CoastTitle = "holo-coast";
    public const string GridTitle = "holo-grid";

    public static UIColor Hex(string hex, double alpha = 1) => UIColor.FromRGBA(
        Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16), (int)Math.Round(alpha * 255));

    // The whole world in map points (MapKit's world is 2^28 points square).
    public static MKPolygon Veil()
    {
        const double World = 268_435_456;
        var veil = MKPolygon.FromPoints([new MKMapPoint(0, 0), new MKMapPoint(World, 0), new MKMapPoint(World, World), new MKMapPoint(0, World)]);
        veil.Title = VeilTitle;
        return veil;
    }

    public static MKMultiPolyline Grid()
    {
        var lines = new List<MKPolyline>();
        for (var lon = -180; lon < 180; lon += OverwatchMapLook.GridStep)
            lines.Add(MKPolyline.FromCoordinates([new CLLocationCoordinate2D(-85, lon), new CLLocationCoordinate2D(0, lon), new CLLocationCoordinate2D(85, lon)]));
        for (var lat = -75; lat <= 75; lat += OverwatchMapLook.GridStep)
            lines.Add(MKPolyline.FromCoordinates(Enumerable.Range(0, 73).Select(i => new CLLocationCoordinate2D(lat, -180 + i * 5)).ToArray()));
        return new MKMultiPolyline(lines.ToArray()) { Title = GridTitle };
    }

    // The land file the web globe draws (/data/overwatch-land.json: { rings: [[[lon, lat], ...], ...] }).
    public static IReadOnlyList<IMKOverlay> Land(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var polygons = new List<MKPolygon>();
        var lines = new List<MKPolyline>();
        foreach (var ring in doc.RootElement.GetProperty("rings").EnumerateArray())
        {
            var points = ring.EnumerateArray().Select(p => new CLLocationCoordinate2D(p[1].GetDouble(), p[0].GetDouble())).ToArray();
            if (points.Length < 4) continue;
            polygons.Add(MKPolygon.FromCoordinates(points));
            lines.Add(MKPolyline.FromCoordinates(points));
        }
        return
        [
            new MKMultiPolygon(polygons.ToArray()) { Title = LandTitle },
            new MKMultiPolyline(lines.ToArray()) { Title = CoastGlowTitle },
            new MKMultiPolyline(lines.ToArray()) { Title = CoastTitle },
        ];
    }

    // The renderer for one of the base overlays, or null when the overlay isn't one of them.
    public static MKOverlayRenderer? Renderer(IMKOverlay overlay) => overlay switch
    {
        HoloStreetOverlay streets => new MKTileOverlayRenderer(streets) { Alpha = 0 },
        MKPolygon { Title: VeilTitle } veil => new MKPolygonRenderer(veil) { FillColor = Hex(OverwatchMapLook.Globe), StrokeColor = UIColor.Clear, LineWidth = 0 },
        MKMultiPolygon { Title: LandTitle } land => new MKMultiPolygonRenderer(land) { FillColor = Hex(OverwatchMapLook.Green, OverwatchMapLook.LandAlpha), StrokeColor = UIColor.Clear, LineWidth = 0 },
        MKMultiPolyline { Title: CoastGlowTitle } glow => new MKMultiPolylineRenderer(glow) { StrokeColor = Hex(OverwatchMapLook.Green, 0.22), LineWidth = 5 },
        MKMultiPolyline { Title: CoastTitle } coast => new MKMultiPolylineRenderer(coast) { StrokeColor = Hex(OverwatchMapLook.Green, OverwatchMapLook.CoastAlpha), LineWidth = 1.2f },
        MKMultiPolyline { Title: GridTitle } grid => new MKMultiPolylineRenderer(grid) { StrokeColor = Hex(OverwatchMapLook.Green, OverwatchMapLook.GridAlpha), LineWidth = 0.7f },
        _ => null,
    };
}

// Esri's dark street map, greyed, brightened and washed green exactly as the web globe does it
// (saturation 0, brightness, then the green at WashAlpha). A tile that fails comes back as it was.
internal sealed class HoloStreetOverlay : MKTileOverlay
{
    private static readonly CIContext Context = CIContext.Create();
    private static readonly CGColorSpace Rgb = CGColorSpace.CreateSrgb()
        ?? throw new InvalidOperationException("The sRGB colour space is unavailable.");

    public HoloStreetOverlay() : base(OverwatchMapLook.StreetTiles)
    {
        CanReplaceMapContent = false;
        MaximumZ = OverwatchMapLook.StreetMaximumZoom;
    }

    public override void LoadTileAtPath(MKTileOverlayPath path, MKTileOverlayLoadTileCompletionHandler result)
    {
        NSUrlSession.SharedSession.CreateDataTask(URLForTilePath(path), (data, _, error) =>
        {
            if (error is not null || data is null || data.Length == 0)
            {
                result(null!, error!);
                return;
            }
            NSData? tinted = null;
            try { tinted = Tint(data); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Overwatch: street tile tint failed: {ex.Message}"); }
            result(tinted ?? data, null!);
        }).Resume();
    }

    internal static NSData? Tint(NSData data)
    {
        using var source = CIImage.FromData(data);
        if (source is null) return null;
        var extent = source.Extent;
        var b = (float)OverwatchMapLook.StreetBrightness;
        using var grey = new CIColorControls { InputImage = source, Saturation = 0, Brightness = 0, Contrast = 1 };
        using var brighter = new CIColorMatrix
        {
            InputImage = grey.OutputImage,
            RVector = new CIVector(b, 0, 0, 0),
            GVector = new CIVector(0, b, 0, 0),
            BVector = new CIVector(0, 0, b, 0),
        };
        var hex = OverwatchMapLook.Green;
        using var washColor = new CIColor(Convert.ToInt32(hex.Substring(1, 2), 16) / 255f, Convert.ToInt32(hex.Substring(3, 2), 16) / 255f,
            Convert.ToInt32(hex.Substring(5, 2), 16) / 255f, (float)OverwatchMapLook.WashAlpha);
        using var wash = CIImage.ImageWithColor(washColor).ImageByCroppingToRect(extent);
        using var blend = new CISourceOverCompositing { InputImage = wash, BackgroundImage = brighter.OutputImage };
        if (blend.OutputImage is not { } output) return null;
        using var cg = Context.CreateCGImage(output, extent, CIFormat.Rgba8, Rgb);
        return cg is null ? null : UIImage.FromImage(cg).AsJPEG(0.85f);
    }
}
