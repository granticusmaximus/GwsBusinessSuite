namespace GwsBusinessSuite.Application.Growth;

public sealed record WebAnalyticsEventInput(
    string EventName,
    string VisitorKey,
    string SessionKey,
    string Path,
    string? PageTitle,
    string? Referrer,
    string? Source,
    string? Medium,
    string? Campaign,
    int EngagementSeconds);

public sealed record AnalyticsMetric(string Label, int Value, decimal ChangePercent = 0);
public sealed record AnalyticsPoint(DateOnly Date, int Visitors, int PageViews);
public sealed record AnalyticsBreakdownRow(string Label, int Visitors, int Views, decimal Share);
public sealed record AnalyticsPeriodComparison(decimal PreviousValue, decimal? ChangePercent);
public sealed record AnalyticsAnnotationInput(Guid? Id, DateOnly Date, string Note);
public sealed record AnalyticsAnnotationView(Guid Id, DateOnly Date, string Note);
public sealed record AnalyticsReportScheduleInput(
    Guid? Id,
    string Name,
    string RecipientEmail,
    string Frequency,
    int RangeDays,
    int DeliveryDay,
    int DeliveryHourUtc,
    bool IsActive);
public sealed record AnalyticsReportScheduleView(
    Guid Id,
    string Name,
    string RecipientEmail,
    string Frequency,
    int RangeDays,
    int DeliveryDay,
    int DeliveryHourUtc,
    bool IsActive,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastDeliveredAt,
    string LastStatus,
    string LastError);
public sealed record GrowthReportEmail(
    string RecipientEmail,
    string Subject,
    string PlainTextBody,
    string HtmlBody);
public sealed record GrowthReportDeliveryConfiguration(bool IsConfigured, string Message);
public sealed record AnalyticsGoalInput(
    Guid? Id,
    string Name,
    string MatchType,
    string MatchValue,
    bool IsActive);

public sealed record AnalyticsGoalView(
    Guid Id,
    string Name,
    string MatchType,
    string MatchValue,
    bool IsActive,
    int Conversions,
    int ConvertingVisitors,
    decimal ConversionRate,
    DateTimeOffset? LastConvertedAt,
    string TopSource);

public sealed record AnalyticsFunnelStepInput(
    string Name,
    string MatchType,
    string MatchValue);

public sealed record AnalyticsFunnelInput(
    Guid? Id,
    string Name,
    bool IsActive,
    IReadOnlyList<AnalyticsFunnelStepInput> Steps);

public sealed record AnalyticsFunnelStepView(
    Guid Id,
    string Name,
    string MatchType,
    string MatchValue,
    int SortOrder,
    int ReachedSessions,
    int DropOffSessions,
    decimal StepConversionRate,
    decimal DropOffRate);

public sealed record AnalyticsFunnelView(
    Guid Id,
    string Name,
    bool IsActive,
    int StartedSessions,
    int CompletedSessions,
    decimal CompletionRate,
    IReadOnlyList<AnalyticsFunnelStepView> Steps);

public sealed record AnalyticsSegmentRuleInput(
    string Dimension,
    string Operator,
    string Value);

public sealed record AnalyticsSegmentInput(
    Guid? Id,
    string Name,
    IReadOnlyList<AnalyticsSegmentRuleInput> Rules);

public sealed record AnalyticsSegmentRuleView(
    Guid Id,
    string Dimension,
    string Operator,
    string Value,
    int SortOrder);

public sealed record AnalyticsSegmentView(
    Guid Id,
    string Name,
    IReadOnlyList<AnalyticsSegmentRuleView> Rules);

public sealed record AnalyticsRetentionCell(
    int PeriodIndex,
    int? Visitors,
    decimal? Rate);

public sealed record AnalyticsRetentionCohort(
    DateOnly CohortStart,
    int Visitors,
    IReadOnlyList<AnalyticsRetentionCell> Periods);

public sealed class GrowthAnalyticsDashboard
{
    public int Visitors { get; init; }
    public int PageViews { get; init; }
    public int Sessions { get; init; }
    public decimal BounceRate { get; init; }
    public decimal ViewsPerSession { get; init; }
    public TimeSpan AverageEngagement { get; init; }
    public int VisitorsNow { get; init; }
    public int TotalConversions { get; init; }
    public decimal OverallConversionRate { get; init; }
    public int NewVisitors { get; init; }
    public int ReturningVisitors { get; init; }
    public decimal ReturningVisitorRate { get; init; }
    public AnalyticsPeriodComparison VisitorsComparison { get; init; } = new(0, 0);
    public AnalyticsPeriodComparison PageViewsComparison { get; init; } = new(0, 0);
    public AnalyticsPeriodComparison BounceRateComparison { get; init; } = new(0, 0);
    public AnalyticsPeriodComparison AverageEngagementComparison { get; init; } = new(0, 0);
    public bool GeoLocationConfigured { get; init; }
    public string RetentionPeriodLabel { get; init; } = "Day";
    public IReadOnlyList<AnalyticsRetentionCohort> RetentionCohorts { get; init; } = [];
    public IReadOnlyList<AnalyticsPoint> Trend { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> TopPages { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> TopSources { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Campaigns { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Devices { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Browsers { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Countries { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Regions { get; init; } = [];
    public IReadOnlyList<AnalyticsAnnotationView> Annotations { get; init; } = [];
    public IReadOnlyList<AnalyticsGoalView> Goals { get; init; } = [];
    public IReadOnlyList<AnalyticsFunnelView> Funnels { get; init; } = [];
}

public sealed record SocialAccountView(
    Guid Id,
    string Network,
    string DisplayName,
    string ExternalAccountId,
    bool IsEnabled,
    bool HasCredential,
    DateTimeOffset? LastPublishedAt);

public sealed record SocialAccountInput(
    Guid? Id,
    string Network,
    string DisplayName,
    string ExternalAccountId,
    string AccessToken,
    bool IsEnabled);

public sealed record SocialTargetDraft(Guid SocialAccountId, string Content);

public sealed record SocialPostView(
    Guid Id,
    string Title,
    string SourceUrl,
    string Status,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<SocialPostTargetView> Targets);

public sealed record SocialPostTargetView(
    Guid Id,
    Guid SocialAccountId,
    string Network,
    string AccountName,
    string Content,
    string Status,
    string ExternalPostId,
    string ErrorMessage);

public sealed record SocialPublishResult(bool IsSuccess, string Message);
