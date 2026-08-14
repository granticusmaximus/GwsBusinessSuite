using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class QuickNoteService(IAppDbContext db, IWikiService wikiService) : IQuickNoteService
{
    public const string QuickNotesFolderSystemKey = "quick-notes";

    public async Task<WikiPage> AddQuickNoteAsync(string title, string markdownBody, string performedBy, CancellationToken cancellationToken = default)
    {
        var folder = await EnsureQuickNotesFolderAsync(performedBy, cancellationToken);

        var trimmedTitle = string.IsNullOrWhiteSpace(title) ? "Untitled note" : title.Trim();
        var note = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = trimmedTitle,
            Icon = "📝",
            ParentWikiPageId = folder.Id,
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromLegacyMarkdown(markdownBody))
        }, performedBy, cancellationToken: cancellationToken);

        await RebuildFolderIndexAsync(folder.Id, performedBy, cancellationToken);
        return note;
    }

    // Found by SystemKey (see WikiPage.SystemKey), not by title - a title is user-editable
    // and not unique, so it can't reliably identify "the" quick-notes folder across repeated
    // calls the way a stable key can.
    private async Task<WikiPage> EnsureQuickNotesFolderAsync(string performedBy, CancellationToken cancellationToken)
    {
        var existing = await db.WikiPages.FirstOrDefaultAsync(
            page => page.SystemKey == QuickNotesFolderSystemKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.TrashedAt is not null)
            {
                await wikiService.RestorePageAsync(existing.Id, performedBy, cancellationToken);
                existing.TrashedAt = null;
            }
            return existing;
        }

        var created = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Quick Notes",
            Icon = "🗒️",
            BlocksJson = WikiBlockJson.Serialize(
            [
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Notes saved from the dashboard's Quick Note button show up here.")],
                    new Dictionary<string, string>())
            ])
        }, performedBy, cancellationToken: cancellationToken);

        var folder = await db.WikiPages.FirstAsync(page => page.Id == created.Id, cancellationToken);
        folder.SystemKey = QuickNotesFolderSystemKey;
        await db.SaveChangesAsync(cancellationToken);
        return folder;
    }

    // Regenerates the folder's own body as a bulleted list of "wikilink:" links (Sentinel's
    // existing internal-page-mention scheme - see wiki-block-editor.js's navigateToWikiLink),
    // so opening the folder page always shows every current quick note as a clickable title,
    // without needing a dedicated "page link" block type this codebase doesn't otherwise have.
    private async Task RebuildFolderIndexAsync(Guid folderId, string performedBy, CancellationToken cancellationToken)
    {
        var folder = await db.WikiPages.FirstOrDefaultAsync(page => page.Id == folderId, cancellationToken);
        if (folder is null) return;

        var children = await db.WikiPages.AsNoTracking()
            .Where(page => page.ParentWikiPageId == folderId && page.TrashedAt == null)
            .ToListAsync(cancellationToken);
        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column - sort client-side.
        var orderedChildren = children.OrderByDescending(page => page.CreatedAt).ToList();

        var listBlocks = orderedChildren.Count == 0
            ? new List<WikiBlock>
            {
                new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("No quick notes yet - use the Quick Note button on the dashboard to add one.")],
                    new Dictionary<string, string>())
            }
            : orderedChildren.Select(page => new WikiBlock(
                Guid.NewGuid(), WikiBlockTypes.BulletedListItem, 0,
                [new WikiRichTextSpan(page.Title, Link: $"wikilink:{page.Id}")],
                new Dictionary<string, string>())).ToList();

        await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = folder.Id,
            Title = folder.Title,
            Slug = folder.Slug,
            Icon = folder.Icon,
            CoverImageUrl = folder.CoverImageUrl,
            ParentWikiPageId = folder.ParentWikiPageId,
            ExpectedContentVersion = folder.ContentVersion,
            BlocksJson = WikiBlockJson.Serialize(listBlocks)
            // createRevisionCheckpoint: false - same "silent background autosave" convention
            // IWikiService already documents, since this rewrite happens on every single note
            // save and isn't itself a user-authored edit worth its own revision entry.
        }, performedBy, createRevisionCheckpoint: false, cancellationToken: cancellationToken);
    }
}
