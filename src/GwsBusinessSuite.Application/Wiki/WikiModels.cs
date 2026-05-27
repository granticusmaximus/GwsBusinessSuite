using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Wiki;

public sealed class WikiPageEditorModel
{
    public Guid? WikiPageId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Markdown { get; set; } = string.Empty;
}
