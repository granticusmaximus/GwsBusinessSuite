using System.Globalization;

namespace GwsBusinessSuite.Application.CmsBuilder;

// The schema stored in CmsPage.BlocksJson — a page is a list of sections, each split
// into columns, each holding widgets. Canvas Studio edits this in CmsBuilderEditor.razor;
// the real page render comes from CmsBlockHtmlRenderer.cs, and CmsBlockPreview.razor still
// exists only as a simplified revision-history preview in EditPage.razor.
public sealed class PageLayout
{
    public List<LayoutSection> Sections { get; set; } = new();
}

public sealed class LayoutSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid? GlobalBlockId { get; set; }
    public string Label { get; set; } = "Section";

    // transparent | light | dark | accent
    public string Background { get; set; } = "transparent";

    // none | sm | md | lg | xl
    public string Padding { get; set; } = "md";

    // full | half-half | one-third-two-thirds | two-thirds-one-third | thirds
    public string ColumnLayout { get; set; } = "full";

    // Client-safe structural locking - see CmsEditPermissions. Defaults to Inherit, meaning
    // "use the page's own setting" - so an existing page with no lock configuration anywhere
    // behaves exactly as it always has (fully editable by any Contributor).
    public string EditPermission { get; set; } = CmsEditPermissions.Inherit;

    // Phase 4 (Freeform Canvas Layout) - see CmsSectionLayoutModes. Flow (the default) keeps
    // today's ColumnLayout/Columns-driven behavior completely unchanged. Freeform ignores
    // ColumnLayout's multi-column split - every widget lives in Columns[0] and is positioned
    // by its own LayoutWidget.Freeform box instead.
    public string LayoutMode { get; set; } = CmsSectionLayoutModes.Flow;

    // Only meaningful when LayoutMode == Freeform. A fixed pixel height for the positioning
    // canvas - percentage-based child positions (LayoutWidget.Freeform) need a concrete box
    // to be percentages OF, and absolutely-positioned children don't contribute to a parent's
    // natural height the way flow children do.
    public int FreeformHeightPx { get; set; } = 480;

    public List<LayoutColumn> Columns { get; set; } = new();
}

// Phase 4 (Freeform Canvas Layout) - an opt-in per-section alternative to the normal
// column-flow layout, for pages that need to place widgets anywhere on a canvas (overlapping
// hero graphics, custom collages) rather than stacked in columns - a common WordPress-builder
// gap this app's CMS didn't have until now. See LayoutSection.LayoutMode/FreeformHeightPx and
// LayoutWidget.Freeform.
public static class CmsSectionLayoutModes
{
    public const string Flow = "Flow";
    public const string Freeform = "Freeform";
}

// A widget's position/size within its parent Freeform-mode section, as percentages of the
// section's FreeformHeightPx-tall canvas (X/Y/Width/Height all 0-100) so it stays proportional
// if the canvas is later resized. Z is a plain stacking-order tiebreak for overlapping widgets
// (higher paints on top) - not a percentage, no clamping needed beyond "is a number".
// Only meaningful when the parent LayoutSection.LayoutMode == Freeform; ignored otherwise.
public sealed class FreeformPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 30;
    public double Height { get; set; } = 20;
    public int Z { get; set; }

    // A simple deterministic scatter so several widgets switched into Freeform (or dropped
    // onto a Freeform canvas) at once don't all start stacked exactly on top of each other.
    public static FreeformPosition DefaultFor(int index) => new()
    {
        X = 5 + index * 12 % 55,
        Y = 5 + index * 18 % 45,
        Width = 35,
        Height = 22
    };

    // Keeps the box fully inside the 0-100 canvas and above a sane minimum size, after a
    // hand-typed Inspector value or a drag/resize gesture.
    public void Clamp()
    {
        Width = Math.Clamp(Width, 5, 100);
        Height = Math.Clamp(Height, 5, 100);
        X = Math.Clamp(X, 0, 100 - Width);
        Y = Math.Clamp(Y, 0, 100 - Height);
    }

    public string ToInlineStyle() => string.Create(CultureInfo.InvariantCulture,
        $"left:{X}%;top:{Y}%;width:{Width}%;height:{Height}%;z-index:{Z};");
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
    public Guid? GlobalBlockId { get; set; }

    // heading | paragraph | button | image | hero | card | form | spacer | divider | html
    public string WidgetType { get; set; } = "paragraph";

    public Dictionary<string, string> Props { get; set; } = new();

    // Per-widget style overrides (Elementor-style "Style" tab) — applies uniformly across
    // every widget type, layered on top of Props (content) and the site's global design
    // tokens (Phase 5). A widget with every field left at its default renders with zero
    // extra markup — see ToInlineStyle — so existing pages are visually unaffected.
    public WidgetStyle Style { get; set; } = new();

    // Part 6.3 (OrchardCore-style Layers) — a simple, no-code condition on whether this
    // widget renders at all. Evaluated by CmsBlockHtmlRenderer.ShouldRenderWidget; a widget
    // with Mode == Always (the default) always renders, so existing pages are unaffected.
    public VisibilityRule Visibility { get; set; } = new();

    // Client-safe structural locking - see CmsEditPermissions. Defaults to Inherit (falls
    // through to the parent section, then the page); has no effect on public rendering, only
    // on what Canvas Studio lets a non-Admin Contributor do with this widget.
    public string EditPermission { get; set; } = CmsEditPermissions.Inherit;

    // Phase 3 (per-instance overrides) - only meaningful when GlobalBlockId is set and the
    // referenced GlobalBlock has marked a key as overridable (GlobalBlock.OverridableFieldsJson).
    // Holds THIS placement's own diverged value for such a key, keyed by Props key name; a key
    // absent here stays 100% synced to the shared canonical, same as before this existed. See
    // GlobalBlockMaterializer.ApplyResolvedWidget (read/merge) and GlobalBlockService
    // .SyncWidgetAsync (write/preserve-canonical).
    public Dictionary<string, string> Overrides { get; set; } = new();

    // Phase 4 (Freeform Canvas Layout) - this widget's own position/size within its parent
    // section, only meaningful when that section's LayoutMode == Freeform. Null means "not
    // positioned yet" (a widget still living in a Flow section, or one about to be
    // materialized with a default box the moment its section switches to Freeform or it's
    // first dropped onto one) - see CmsBuilderEditor.razor's MaterializeFreeformPositionIfNeeded.
    public FreeformPosition? Freeform { get; set; }
}

// Client-safe structural locking (Phase 2): an agency builds a site as Admin, then wants to
// hand editing of specific parts to a client who only has the Contributor role. Locked = the
// Contributor can't select/edit/move/delete/restyle it at all; ContentOnly = they can edit its
// text/image/link content but not move/delete/restyle/change visibility; Open = fully editable,
// same as today's unrestricted behavior. Admin always has full access regardless of this
// setting - it constrains Contributor only. Resolved top-down (widget -> section -> page),
// mirroring WidgetStyle's own "empty/default = inherit, explicit value = override" convention -
// see CmsEditPermissionResolver.
public static class CmsEditPermissions
{
    public const string Inherit = "Inherit";
    public const string Locked = "Locked";
    public const string ContentOnly = "ContentOnly";
    public const string Open = "Open";

    public static readonly string[] WidgetAndSectionOptions = [Inherit, Locked, ContentOnly, Open];
    public static readonly string[] PageOptions = [Open, ContentOnly, Locked];
}

public static class CmsEditPermissionResolver
{
    // Widget wins if it has an explicit (non-Inherit) value, else section, else the page's own
    // default (which is never Inherit - CmsPage.EditPermission always holds a real value).
    public static string ResolveForWidget(string pageDefault, string sectionValue, string widgetValue)
    {
        if (IsExplicit(widgetValue)) return widgetValue;
        if (IsExplicit(sectionValue)) return sectionValue;
        return NormalizePageDefault(pageDefault);
    }

    public static string ResolveForSection(string pageDefault, string sectionValue) =>
        IsExplicit(sectionValue) ? sectionValue : NormalizePageDefault(pageDefault);

    private static bool IsExplicit(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != CmsEditPermissions.Inherit;

    private static string NormalizePageDefault(string pageDefault) =>
        string.IsNullOrWhiteSpace(pageDefault) || pageDefault == CmsEditPermissions.Inherit ? CmsEditPermissions.Open : pageDefault;
}

public sealed class VisibilityRule
{
    public string Mode { get; set; } = VisibilityModes.Always;

    // Only meaningful when Mode == UrlPattern. A simple glob ("*" matches any run of
    // characters) matched against the page's full path (e.g. "blog/*", "services") -
    // deliberately not a general regex, consistent with this codebase's preference for the
    // narrowest matching mechanism that actually works (see CreateSlug, etc.).
    public string UrlPattern { get; set; } = string.Empty;
}

public static class VisibilityModes
{
    public const string Always = "Always";
    public const string LoggedInOnly = "LoggedInOnly";
    public const string HomepageOnly = "HomepageOnly";
    public const string UrlPattern = "UrlPattern";

    public static readonly IReadOnlyList<string> All = [Always, LoggedInOnly, HomepageOnly, UrlPattern];
}

public sealed class WidgetStyle
{
    // Hex color, empty = inherit from the global design tokens / surrounding theme.
    public string TextColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";

    // Optional named reference into the site's DesignTokenSet (see DesignTokenModels.cs), e.g.
    // "Primary" - when set and a matching token exists, it takes precedence over the raw
    // TextColor/BackgroundColor value above at render time, so changing the token once cascades
    // everywhere it's referenced. Empty = use the raw value (or nothing, if that's also empty).
    public string TextColorToken { get; set; } = "";
    public string BackgroundColorToken { get; set; } = "";

    // none | sm | md | lg | xl — same scale as LayoutSection.Padding, for consistency.
    public string Padding { get; set; } = "none";

    // none | sm | md | lg | full
    public string BorderRadius { get; set; } = "none";

    // default | sm | md | lg | xl — scales the widget's base font-size; "default" leaves
    // the widget type's own natural size untouched.
    public string FontSize { get; set; } = "default";

    // Optional named reference into the site's DesignTokenSet's type scale - same precedence
    // convention as TextColorToken: takes over from FontSize's fixed scale when set and a
    // matching step exists.
    public string FontSizeToken { get; set; } = "";

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
        || !string.IsNullOrWhiteSpace(TextColorToken) || !string.IsNullOrWhiteSpace(BackgroundColorToken)
        || Padding != "none" || BorderRadius != "none" || FontSize != "default"
        || !string.IsNullOrWhiteSpace(FontSizeToken);

    // Builds the inline style="" attribute value for a wrapper element around the widget's
    // rendered content. Returns "" (no wrapper needed) when nothing is overridden. `tokens` is
    // optional so every existing call site keeps working unchanged - passing none simply means
    // *Token fields never resolve and the raw values are used, same as before this existed.
    public string ToInlineStyle(DesignTokenSet? tokens = null)
    {
        if (!HasAnyOverride)
        {
            return "";
        }

        var parts = new List<string>();
        var textColor = ResolveColor(TextColorToken, TextColor, tokens);
        if (!string.IsNullOrWhiteSpace(textColor)) parts.Add($"color:{textColor}");
        var backgroundColor = ResolveColor(BackgroundColorToken, BackgroundColor, tokens);
        if (!string.IsNullOrWhiteSpace(backgroundColor)) parts.Add($"background-color:{backgroundColor}");
        if (Padding != "none" && PaddingRems.TryGetValue(Padding, out var pad)) parts.Add($"padding:{pad}");
        if (BorderRadius != "none" && BorderRadiusPx.TryGetValue(BorderRadius, out var radius)) parts.Add($"border-radius:{radius}");
        var fontSize = ResolveFontSize(tokens);
        if (!string.IsNullOrWhiteSpace(fontSize)) parts.Add($"font-size:{fontSize}");

        return string.Join(';', parts);
    }

    private static string? ResolveColor(string tokenName, string rawValue, DesignTokenSet? tokens)
    {
        if (!string.IsNullOrWhiteSpace(tokenName) && tokens is not null)
        {
            var match = tokens.Colors.FirstOrDefault(color => string.Equals(color.Name, tokenName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Hex;
        }
        return string.IsNullOrWhiteSpace(rawValue) ? null : rawValue;
    }

    private string? ResolveFontSize(DesignTokenSet? tokens)
    {
        if (!string.IsNullOrWhiteSpace(FontSizeToken) && tokens is not null)
        {
            var step = tokens.TypeScale.FirstOrDefault(item => string.Equals(item.Name, FontSizeToken, StringComparison.OrdinalIgnoreCase));
            if (step is not null) return step.RemValue;
        }
        return FontSize != "default" && FontSizeRems.TryGetValue(FontSize, out var size) ? size : null;
    }
}
