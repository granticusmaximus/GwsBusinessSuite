namespace GwsBusinessSuite.Application.CmsBuilder;

// The schema stored in CmsPage.BlocksJson — a page is a list of sections, each split
// into columns, each holding widgets. Edited by the Studio (CmsBuilderEditor.razor) and
// rendered by two renderers that must stay in sync: CmsBlockHtmlRenderer.cs (the live,
// server-rendered page) and CmsBlockPreview.razor (the admin Studio's preview pane).
// (A third renderer, CmsBlockRenderer.jsx in the now-retired apps/public-site React app,
// no longer runs — grantwatson.dev is served natively by this Blazor app.)
public sealed class PageLayout
{
    public List<LayoutSection> Sections { get; set; } = new();
}

public sealed class LayoutSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Section";

    // transparent | light | dark | accent
    public string Background { get; set; } = "transparent";

    // none | sm | md | lg | xl
    public string Padding { get; set; } = "md";

    // full | half-half | one-third-two-thirds | two-thirds-one-third | thirds
    public string ColumnLayout { get; set; } = "full";

    public List<LayoutColumn> Columns { get; set; } = new();
}

public sealed class LayoutColumn
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Span { get; set; } = 12;
    public List<LayoutWidget> Widgets { get; set; } = new();
}

public sealed class LayoutWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // heading | paragraph | button | image | hero | card | form | spacer | divider | html
    public string WidgetType { get; set; } = "paragraph";

    public Dictionary<string, string> Props { get; set; } = new();

    // Per-widget style overrides (Elementor-style "Style" tab) — applies uniformly across
    // every widget type, layered on top of Props (content) and the site's global design
    // tokens (Phase 5). A widget with every field left at its default renders with zero
    // extra markup — see ToInlineStyle — so existing pages are visually unaffected.
    public WidgetStyle Style { get; set; } = new();
}

public sealed class WidgetStyle
{
    // Hex color, empty = inherit from the global design tokens / surrounding theme.
    public string TextColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";

    // none | sm | md | lg | xl — same scale as LayoutSection.Padding, for consistency.
    public string Padding { get; set; } = "none";

    // none | sm | md | lg | full
    public string BorderRadius { get; set; } = "none";

    // default | sm | md | lg | xl — scales the widget's base font-size; "default" leaves
    // the widget type's own natural size untouched.
    public string FontSize { get; set; } = "default";

    private static readonly Dictionary<string, string> PaddingRems = new()
    {
        ["sm"] = "0.75rem", ["md"] = "1.5rem", ["lg"] = "2.5rem", ["xl"] = "4rem"
    };

    private static readonly Dictionary<string, string> BorderRadiusPx = new()
    {
        ["sm"] = "6px", ["md"] = "12px", ["lg"] = "20px", ["full"] = "999px"
    };

    private static readonly Dictionary<string, string> FontSizeRems = new()
    {
        ["sm"] = "0.875rem", ["md"] = "1.125rem", ["lg"] = "1.375rem", ["xl"] = "1.75rem"
    };

    public bool HasAnyOverride =>
        !string.IsNullOrWhiteSpace(TextColor) || !string.IsNullOrWhiteSpace(BackgroundColor)
        || Padding != "none" || BorderRadius != "none" || FontSize != "default";

    // Builds the inline style="" attribute value for a wrapper element around the widget's
    // rendered content. Returns "" (no wrapper needed) when nothing is overridden.
    public string ToInlineStyle()
    {
        if (!HasAnyOverride)
        {
            return "";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(TextColor)) parts.Add($"color:{TextColor}");
        if (!string.IsNullOrWhiteSpace(BackgroundColor)) parts.Add($"background-color:{BackgroundColor}");
        if (Padding != "none" && PaddingRems.TryGetValue(Padding, out var pad)) parts.Add($"padding:{pad}");
        if (BorderRadius != "none" && BorderRadiusPx.TryGetValue(BorderRadius, out var radius)) parts.Add($"border-radius:{radius}");
        if (FontSize != "default" && FontSizeRems.TryGetValue(FontSize, out var size)) parts.Add($"font-size:{size}");

        return string.Join(';', parts);
    }
}
