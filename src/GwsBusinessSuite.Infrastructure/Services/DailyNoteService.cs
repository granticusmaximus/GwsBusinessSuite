using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DailyNoteService(IAppDbContext db, IWikiService wikiService, TimeProvider timeProvider) : IDailyNoteService
{
    public async Task<WikiPage> OpenOrCreateAsync(DateOnly date, string performedBy, CancellationToken cancellationToken = default)
    {
        var key = DailyNotes.SystemKeyFor(date);
        var existing = await db.WikiPages.FirstOrDefaultAsync(page => page.SystemKey == key, cancellationToken);
        if (existing is not null)
        {
            if (existing.TrashedAt is not null)
            {
                await wikiService.RestorePageAsync(existing.Id, performedBy, cancellationToken);
                existing.TrashedAt = null;
            }
            return existing;
        }

        var folder = await EnsureFolderAsync(performedBy, cancellationToken);
        var template = await db.SentinelPageTemplates.AsNoTracking()
            .FirstOrDefaultAsync(item => item.UseForDailyNotes, cancellationToken);
        // Fresh block ids, as SentinelTemplateService.CreatePageAsync does, so no two days share one.
        var blocks = template is null
            ? []
            : WikiBlockJson.ParseBlocks(template.BlocksJson).Select(block => block with { Id = Guid.NewGuid() }).ToList();

        var created = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = DailyNotes.TitleFor(date),
            Icon = template?.Icon ?? "📅",
            CoverImageUrl = template?.CoverImageUrl,
            ParentWikiPageId = folder.Id,
            BlocksJson = WikiBlockJson.Serialize(blocks)
        }, performedBy, cancellationToken: cancellationToken);

        var page = await db.WikiPages.FirstAsync(item => item.Id == created.Id, cancellationToken);
        page.SystemKey = key;
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task<WikiPage> AppendAsync(
        DateOnly date,
        string title,
        string markdownBody,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var page = await OpenOrCreateAsync(date, performedBy, cancellationToken);
        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson).ToList();
        var heading = $"{timeProvider.GetLocalNow():HH:mm} {(string.IsNullOrWhiteSpace(title) ? "Note" : title.Trim())}";
        blocks.Add(new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Heading3, 0, [new WikiRichTextSpan(heading)], new Dictionary<string, string>()));
        blocks.AddRange(WikiBlockJson.FromMarkdown(markdownBody));

        return await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id,
            Title = page.Title,
            Slug = page.Slug,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            ParentWikiPageId = page.ParentWikiPageId,
            IsFullWidth = page.IsFullWidth,
            FontStyle = page.FontStyle,
            ExpectedContentVersion = page.ContentVersion,
            BaseBlocksJson = page.BlocksJson,
            BlocksJson = WikiBlockJson.Serialize(blocks)
        }, performedBy, cancellationToken: cancellationToken);
    }

    // Found by SystemKey, like QuickNoteService's folder.
    private async Task<WikiPage> EnsureFolderAsync(string performedBy, CancellationToken cancellationToken)
    {
        var existing = await db.WikiPages.FirstOrDefaultAsync(page => page.SystemKey == DailyNotes.FolderSystemKey, cancellationToken);
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
            Title = "Daily notes",
            Icon = "🗓️",
            BlocksJson = WikiBlockJson.Serialize(
            [
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("One page per day. Open today's with the Today button or Cmd/Ctrl+Shift+D.")],
                    new Dictionary<string, string>())
            ])
        }, performedBy, cancellationToken: cancellationToken);

        var folder = await db.WikiPages.FirstAsync(page => page.Id == created.Id, cancellationToken);
        folder.SystemKey = DailyNotes.FolderSystemKey;
        await db.SaveChangesAsync(cancellationToken);
        return folder;
    }
}
