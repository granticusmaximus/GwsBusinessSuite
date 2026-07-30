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
    public IReadOnlyList<AnalyticsPoint> Trend { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> TopPages { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> TopSources { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Campaigns { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Devices { get; init; } = [];
    public IReadOnlyList<AnalyticsBreakdownRow> Browsers { get; init; } = [];
    public IReadOnlyList<AnalyticsGoalView> Goals { get; init; } = [];
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
