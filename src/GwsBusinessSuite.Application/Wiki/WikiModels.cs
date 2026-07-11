using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Wiki;

public sealed class WikiPageEditorModel
{
    public Guid? WikiPageId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Markdown { get; set; } = string.Empty;

    public Guid? ParentWikiPageId { get; set; }
}

// One entry per git commit touching a page's file - the source of truth for "history" is
// the git repo itself, not a DB table (unlike CmsPageRevision's bounded snapshot rows).
public sealed class WikiRevisionView
{
    public string Sha { get; init; } = string.Empty;
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
    public string Message { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public DateTimeOffset When { get; init; }
}
