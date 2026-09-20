using System.Globalization;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Phase 4 (Section-level Color Scheme shortcut) - picking a background token needs a readable
// default text color to pair with it, and DesignTokenSet has no "paired foreground" concept
// (colors are a flat, freely-named list - see DesignTokenModels.cs), so this computes one from
// perceived brightness instead of requiring the user to author a matching token by convention.
public static class ColorContrast
{
    // A near-black/near-white pair rather than pure #000/#fff - matches this app's existing
    // dark-surface token convention elsewhere (see cms-public.css's --cms-dark: #0f172a) and
    // reads slightly softer on screen than absolute black or white.
    private const string DarkText = "#0f172a";
    private const string LightText = "#f8fafc";

    public static string GetReadableTextColor(string backgroundHex)
    {
        if (!TryParseRgb(backgroundHex, out var r, out var g, out var b))
        {
            return DarkText;
        }

        // Perceived-brightness formula (ITU-R BT.601 luma weights), same simplified approach
        // widely used for this exact "pick black or white text" problem - full WCAG relative
        // luminance/contrast-ratio math would be overkill for a one-shot default that the
        // author can always override per-widget afterward.
        var brightness = (r * 299 + g * 587 + b * 114) / 1000.0;
        return brightness > 140 ? DarkText : LightText;
    }

    private static bool TryParseRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var value = hex.Trim().TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(c => new string(c, 2)));
        }
        if (value.Length != 6)
        {
            return false;
        }

        return int.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
