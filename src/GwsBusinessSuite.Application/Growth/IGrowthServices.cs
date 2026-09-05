namespace GwsBusinessSuite.Application.Growth;

public sealed record AnalyticsGeoLocation(
    string CountryCode,
    string CountryName,
    string RegionCode,
    string RegionName);

// Who to credit for the loaded location database, and where to link. Several of the free
// databases this app can read are licensed on condition that the credit is actually shown -
// DB-IP's Lite editions are CC-BY 4.0, which requires a link back to db-ip.com on any page that
// displays results derived from them.
public sealed record AnalyticsGeoAttribution(string Text, string Url);

public interface IAnalyticsGeoLocationResolver
{
    bool IsConfigured { get; }

    // Derived from the loaded file's own metadata rather than configured, so it can never credit
    // a vendor whose data isn't actually in use - swapping DB-IP for MaxMind's GeoLite2 changes
    // this on its own. Null when no database is loaded, or when the database is one with no
    // attribution requirement to honour.
    AnalyticsGeoAttribution? Attribution { get; }

    AnalyticsGeoLocation? Resolve(System.Net.IPAddress? address);
}

public interface IGrowthAnalyticsService
{
    Task RecordAsync(
        WebAnalyticsEventInput input,
        string? userAgent,
        System.Net.IPAddress? remoteAddress = null,
        CancellationToken cancellationToken = default);

    Task<GrowthAnalyticsDashboard> GetDashboardAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? segmentId = null,
        int breakdownLimit = 12,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnalyticsGoalView>> GetGoalsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    Task SaveGoalAsync(AnalyticsGoalInput input, CancellationToken cancellationToken = default);
    Task DeleteGoalAsync(Guid goalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnalyticsFunnelView>> GetFunnelsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
    Task SaveFunnelAsync(AnalyticsFunnelInput input, CancellationToken cancellationToken = default);
    Task DeleteFunnelAsync(Guid funnelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnalyticsSegmentView>> GetSegmentsAsync(CancellationToken cancellationToken = default);
    Task<Guid> SaveSegmentAsync(AnalyticsSegmentInput input, CancellationToken cancellationToken = default);
    Task DeleteSegmentAsync(Guid segmentId, CancellationToken cancellationToken = default);
    Task<Guid> SaveAnnotationAsync(AnalyticsAnnotationInput input, CancellationToken cancellationToken = default);
    Task DeleteAnnotationAsync(Guid annotationId, CancellationToken cancellationToken = default);
}

public interface ISocialPublishingService
{
    Task<IReadOnlyList<SocialAccountView>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task SaveAccountAsync(SocialAccountInput input, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SocialPostView>> GetPostsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, string>> GenerateVariantsAsync(
        string topic,
        string sourceUrl,
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken = default);
    Task<Guid> SaveDraftAsync(
        string title,
        string sourceUrl,
        IReadOnlyCollection<SocialTargetDraft> targets,
        DateTimeOffset? scheduledFor,
        CancellationToken cancellationToken = default);
    Task<SocialPublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default);
    Task PublishDueAsync(CancellationToken cancellationToken = default);

    // Mirrors IDockerHealthService's alert surface exactly so NotificationBell can merge both
    // sources through the same shape - see SocialPostAlert for why this is a separate table
    // rather than reusing DockerHealthAlert.
    Task<IReadOnlyList<SocialPostAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default);
    Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default);
    Task MarkAllAlertsReadAsync(CancellationToken cancellationToken = default);
    Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default);
}

public interface IGrowthReportEmailSender
{
    GrowthReportDeliveryConfiguration Configuration { get; }
    Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default);
}

public interface IGrowthReportService
{
    GrowthReportDeliveryConfiguration DeliveryConfiguration { get; }
    Task<IReadOnlyList<AnalyticsReportScheduleView>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task<Guid> SaveScheduleAsync(AnalyticsReportScheduleInput input, CancellationToken cancellationToken = default);
    Task DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default);
    Task SendNowAsync(Guid scheduleId, CancellationToken cancellationToken = default);
    Task<int> DeliverDueAsync(CancellationToken cancellationToken = default);
}
