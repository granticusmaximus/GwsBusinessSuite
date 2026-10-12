using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Hover previews: the same View check as opening the page, so a preview never shows a page the
// hovering user couldn't open.
public sealed class SentinelPagePreviewService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null) : ISentinelPagePreviewService
{
    public async Task<SentinelPagePreview?> GetAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var page = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId && p.TrashedAt == null, cancellationToken);
        if (page is null) return null;
        if (accessService is not null
            && !await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.View, cancellationToken))
            return null;
        return SentinelPagePreviewBuilder.Build(page, WikiBlockJson.ParseBlocks(page.BlocksJson), timeProvider.GetUtcNow());
    }
}
