using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class CmsSiteEditorModel
{
    public Guid? SiteId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Theme { get; set; } = "Default";

    public string CustomCss { get; set; } = string.Empty;

    public string NavMenuJson { get; set; } = "[]";
}

public sealed class CmsPageEditorModel
{
    public Guid? PageId { get; set; }

    [Required]
    public Guid? SiteId { get; set; }

    public Guid? ParentPageId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    [Required]
    public string BlocksJson { get; set; } = "[]";

    public string MetaTitle { get; set; } = string.Empty;

    public string MetaDescription { get; set; } = string.Empty;

    public string OgImageUrl { get; set; } = string.Empty;

    public string CustomCss { get; set; } = string.Empty;
}

public sealed record NavMenuItem(string Id, string Label, string Href, bool OpenInNewTab);

public sealed class CmsWorkflowBlueprintSummary
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
