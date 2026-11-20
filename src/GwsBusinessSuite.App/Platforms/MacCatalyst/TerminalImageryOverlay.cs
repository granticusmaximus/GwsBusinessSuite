using CoreGraphics;
using CoreImage;
using Foundation;
using GwsBusinessSuite.Application.Overwatch;
using MapKit;
using UIKit;

namespace GwsBusinessSuite.App;

// The Mac map's base layer (19-11-26): the same Esri World Imagery the web globe draws, in place of
// Apple's map, with the same military-terminal treatment (OverwatchMapLook). Each tile goes through
// Cesium's imagery steps in Core Image: brightness multiplies, contrast pivots on 0.5, colour out,
// gamma, then the green wash. A tile that fails to load or filter comes back empty, never a crash.
internal sealed class TerminalImageryOverlay : MKTileOverlay
{
    private static readonly CIContext Context = CIContext.Create();
    private static readonly CGColorSpace Rgb = CGColorSpace.CreateSrgb()
        ?? throw new InvalidOperationException("The sRGB colour space is unavailable.");

    public TerminalImageryOverlay() : base(OverwatchMapLook.ImageryTiles)
    {
        CanReplaceMapContent = true;
        MaximumZ = OverwatchMapLook.MaximumZoom;
    }

    public override void LoadTileAtPath(MKTileOverlayPath path, MKTileOverlayLoadTileCompletionHandler result)
    {
        var url = URLForTilePath(path);
        NSUrlSession.SharedSession.CreateDataTask(url, (data, _, error) =>
        {
            if (error is not null || data is null || data.Length == 0)
            {
                result(null!, error!);
                return;
            }
            NSData? tinted = null;
            try { tinted = Tint(data); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Overwatch: tile tint failed: {ex.Message}"); }
            result(tinted ?? data, null!);
        }).Resume();
    }

    internal static NSData? Tint(NSData data)
    {
        using var source = CIImage.FromData(data);
        if (source is null) return null;
        var extent = source.Extent;
        var b = (float)OverwatchMapLook.Brightness;
        using var brightness = new CIColorMatrix
        {
            InputImage = source,
            RVector = new CIVector(b, 0, 0, 0),
            GVector = new CIVector(0, b, 0, 0),
            BVector = new CIVector(0, 0, b, 0),
        };
        using var controls = new CIColorControls
        {
            InputImage = brightness.OutputImage,
            Contrast = (float)OverwatchMapLook.Contrast,
            Saturation = (float)OverwatchMapLook.Saturation,
            Brightness = 0,
        };
        using var gamma = new CIGammaAdjust { InputImage = controls.OutputImage, Power = (float)(1 / OverwatchMapLook.Gamma) };
        var (r, g, bl) = Rgb01(OverwatchMapLook.WashHex);
        using var washColor = new CIColor(r, g, bl, (float)OverwatchMapLook.WashAlpha);
        using var wash = CIImage.ImageWithColor(washColor).ImageByCroppingToRect(extent);
        using var blend = new CISourceOverCompositing { InputImage = wash, BackgroundImage = gamma.OutputImage };
        if (blend.OutputImage is not { } output) return null;
        using var cg = Context.CreateCGImage(output, extent, CIFormat.Rgba8, Rgb);
        return cg is null ? null : UIImage.FromImage(cg).AsJPEG(0.85f);
    }

    private static (float R, float G, float B) Rgb01(string hex) =>
        (Convert.ToInt32(hex.Substring(1, 2), 16) / 255f, Convert.ToInt32(hex.Substring(3, 2), 16) / 255f, Convert.ToInt32(hex.Substring(5, 2), 16) / 255f);
}
