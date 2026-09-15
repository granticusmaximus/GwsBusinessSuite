#if MACCATALYST || IOS
using System.Runtime.InteropServices;
using Microsoft.Maui.Platform;
#endif

namespace GwsBusinessSuite.App;

// Bootstrap Icons (used throughout the Blazor web app) isn't available here - it's loaded via
// CDN in the web app's HTML head, with no font file vendored into this repo to bundle as a MAUI
// resource. SF Symbols are Apple's own equivalent, available on every platform this app actually
// ships to (Mac Catalyst, iOS) with no extra asset to bundle at all, and read as more "native"
// here than a web icon font would. No-ops (returns null) on Android/Windows builds of this same
// project, where UIKit doesn't exist.
public static class SfSymbolImageSource
{
    public static ImageSource? Get(string symbolName, string hexColor, double pointSize = 15)
    {
#if MACCATALYST || IOS
        var configuration = UIKit.UIImageSymbolConfiguration.Create((NFloat)pointSize, UIKit.UIImageSymbolWeight.Regular);
        using var symbol = UIKit.UIImage.GetSystemImage(symbolName, configuration);
        if (symbol is null)
        {
            return null;
        }
        using var tinted = symbol.ApplyTintColor(Color.FromArgb(hexColor).ToPlatform());
        var png = tinted.AsPNG();
        if (png is null)
        {
            return null;
        }
        var bytes = png.ToArray();
        return ImageSource.FromStream(() => new MemoryStream(bytes));
#else
        return null;
#endif
    }
}
