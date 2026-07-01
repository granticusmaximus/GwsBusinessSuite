namespace GwsBusinessSuite.Application.CmsBuilder;

// The schema stored in CmsPage.BlocksJson — a page is a list of sections, each split
// into columns, each holding widgets. Edited by the Studio (CmsBuilderEditor.razor) and
// rendered by three parallel renderers that must stay in sync: CmsBlockRenderer.jsx
// (live React site), CmsBlockHtmlRenderer.cs (server-rendered /cms/ route), and
// CmsBlockPreview.razor (admin preview).
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
}
