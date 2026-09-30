using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Shared by live pages, exports and previews so previewing never needs to save a preset.
public static class CmsThemeCss
{
    public static string Build(DesignTokenSet? tokens)
    {
        if (tokens is null) return string.Empty;
        var declarations = new StringBuilder();
        void Color(string name, params string[] variables)
        {
            var value = tokens.Colors.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Hex;
            if (value is null || !Regex.IsMatch(value, "^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$")) return;
            foreach (var variable in variables) declarations.Append($"{variable}:{value};");
        }
        Color("Primary", "--cms-dark");
        Color("Accent", "--cms-accent", "--accent");
        Color("Surface", "--cms-surface", "--bg", "--bg-surface", "--bg-surface-2");
        Color("Text", "--cms-text", "--text-1");
        var sizes = new StringBuilder();
        foreach (var (name, selector) in new[] { ("Body", "body"), ("Heading", ".gws-heading"), ("Display", ".gws-hero-headline") })
        {
            var value = tokens.TypeScale.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.RemValue;
            if (value is not null && Regex.IsMatch(value, "^[0-9]+(\\.[0-9]+)?rem$"))
                sizes.Append($"{selector}{{font-size:{(name == "Display" ? $"clamp(2rem,6vw,{value})" : value)};}}");
        }
        if (declarations.Length == 0 && sizes.Length == 0) return string.Empty;
        return $":root{{{declarations}}}{sizes}" +
            "body{background:var(--bg,var(--cms-surface));}" +
            ":root{--text-2:color-mix(in srgb,var(--text-1) 78%,var(--bg));--text-3:color-mix(in srgb,var(--text-1) 65%,var(--bg));" +
            "--cms-text-soft:color-mix(in srgb,var(--cms-text) 78%,var(--cms-surface));--cms-text-faint:color-mix(in srgb,var(--cms-text) 65%,var(--cms-surface));" +
            "--border:color-mix(in srgb,var(--text-1) 20%,var(--bg));--cms-border:color-mix(in srgb,var(--cms-text) 20%,var(--cms-surface));" +
            "--accent-low:color-mix(in srgb,var(--accent) 12%,transparent);--accent-hover:color-mix(in srgb,var(--accent) 85%,black);}" +
            ".site-nav{background:color-mix(in srgb,var(--bg) 88%,transparent);}";
    }
}
