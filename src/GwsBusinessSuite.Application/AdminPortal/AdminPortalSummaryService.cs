using GwsBusinessSuite.Application.AppGeneration;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.DockerHealth;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Application.AdminPortal;

public sealed class AdminPortalSummaryService(
    ICommentService commentService,
    IContentStudioService contentStudioService,
    ICrmService crmService,
    IDockerHealthService dockerHealthService,
    IAppGenerationService appGenerationService,
    IMemoryCache? cache = null) : IAdminPortalSummaryService
{
    // Was an unconditional `_summaryTask ??= LoadAsync(...)` field - correct for one page load,
    // but this service is Scoped and Blazor Server's DI scope is per-circuit, not per-render:
    // once computed, every later call for the rest of that user's session returned the same
    // stale counts, even as new comments/drafts/alerts genuinely arrived. Keyed by
    // includeAdminMetrics so the admin and non-admin variants (NavMenu.razor vs Home.razor,
    // called with different values) never collide, and bounded to a short TTL so the numbers
    // catch up again on the next page view rather than staying frozen for the whole session.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private readonly IMemoryCache _cache = cache ?? new MemoryCache(new MemoryCacheOptions());

    public Task<AdminPortalSummary> GetAsync(bool includeAdminMetrics, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"admin-portal-summary:{includeAdminMetrics}";
        if (_cache.TryGetValue(cacheKey, out Task<AdminPortalSummary>? cached) && cached is not null)
        {
            return cached;
        }

        var task = LoadAsync(includeAdminMetrics, cancellationToken);
        _cache.Set(cacheKey, task, CacheDuration);
        return task;
    }

    private async Task<AdminPortalSummary> LoadAsync(bool includeAdminMetrics, CancellationToken cancellationToken)
    {
        var draftsTask = contentStudioService.CountPendingReviewAsync(cancellationToken);
        var commentsTask = commentService.CountPendingAsync(cancellationToken);
        var followUpsTask = includeAdminMetrics
            ? crmService.CountDueFollowUpsAsync(cancellationToken)
            : Task.FromResult(0);
        var alertsTask = includeAdminMetrics
            ? dockerHealthService.CountUnreadAlertsAsync(cancellationToken)
            : Task.FromResult(0);
        var approvalsTask = includeAdminMetrics
            ? appGenerationService.CountPendingApprovalAsync(cancellationToken)
            : Task.FromResult(0);

        await Task.WhenAll(draftsTask, commentsTask, followUpsTask, alertsTask, approvalsTask);
        return new AdminPortalSummary(
            await draftsTask,
            await commentsTask,
            await followUpsTask,
            await alertsTask,
            await approvalsTask);
    }
}
