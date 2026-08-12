using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.MissionControl;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class MissionControlService(
    IAutomationWorkflowService automationWorkflowService,
    ICrmService crmService,
    IAffiliateAnalyticsService affiliateAnalyticsService,
    IDockerHealthService dockerHealthService,
    TimeProvider timeProvider) : IMissionControlService
{
    public async Task<MissionControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var automationFailures = await automationWorkflowService.ListRecentFailuresAsync(5, cancellationToken);
        var crm = await crmService.GetDashboardAsync(cancellationToken);
        var affiliate = await affiliateAnalyticsService.GetDashboardAsync(cancellationToken);
        var dockerUnreadAlerts = await dockerHealthService.CountUnreadAlertsAsync(cancellationToken);

        return new MissionControlSnapshot(
            automationFailures.Count,
            automationFailures,
            crm.OpenDeals.Count,
            crm.OpenDeals.Sum(deal => deal.ValueUsd),
            crm.DueFollowUps.Count,
            affiliate.TotalClicks,
            affiliate.TotalCommissionAmount,
            dockerUnreadAlerts,
            timeProvider.GetUtcNow());
    }
}
