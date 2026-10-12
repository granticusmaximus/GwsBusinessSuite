using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Related notes + unlinked mentions (Sentinel item 3). Both lists only ever show pages the user
// can view; linking edits the other page through IWikiService.SavePageAsync (save rules, a
// revision) and needs edit access to it.
public sealed class SentinelConnectionsService(
    IAppDbContext dbContext,
    IWikiService wikiService,
    ISentinelAccessService? accessService = null,
    IHybridSearchService? hybridSearch = null) : ISentinelConnectionsService
{
    public const int MaxRelated = 6;
    public const int MaxMentions = 25;
    private const int MaxScannedPages = 200;

    public async Task<IReadOnlyList<SentinelRelatedPage>> GetRelatedAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        if (hybridSearch is null || await LoadViewableAsync(pageId, username, cancellationToken) is not { } page) return [];
        var opening = string.Join(' ', WikiBlockJson.ParseBlocks(page.BlocksJson).Select(b => b.PlainText)).Trim();
        var query = $"{page.Title} {(opening.Length > 400 ? opening[..400] : opening)}".Trim();
        if (query.Length == 0) return [];
        var hits = (await hybridSearch.SearchAsync(query, [SemanticSourceTypes.WikiPage], 15, cancellationToken))
            .Where(h => h.SourceId != pageId).DistinctBy(h => h.SourceId).ToList();
        var visible = await VisibleAsync(hits.Select(h => h.SourceId), username, cancellationToken);
        var ids = hits.Select(h => h.SourceId).Where(visible.Contains).ToList();
        var pages = await dbContext.WikiPages.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrashedAt == null)
            .Select(p => new { p.Id, p.Title, p.Icon }).ToDictionaryAsync(p => p.Id, cancellationToken);
        return hits.Where(h => pages.ContainsKey(h.SourceId)).Take(MaxRelated)
            .Select(h => new SentinelRelatedPage(h.SourceId, pages[h.SourceId].Title, pages[h.SourceId].Icon, h.Preview)).ToList();
    }

    public async Task<IReadOnlyList<SentinelUnlinkedMention>> GetUnlinkedMentionsAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        if (await LoadViewableAsync(pageId, username, cancellationToken) is not { } page) return [];
        var names = SentinelMentions.Names(page.Title, page.Aliases);
        if (names.Count == 0) return [];

        // Narrow in SQLite by each name, then match whole words in the parsed blocks.
        var candidateIds = new HashSet<Guid>();
        foreach (var name in names)
        {
            var lowered = name.ToLower();
            candidateIds.UnionWith(await dbContext.WikiPages.AsNoTracking()
                .Where(p => p.Id != pageId && p.TrashedAt == null && p.BlocksJson.ToLower().Contains(lowered))
                .Select(p => p.Id).Take(MaxScannedPages).ToListAsync(cancellationToken));
        }
        var visible = await VisibleAsync(candidateIds, username, cancellationToken);
        var ids = candidateIds.Where(visible.Contains).Take(MaxScannedPages).ToList();
        var sources = await dbContext.WikiPages.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

        var mentions = new List<SentinelUnlinkedMention>();
        foreach (var source in sources.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt))
        {
            foreach (var block in WikiBlockJson.ParseBlocks(source.BlocksJson))
            {
                if (SentinelMentions.FindUnlinked(block, names) is not { } name) continue;
                mentions.Add(new SentinelUnlinkedMention(source.Id, string.IsNullOrWhiteSpace(source.Title) ? "Untitled" : source.Title,
                    block.Id, name, SentinelMentions.Snippet(block.PlainText, name)));
                if (mentions.Count >= MaxMentions) return mentions;
            }
        }
        return mentions;
    }

    public async Task<bool> LinkMentionAsync(Guid sourcePageId, Guid blockId, Guid targetPageId, string username, CancellationToken cancellationToken = default)
    {
        if (accessService is not null
            && !await accessService.CanAccessAsync(sourcePageId, isDatabase: false, username, SentinelAccessLevels.Edit, cancellationToken))
            throw new UnauthorizedAccessException("You need edit access to that page to add the link.");
        var target = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetPageId && p.TrashedAt == null, cancellationToken);
        var source = await wikiService.GetPageAsync(sourcePageId, cancellationToken);
        if (target is null || source is null || source.TrashedAt is not null) return false;

        var blocks = WikiBlockJson.ParseBlocks(source.BlocksJson).ToList();
        var index = blocks.FindIndex(b => b.Id == blockId);
        if (index < 0 || SentinelMentions.LinkFirst(blocks[index], SentinelMentions.Names(target.Title, target.Aliases), targetPageId) is not { } linked)
            return false;
        blocks[index] = linked;
        await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = source.Id,
            ExpectedContentVersion = source.ContentVersion,
            Title = source.Title,
            Slug = source.Slug,
            BlocksJson = WikiBlockJson.Serialize(blocks),
            BaseBlocksJson = source.BlocksJson,
            Icon = source.Icon,
            CoverImageUrl = source.CoverImageUrl,
            ParentWikiPageId = source.ParentWikiPageId,
            IsFullWidth = source.IsFullWidth,
            FontStyle = source.FontStyle
        }, username, createRevisionCheckpoint: true, cancellationToken);
        return true;
    }

    private async Task<WikiPage?> LoadViewableAsync(Guid pageId, string username, CancellationToken ct)
    {
        var page = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId && p.TrashedAt == null, ct);
        if (page is null) return null;
        return accessService is null || await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.View, ct) ? page : null;
    }

    private async Task<HashSet<Guid>> VisibleAsync(IEnumerable<Guid> ids, string username, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (accessService is null) return list.ToHashSet();
        var allowed = await accessService.GetAccessibleTargetsAsync(list.Select(id => new SentinelAccessTarget(id, IsDatabase: false)).ToList(), username, SentinelAccessLevels.View, ct);
        return allowed.Select(t => t.TargetId).ToHashSet();
    }
}
