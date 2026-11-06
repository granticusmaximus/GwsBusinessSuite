using GwsBusinessSuite.Application.Automation;

namespace GwsBusinessSuite.Application.MissionControl;

// A single-glance composite of already-existing per-module dashboards/summaries - deliberately
// not a new source of truth (nothing here is computed or persisted independently; every field
// is read straight from the module service that already owns it). "Mission Control" beats
// checking automation/CRM/affiliate/container-health pages separately for the daily "is
// anything on fire" check.
public sealed record MissionControlSnapshot(
    int AutomationRecentFailureCount,
    IReadOnlyList<AutomationRecentFailureView> AutomationRecentFailures,
    int CrmOpenDealCount,
    decimal CrmOpenDealValueUsd,
    int CrmDueFollowUpCount,
    int AffiliateTotalClicks,
    decimal AffiliateTotalCommissionAmount,
    int DockerUnreadAlertCount,
    DateTimeOffset GeneratedAt,
    MissionControlTrends? Trends = null);

// The tiles' sparklines (T5, 06-11-26): one value per day for the last Days days, oldest first,
// in the server's time zone.
public sealed record MissionControlTrends(
    int Days,
    IReadOnlyList<int> AutomationFailures,
    // The value of deals opened each day that are still open.
    IReadOnlyList<decimal> PipelineAddedUsd,
    IReadOnlyList<int> AffiliateClicks,
    IReadOnlyList<int> DockerAlerts);

public interface IMissionControlService
{
    Task<MissionControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
