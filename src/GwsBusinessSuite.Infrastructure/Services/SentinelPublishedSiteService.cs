using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Loads the published tree for a page share (SentinelPublishedSite holds the rules).
public sealed class SentinelPublishedSiteService(IAppDbContext dbContext) : ISentinelPublishedSiteService
{
    public async Task<SentinelPublishedSite.Site?> GetSiteAsync(SentinelShareView share, CancellationToken cancellationToken = default)
    {
        if (share.IsDatabase || share.IsAutomationWorkflow) return null;
        var nodes = await dbContext.WikiPages.AsNoTracking()
            .Where(page => share.IncludeSubpages || page.Id == share.TargetId)
            .Select(page => new { page.Id, page.ParentWikiPageId, page.SortOrder, page.Title, page.Icon, IsTrashed = page.TrashedAt != null })
            .ToListAsync(cancellationToken);
        var shared = share.IncludeSubpages
            ? (await dbContext.SentinelResourcePermissions.AsNoTracking()
                .Where(permission => !permission.IsDatabase)
                .Select(permission => permission.TargetId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet()
            : [];
        return SentinelPublishedSite.Build(
            share.TargetId,
            nodes.Select(node => new SentinelPublishedSite.Node(node.Id, node.ParentWikiPageId, node.SortOrder, node.Title, node.Icon,
                node.IsTrashed, node.Id != share.TargetId && shared.Contains(node.Id))).ToList(),
            share.SiteTitle);
    }

    public async Task<IReadOnlyList<SentinelPublishedSite.SearchResult>> SearchAsync(SentinelPublishedSite.Site site, string? query, CancellationToken cancellationToken = default)
    {
        if ((query ?? string.Empty).Trim().Length < 2) return [];
        var ids = site.Pages.Select(page => page.Id).ToList();
        var pages = await dbContext.WikiPages.AsNoTracking()
            .Where(page => ids.Contains(page.Id))
            .Select(page => new { page.Id, page.BlocksJson })
            .ToListAsync(cancellationToken);
        // Live business blocks are never published, so their labels aren't searchable either.
        var texts = pages.ToDictionary(page => page.Id, page => string.Join(" ",
            WikiBlockJson.ParseBlocks(page.BlocksJson)
                .Where(block => block.Type != WikiBlockTypes.Business)
                .Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 2000))
                .Where(text => text.Length > 0)));
        return SentinelPublishedSite.Search(site, texts, query);
    }
}
