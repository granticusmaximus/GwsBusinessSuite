using GwsBusinessSuite.Application.Abstractions;
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
    TimeProvider timeProvider,
    TimeZoneInfo? zone = null) : IMissionControlService
{
    public const int TrendDays = 14;

    public async Task<MissionControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var automationFailures = await automationWorkflowService.ListRecentFailuresAsync(5, cancellationToken);
        var crm = await crmService.GetDashboardAsync(cancellationToken);
        var affiliate = await affiliateAnalyticsService.GetDashboardAsync(cancellationToken);
        var dockerUnreadAlerts = await dockerHealthService.CountUnreadAlertsAsync(cancellationToken);

        // One shared DbContext under these services, so the day counts run one after another.
        var range = DayRange.Last(TrendDays, timeProvider.GetUtcNow(), zone ?? TimeZoneInfo.Local);
        var failuresByDay = await automationWorkflowService.CountFailuresByDayAsync(range, cancellationToken);
        var clicksByDay = await affiliateAnalyticsService.CountClicksByDayAsync(range, cancellationToken);
        var alertsByDay = await dockerHealthService.CountAlertsByDayAsync(range, cancellationToken);
        var pipelineByDay = new decimal[range.Days];
        foreach (var deal in crm.OpenDeals)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(deal.CreatedAt, range.Zone).DateTime).DayNumber - range.FirstDay.DayNumber;
            if (day >= 0 && day < range.Days) pipelineByDay[day] += deal.ValueUsd;
        }

        return new MissionControlSnapshot(
            automationFailures.Count,
            automationFailures,
            crm.OpenDeals.Count,
            crm.OpenDeals.Sum(deal => deal.ValueUsd),
            crm.DueFollowUps.Count,
            affiliate.TotalClicks,
            affiliate.TotalCommissionAmount,
            dockerUnreadAlerts,
            timeProvider.GetUtcNow(),
            new MissionControlTrends(range.Days, failuresByDay, pipelineByDay, clicksByDay, alertsByDay));
    }
}
