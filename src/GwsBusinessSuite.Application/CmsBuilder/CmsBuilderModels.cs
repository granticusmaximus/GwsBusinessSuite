using System.ComponentModel.DataAnnotations;
using GwsBusinessSuite.Domain.Entities;

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

    public string FooterNavMenuJson { get; set; } = "[]";

    public string AccentColorHex { get; set; } = "#f59e0b";

    public string FontPairingKey { get; set; } = CmsFontPairings.Elegant;

    public string LogoUrl { get; set; } = string.Empty;

    public string FaviconUrl { get; set; } = string.Empty;
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

    public string CanonicalUrl { get; set; } = string.Empty;

    public string CategoryName { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public string CustomCss { get; set; } = string.Empty;

    public string Status { get; set; } = CmsPageStatuses.Draft;

    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed record NavMenuItem(string Id, string Label, string Href, bool OpenInNewTab);

public sealed class CmsWorkflowBlueprintSummary
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
