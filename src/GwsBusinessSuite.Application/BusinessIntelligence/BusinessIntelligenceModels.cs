namespace GwsBusinessSuite.Application.BusinessIntelligence;

public static class BiQueryShapes
{
    public const string Deals = "Deals";
    public const string ArticlePerformance = "ArticlePerformance";
    public const string AffiliateRevenue = "AffiliateRevenue";
    public const string SupportTickets = "SupportTickets";
    public const string FormSubmissions = "FormSubmissions";
    public const string AutomationRuns = "AutomationRuns";
}

public static class BiMetrics
{
    public const string Count = "Count";
    public const string PipelineValue = "PipelineValue";
    public const string PageViews = "PageViews";
    public const string Visitors = "Visitors";
    public const string Commission = "Commission";
    public const string Sales = "Sales";
    public const string Actions = "Actions";
    public const string AverageSatisfaction = "AverageSatisfaction";
    public const string SlaBreaches = "SlaBreaches";
}

public static class BiDimensions
{
    public const string Stage = "Stage";
    public const string Month = "Month";
    public const string Article = "Article";
    public const string Advertiser = "Advertiser";
    public const string Status = "Status";
    public const string Priority = "Priority";
    public const string Form = "Form";
    public const string Workflow = "Workflow";
}

public static class BiVisualizations
{
    public const string Bar = "Bar";
    public const string Line = "Line";
    public const string Table = "Table";
    // Just the headline total, change, and goal - a compact KPI tile.
    public const string Kpi = "Kpi";

    public static readonly string[] All = [Bar, Line, Table, Kpi];
}

public sealed record BiOption(string Value, string Label);

public sealed record BiQueryShapeDefinition(
    string Value,
    string Label,
    string Description,
    IReadOnlyList<BiOption> Metrics,
    IReadOnlyList<BiOption> Dimensions);

public sealed class BiWidgetEditor
{
    public Guid? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string QueryShape { get; set; } = BiQueryShapes.Deals;
    public string Metric { get; set; } = BiMetrics.Count;
    public string Dimension { get; set; } = BiDimensions.Stage;
    public string Visualization { get; set; } = BiVisualizations.Bar;
    public int RangeDays { get; set; } = 30;
    public bool IsWide { get; set; }
    public decimal? GoalValue { get; set; }
    public bool GoalIsCeiling { get; set; }
}

// A widget whose total moved across its goal since the last check (see EvaluateGoalsAsync).
public sealed record BiGoalCrossing(Guid WidgetId, string OwnerUsername, string Title, string MetricLabel,
    decimal Total, decimal GoalValue, bool GoalIsCeiling, bool GoalMet, string ValueFormat);

public sealed record BiDataPoint(string Label, decimal Value);

// One record behind a chart point (drill-down): a deal, a commission record, or one day of an
// article's traffic. Value is in the chart's own metric.
public sealed record BiDrillRow(string Title, string Detail, decimal Value, DateTimeOffset? When);

// A dashboard-wide custom window (To exclusive) that overrides every widget's own RangeDays
// while it's applied - it's a viewing choice, never saved onto the widgets.
public sealed record BiDateRange(DateTimeOffset From, DateTimeOffset To);

public sealed record BiChartResult(
    string MetricLabel,
    string DimensionLabel,
    string ValueFormat,
    decimal Total,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<BiDataPoint> Points,
    // Same metric over the equally long window just before From, for period-over-period change.
    decimal PreviousTotal = 0m,
    // When these numbers were actually computed - dashboard results are cached for a few minutes.
    DateTimeOffset DataAsOf = default);

public sealed record BiDashboardWidget(
    Guid Id,
    string Title,
    string QueryShape,
    string Metric,
    string Dimension,
    string Visualization,
    int RangeDays,
    int SortOrder,
    BiChartResult Chart,
    bool IsWide = false,
    decimal? GoalValue = null,
    bool GoalIsCeiling = false)
{
    // A floor goal is met at or above it; a ceiling goal (e.g. SLA breaches) at or below it.
    public bool? GoalMet => GoalValue is not { } goal ? null : GoalIsCeiling ? Chart.Total <= goal : Chart.Total >= goal;
}

public sealed record BiReportSubscriptionView(bool Enabled, string? Recipient, int DayOfWeek, int HourLocal,
    DateTimeOffset? LastSentAt, bool EmailConfigured);

public sealed record BiReportSendResult(bool Sent, string Message);

// Weekly email of an admin's own dashboard (totals, change vs previous period, goal status).
public interface IBiReportEmailService
{
    Task<BiReportSubscriptionView> GetSubscriptionAsync(string ownerUsername, CancellationToken cancellationToken = default);
    Task SaveSubscriptionAsync(string ownerUsername, BiReportSubscriptionView subscription, CancellationToken cancellationToken = default);
    Task<BiReportSendResult> SendAsync(string ownerUsername, bool force, CancellationToken cancellationToken = default);
    Task<int> SendDueReportsAsync(CancellationToken cancellationToken = default);
}

public interface IBusinessIntelligenceService
{
    IReadOnlyList<BiQueryShapeDefinition> GetQueryShapes();
    IReadOnlyList<int> GetRangeOptions();
    Task<IReadOnlyList<BiDashboardWidget>> GetDashboardAsync(string ownerUsername, BiDateRange? range = null, bool refresh = false, CancellationToken cancellationToken = default);
    Task<BiChartResult> PreviewAsync(BiWidgetEditor editor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BiDrillRow>> DrillDownAsync(BiWidgetEditor editor, string pointLabel, BiDateRange? range = null, CancellationToken cancellationToken = default);
    Task<Guid> SaveWidgetAsync(string ownerUsername, BiWidgetEditor editor, CancellationToken cancellationToken = default);
    Task DeleteWidgetAsync(string ownerUsername, Guid widgetId, CancellationToken cancellationToken = default);
    Task MoveWidgetAsync(string ownerUsername, Guid widgetId, int direction, CancellationToken cancellationToken = default);
    Task SetWidgetWideAsync(string ownerUsername, Guid widgetId, bool isWide, CancellationToken cancellationToken = default);
    // Recomputes every widget that has a goal (all owners), records whether it's met, and returns
    // the ones whose state flipped since the previous check. A first check only records state.
    Task<IReadOnlyList<BiGoalCrossing>> EvaluateGoalsAsync(CancellationToken cancellationToken = default);
}
