namespace GwsBusinessSuite.Application.AdminPortal;

public interface IAdminPortalSummaryService
{
    Task<AdminPortalSummary> GetAsync(bool includeApprovalQueue, CancellationToken cancellationToken = default);
}

public sealed record AdminPortalSummary(
    int PendingDrafts,
    int PendingComments,
    int DueFollowUps,
    int UnreadSystemAlerts,
    int PendingAppApprovals);
