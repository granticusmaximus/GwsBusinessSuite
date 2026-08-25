using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.DeveloperApi;

// Deliberately its own small, independent implementation rather than a refactor of
// SentinelAiService.ExecuteToolCallAsync's search_wiki/get_page cases - purely additive, zero
// risk to that 1991-line orchestration engine.
public sealed class DeveloperApiSentinelReadService(
    IAppDbContext db,
    ISentinelWorkspaceService workspaceService,
    ISentinelAccessService accessService) : IDeveloperApiSentinelReadService
{
    public async Task<IReadOnlyList<DeveloperApiSentinelSearchResult>> SearchWikiAsync(
        string query, string ownerUsername, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = await workspaceService.SearchAsync(query, ownerUsername, maxResults: 10, cancellationToken);
        return results.Select(result => new DeveloperApiSentinelSearchResult(result.Id, result.IsDatabase, result.Title, result.Preview)).ToList();
    }

    public async Task<DeveloperApiSentinelPage?> GetPageAsync(
        Guid pageId, string ownerUsername, CancellationToken cancellationToken = default)
    {
        if (!await accessService.CanAccessAsync(pageId, isDatabase: false, ownerUsername, SentinelAccessLevels.View, cancellationToken))
            return null;

        var page = await db.WikiPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null) return null;

        var text = string.Join(" ", WikiBlockJson.ParseBlocks(page.BlocksJson).Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 500)));
        return new DeveloperApiSentinelPage(page.Id, page.Title, Limit(text, 4_000));
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
