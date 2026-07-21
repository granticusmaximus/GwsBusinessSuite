using GwsBusinessSuite.Application.AppGeneration;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.DockerHealth;

namespace GwsBusinessSuite.Application.AdminPortal;

public sealed class AdminPortalSummaryService(
    ICommentService commentService,
    IContentStudioService contentStudioService,
    ICrmService crmService,
    IDockerHealthService dockerHealthService,
    IAppGenerationService appGenerationService) : IAdminPortalSummaryService
{
    private Task<AdminPortalSummary>? _summaryTask;

    public Task<AdminPortalSummary> GetAsync(bool includeAdminMetrics, CancellationToken cancellationToken = default) =>
        _summaryTask ??= LoadAsync(includeAdminMetrics, cancellationToken);

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
