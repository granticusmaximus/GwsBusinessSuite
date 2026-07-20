using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Wiki;

public sealed class WikiPageEditorModel
{
    public Guid? WikiPageId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string BlocksJson { get; set; } = "[]";

    public string? Icon { get; set; }

    public string? CoverImageUrl { get; set; }

    public Guid? ParentWikiPageId { get; set; }
}

// One row per save, bounded to WikiService.MaxRevisionsPerPage (oldest trimmed on write) -
// a DB-snapshot equivalent of CmsPageRevision, replacing the old unbounded git-log history.
public sealed class WikiRevisionView
{
    public Guid Id { get; init; }
    public int RevisionNumber { get; init; }
    public string Label { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public DateTimeOffset When { get; init; }
}

// Returned to wiki-block-editor.js's [[ ]] autocomplete - carries the page id alongside the
// title so the JS side can insert a wikilink:{id} href directly, with no second round-trip
// to resolve an id from the chosen title.
public sealed record WikiLinkSuggestion(Guid Id, string Title);
