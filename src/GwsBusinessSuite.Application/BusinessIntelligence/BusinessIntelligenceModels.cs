namespace GwsBusinessSuite.Application.BusinessIntelligence;

public static class BiQueryShapes
{
    public const string Deals = "Deals";
    public const string ArticlePerformance = "ArticlePerformance";
    public const string AffiliateRevenue = "AffiliateRevenue";
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
}

public static class BiDimensions
{
    public const string Stage = "Stage";
    public const string Month = "Month";
    public const string Article = "Article";
    public const string Advertiser = "Advertiser";
}

public static class BiVisualizations
{
    public const string Bar = "Bar";
    public const string Line = "Line";
    public const string Table = "Table";

    public static readonly string[] All = [Bar, Line, Table];
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
}

public sealed record BiDataPoint(string Label, decimal Value);

public sealed record BiChartResult(
    string MetricLabel,
    string DimensionLabel,
    string ValueFormat,
    decimal Total,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<BiDataPoint> Points);

public sealed record BiDashboardWidget(
    Guid Id,
    string Title,
    string QueryShape,
    string Metric,
    string Dimension,
    string Visualization,
    int RangeDays,
    int SortOrder,
    BiChartResult Chart);

public interface IBusinessIntelligenceService
{
    IReadOnlyList<BiQueryShapeDefinition> GetQueryShapes();
    IReadOnlyList<int> GetRangeOptions();
    Task<IReadOnlyList<BiDashboardWidget>> GetDashboardAsync(string ownerUsername, CancellationToken cancellationToken = default);
    Task<BiChartResult> PreviewAsync(BiWidgetEditor editor, CancellationToken cancellationToken = default);
    Task<Guid> SaveWidgetAsync(string ownerUsername, BiWidgetEditor editor, CancellationToken cancellationToken = default);
    Task DeleteWidgetAsync(string ownerUsername, Guid widgetId, CancellationToken cancellationToken = default);
}
