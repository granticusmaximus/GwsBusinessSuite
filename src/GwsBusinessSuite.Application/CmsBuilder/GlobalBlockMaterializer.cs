using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public static class GlobalBlockMaterializer
{
    private const string SectionPlacementWidgetSeparator = "__";

    public static LayoutWidget? DeserializeWidget(string json) =>
        CmsBuilderJson.Parse<LayoutWidget>(json);

    public static LayoutSection? DeserializeSection(string json) =>
        CmsBuilderJson.Parse<LayoutSection>(json);

    public static LayoutWidget CreateWidgetPlacement(GlobalBlock globalBlock)
    {
        var canonical = DeserializeWidget(globalBlock.Json);
        var placement = CloneWidget(canonical ?? new LayoutWidget
        {
            WidgetType = string.IsNullOrWhiteSpace(globalBlock.WidgetType) ? "paragraph" : globalBlock.WidgetType
        });
        placement.Id = Guid.NewGuid().ToString("N");
        placement.GlobalBlockId = globalBlock.Id;
        return placement;
    }

    public static LayoutSection CreateSectionPlacement(GlobalBlock globalBlock)
    {
        var canonical = DeserializeSection(globalBlock.Json) ?? new LayoutSection();
        var placementId = Guid.NewGuid().ToString("N");
        return new LayoutSection
        {
            Id = placementId,
            GlobalBlockId = globalBlock.Id,
            Label = canonical.Label,
            Background = canonical.Background,
            Padding = canonical.Padding,
            ColumnLayout = canonical.ColumnLayout,
            Columns = ClonePlacementColumns(canonical.Columns, placementId)
        };
    }

    public static LayoutWidget ToCanonicalWidget(LayoutWidget widget)
    {
        var canonical = CloneWidget(widget);
        canonical.GlobalBlockId = null;
        return canonical;
    }

    public static LayoutSection ToCanonicalSection(LayoutSection section)
    {
        return new LayoutSection
        {
            Id = section.Id,
            GlobalBlockId = null,
            Label = section.Label,
            Background = section.Background,
            Padding = section.Padding,
            ColumnLayout = section.ColumnLayout,
            Columns = section.Columns.Select(column => new LayoutColumn
            {
                Id = column.Id,
                Span = column.Span,
                Widgets = column.Widgets.Select(widget => ToCanonicalSectionChildWidget(section.Id, widget)).ToList()
            }).ToList()
        };
    }

    // overridableFields: Props keys this placement may hold its own diverged value for (see
    // LayoutWidget.Overrides) instead of the canonical's - every other key stays 100% synced
    // from the canonical, exactly as before per-instance overrides existed.
    public static void ApplyResolvedWidget(LayoutWidget placement, LayoutWidget canonical, IReadOnlyList<string> overridableFields)
    {
        placement.WidgetType = canonical.WidgetType;
        var mergedProps = new Dictionary<string, string>(canonical.Props);
        foreach (var key in overridableFields)
        {
            if (placement.Overrides.TryGetValue(key, out var overrideValue))
            {
                mergedProps[key] = overrideValue;
            }
        }
        placement.Props = mergedProps;
        placement.Style = CloneStyle(canonical.Style);
    }

    public static void ApplyResolvedSection(LayoutSection placement, LayoutSection canonical)
    {
        placement.Label = canonical.Label;
        placement.Background = canonical.Background;
        placement.Padding = canonical.Padding;
        placement.ColumnLayout = canonical.ColumnLayout;
        placement.Columns = ClonePlacementColumns(canonical.Columns, placement.Id);
    }

    private static LayoutWidget ToCanonicalSectionChildWidget(string sectionPlacementId, LayoutWidget widget)
    {
        var canonical = CloneWidget(widget);
        canonical.Id = StripPlacementWidgetPrefix(widget.Id, sectionPlacementId);
        if (widget.GlobalBlockId is not null)
        {
            // No overridableFields here (a global widget nested inside a global section, being
            // captured while saving the section itself as Global) - this synchronous helper has
            // no DB access to look up the nested widget's own block definition. Out of scope for
            // this first version of per-instance overrides; the nested widget still syncs/
            // resolves normally, just without override support at this specific nesting depth.
            return CreateWidgetPlaceholder(canonical);
        }

        canonical.GlobalBlockId = null;
        return canonical;
    }

    private static List<LayoutColumn> ClonePlacementColumns(IEnumerable<LayoutColumn> columns, string placementSectionId) =>
        columns.Select(column => new LayoutColumn
        {
            Id = column.Id,
            Span = column.Span,
            Widgets = column.Widgets.Select(widget =>
            {
                var clone = CloneWidget(widget);
                clone.Id = CreatePlacementWidgetId(placementSectionId, widget.Id);
                return clone;
            }).ToList()
        }).ToList();

    private static LayoutWidget CloneWidget(LayoutWidget widget) => new()
    {
        Id = widget.Id,
        GlobalBlockId = widget.GlobalBlockId,
        WidgetType = widget.WidgetType,
        Props = new Dictionary<string, string>(widget.Props),
        Style = CloneStyle(widget.Style)
    };

    private static WidgetStyle CloneStyle(WidgetStyle style) => new()
    {
        TextColor = style.TextColor,
        BackgroundColor = style.BackgroundColor,
        TextColorToken = style.TextColorToken,
        BackgroundColorToken = style.BackgroundColorToken,
        Padding = style.Padding,
        BorderRadius = style.BorderRadius,
        FontSize = style.FontSize,
        FontSizeToken = style.FontSizeToken
    };

    // overridableFields: for each key the referenced GlobalBlock has flagged overridable,
    // captures widget.Props' current value into the placeholder's Overrides so it survives the
    // collapse-to-placeholder step (Props itself is dropped here, same as every other key,
    // since Props gets fully re-derived from the canonical + Overrides on next resolve).
    public static LayoutWidget CreateWidgetPlaceholder(LayoutWidget widget, IReadOnlyList<string>? overridableFields = null)
    {
        var placeholder = new LayoutWidget
        {
            Id = widget.Id,
            GlobalBlockId = widget.GlobalBlockId,
            WidgetType = widget.WidgetType
        };

        if (overridableFields is { Count: > 0 })
        {
            foreach (var key in overridableFields)
            {
                if (widget.Props.TryGetValue(key, out var value))
                {
                    placeholder.Overrides[key] = value;
                }
            }
        }

        return placeholder;
    }

    public static LayoutSection CreateSectionPlaceholder(LayoutSection section) => new()
    {
        Id = section.Id,
        GlobalBlockId = section.GlobalBlockId
    };

    private static string CreatePlacementWidgetId(string sectionPlacementId, string widgetId) =>
        $"{sectionPlacementId}{SectionPlacementWidgetSeparator}{widgetId}";

    private static string StripPlacementWidgetPrefix(string widgetId, string sectionPlacementId)
    {
        var prefix = $"{sectionPlacementId}{SectionPlacementWidgetSeparator}";
        return widgetId.StartsWith(prefix, StringComparison.Ordinal)
            ? widgetId[prefix.Length..]
            : widgetId;
    }
}
