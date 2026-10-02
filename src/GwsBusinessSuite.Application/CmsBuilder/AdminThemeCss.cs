using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Carries the live website's theme into the admin portal: the theme's Accent re-colors every
// accent use (buttons, links, active nav item, badges, focus rings) and its Primary colors the
// sidebar and top bar. Page backgrounds deliberately keep following the admin's own light/dark
// toggle, so a very dark or pastel site theme can't make admin screens unreadable.
//
// Every color is resolved to a concrete hex here, rather than with CSS color-mix(), because text
// colors have to be chosen for real contrast (WCAG AA, 4.5:1) against the surface they sit on,
// and that needs the actual numbers.
public static class AdminThemeCss
{
    private const string DarkCanvas = "#141210";
    private const string LightCanvas = "#ffffff";
    private const string DarkInk = "#1c1917";
    private const string LightInk = "#fafaf9";
    private const double ReadableContrast = 4.5;

    public static string Build(DesignTokenSet? tokens)
    {
        if (tokens is null) return string.Empty;
        var accent = Find(tokens, "Accent");
        var primary = Find(tokens, "Primary");
        if (accent is null && primary is null) return string.Empty;

        var shared = new StringBuilder();
        var dark = new StringBuilder();
        var light = new StringBuilder();

        if (accent is not null)
        {
            var (r, g, b) = Rgb(accent);
            shared.Append($"--gws-sidebar-accent:{accent};--gws-sidebar-accent-hover:{Mix(accent, "#000000", 0.15)};");
            shared.Append($"--gws-accent-contrast:{ReadableOn(accent)};");
            shared.Append($"--bs-primary:{accent};--bs-primary-rgb:{r}, {g}, {b};");

            AppendAccentTones(dark, accent, DarkCanvas, towards: "#ffffff");
            AppendAccentTones(light, accent, LightCanvas, towards: "#000000");
        }

        if (primary is not null)
        {
            var text = ReadableOn(primary);
            var towards = text == LightInk ? "#ffffff" : "#000000";
            shared.Append($"--gws-sidebar-bg:{primary};");
            shared.Append($"--gws-admin-bar-bg:{Mix(primary, "#000000", 0.12)};");
            shared.Append($"--gws-sidebar-hover-bg:{Mix(primary, towards, 0.1)};");
            shared.Append($"--gws-sidebar-text:{text};");
            shared.Append($"--gws-sidebar-text-soft:{Mix(text, primary, 0.35)};");
        }

        var css = new StringBuilder();
        // Same specificity as app.css's own light-theme rule and emitted after it, so this wins
        // in both admin modes.
        css.Append($":root,:root[data-theme=\"light\"]{{{shared}}}");
        if (dark.Length > 0) css.Append($":root{{{dark}}}");
        if (light.Length > 0) css.Append($":root[data-theme=\"light\"]{{{light}}}");
        return css.ToString();
    }

    // Link and emphasis text must stay readable on the admin canvas itself, so the accent is
    // nudged lighter (dark mode) or darker (light mode) only as far as contrast requires.
    private static void AppendAccentTones(StringBuilder target, string accent, string canvas, string towards)
    {
        var link = Readable(accent, canvas, towards);
        var (r, g, b) = Rgb(link);
        var hover = Mix(link, towards, 0.2);
        var (hr, hg, hb) = Rgb(hover);
        target.Append($"--bs-link-color:{link};--bs-link-color-rgb:{r}, {g}, {b};");
        target.Append($"--bs-link-hover-color:{hover};--bs-link-hover-color-rgb:{hr}, {hg}, {hb};");
        target.Append($"--bs-primary-text-emphasis:{Mix(link, towards, 0.25)};");
        target.Append($"--bs-primary-bg-subtle:{Mix(canvas, accent, 0.18)};");
        target.Append($"--bs-primary-border-subtle:{Mix(canvas, accent, 0.4)};");
    }

    private static string? Find(DesignTokenSet tokens, string name)
    {
        var value = tokens.Colors.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Hex?.Trim();
        if (value is null || !Regex.IsMatch(value, "^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$")) return null;
        return value.Length == 4
            ? $"#{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}".ToLowerInvariant()
            : value.ToLowerInvariant();
    }

    internal static string ReadableOn(string background) =>
        Contrast(LightInk, background) >= Contrast(DarkInk, background) ? LightInk : DarkInk;

    internal static string Readable(string color, string background, string towards)
    {
        var candidate = color;
        for (var step = 1; step <= 10 && Contrast(candidate, background) < ReadableContrast; step++)
        {
            candidate = Mix(color, towards, step / 10.0);
        }

        return candidate;
    }

    // Linear sRGB mix, the same as CSS color-mix(in srgb, from (1-amount), to amount).
    internal static string Mix(string from, string to, double amount)
    {
        var (r1, g1, b1) = Rgb(from);
        var (r2, g2, b2) = Rgb(to);
        int Channel(int a, int b) => (int)Math.Round(a + (b - a) * amount);
        return $"#{Channel(r1, r2):x2}{Channel(g1, g2):x2}{Channel(b1, b2):x2}";
    }

    public static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double Linear(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static (int R, int G, int B) Rgb(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
}
