using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class CmsSiteEditorModel
{
    public Guid? SiteId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Theme { get; set; } = "Default";
}

public sealed class CmsPageEditorModel
{
    public Guid? PageId { get; set; }

    [Required]
    public Guid? SiteId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    [Required]
    public string BlocksJson { get; set; } = "[]";
}
