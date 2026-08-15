using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.BusinessIntelligence;

public sealed class BusinessIntelligenceService(IAppDbContext db, TimeProvider timeProvider) : IBusinessIntelligenceService
{
    private const int MaxSourceRows = 50_000;
    private static readonly int[] RangeOptions = [7, 30, 90, 365];

    private static readonly BiQueryShapeDefinition[] QueryShapes =
    [
        new(BiQueryShapes.Deals, "CRM deals", "Pipeline count or value grouped by stage or month.",
            [new(BiMetrics.Count, "Deal count"), new(BiMetrics.PipelineValue, "Pipeline value")],
            [new(BiDimensions.Stage, "Stage"), new(BiDimensions.Month, "Month created")]),
        new(BiQueryShapes.ArticlePerformance, "Article performance", "Published article traffic from first-party analytics.",
            [new(BiMetrics.PageViews, "Page views"), new(BiMetrics.Visitors, "Unique visitors")],
            [new(BiDimensions.Article, "Article")]),
        new(BiQueryShapes.AffiliateRevenue, "Affiliate revenue", "CJ sales, commission, or actions by advertiser.",
            [new(BiMetrics.Commission, "Commission"), new(BiMetrics.Sales, "Sales"), new(BiMetrics.Actions, "Actions")],
            [new(BiDimensions.Advertiser, "Advertiser")])
    ];

    public IReadOnlyList<BiQueryShapeDefinition> GetQueryShapes() => QueryShapes;
    public IReadOnlyList<int> GetRangeOptions() => RangeOptions;

    public async Task<IReadOnlyList<BiDashboardWidget>> GetDashboardAsync(
        string ownerUsername,
        CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var widgets = await db.BusinessIntelligenceWidgets
            .AsNoTracking()
            .Where(item => item.OwnerUsername == owner)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        // Loaded once here (at the widest RangeDays any Deals-shaped widget on this dashboard
        // needs) and shared across every such widget instead of re-queried once per widget - a
        // dashboard with several Deals widgets used to re-scan the whole table that many times
        // on a single page load. Each widget's own (possibly narrower) range is then applied
        // in-memory against this shared, already-bounded set (see QueryDeals).
        var dealWidgetRangeDays = widgets.Where(item => item.QueryShape == BiQueryShapes.Deals)
            .Select(item => item.RangeDays).ToList();
        var dealsCache = dealWidgetRangeDays.Count > 0
            ? await LoadDealProjectionsAsync(timeProvider.GetUtcNow().AddDays(-dealWidgetRangeDays.Max()), cancellationToken)
            : null;

        var results = new List<BiDashboardWidget>(widgets.Count);
        foreach (var widget in widgets)
        {
            var editor = new BiWidgetEditor
            {
                Id = widget.Id,
                Title = widget.Title,
                QueryShape = widget.QueryShape,
                Metric = widget.Metric,
                Dimension = widget.Dimension,
                Visualization = widget.Visualization,
                RangeDays = widget.RangeDays
            };
            var chart = await PreviewCoreAsync(editor, dealsCache, cancellationToken);
            results.Add(new BiDashboardWidget(widget.Id, widget.Title, widget.QueryShape, widget.Metric,
                widget.Dimension, widget.Visualization, widget.RangeDays, widget.SortOrder, chart));
        }

        return results;
    }

    public Task<BiChartResult> PreviewAsync(BiWidgetEditor editor, CancellationToken cancellationToken = default) =>
        PreviewCoreAsync(editor, preloadedDeals: null, cancellationToken);

    private async Task<BiChartResult> PreviewCoreAsync(
        BiWidgetEditor editor, IReadOnlyList<DealProjection>? preloadedDeals, CancellationToken cancellationToken)
    {
        var definition = Validate(editor);
        var now = timeProvider.GetUtcNow();
        var from = now.AddDays(-editor.RangeDays);

        var points = editor.QueryShape switch
        {
            BiQueryShapes.Deals => QueryDeals(preloadedDeals ?? await LoadDealProjectionsAsync(from, cancellationToken), editor, from),
            BiQueryShapes.ArticlePerformance => await QueryArticlesAsync(editor, from, cancellationToken),
            BiQueryShapes.AffiliateRevenue => await QueryAffiliateAsync(editor, from, cancellationToken),
            _ => throw new InvalidOperationException("That report source is not supported.")
        };

        var metricLabel = definition.Metrics.Single(option => option.Value == editor.Metric).Label;
        var dimensionLabel = definition.Dimensions.Single(option => option.Value == editor.Dimension).Label;
        var valueFormat = editor.Metric is BiMetrics.PipelineValue or BiMetrics.Commission or BiMetrics.Sales
            ? "Currency"
            : "Number";
        return new BiChartResult(metricLabel, dimensionLabel, valueFormat, points.Sum(point => point.Value), from, now, points);
    }

    public async Task<Guid> SaveWidgetAsync(
        string ownerUsername,
        BiWidgetEditor editor,
        CancellationToken cancellationToken = default)
    {
        Validate(editor);
        var owner = NormalizeOwner(ownerUsername);
        var title = editor.Title.Trim();
        if (title.Length is < 1 or > 120)
        {
            throw new InvalidOperationException("Widget title must be between 1 and 120 characters.");
        }

        BusinessIntelligenceWidget widget;
        if (editor.Id is { } id)
        {
            widget = await db.BusinessIntelligenceWidgets
                .FirstOrDefaultAsync(item => item.Id == id && item.OwnerUsername == owner, cancellationToken)
                ?? throw new InvalidOperationException("Dashboard widget was not found.");
            widget.UpdatedAt = timeProvider.GetUtcNow();
            widget.UpdatedBy = owner;
        }
        else
        {
            var nextOrder = await db.BusinessIntelligenceWidgets
                .Where(item => item.OwnerUsername == owner)
                .Select(item => (int?)item.SortOrder)
                .MaxAsync(cancellationToken) ?? -1;
            widget = new BusinessIntelligenceWidget
            {
                OwnerUsername = owner,
                Title = title,
                QueryShape = editor.QueryShape,
                Metric = editor.Metric,
                Dimension = editor.Dimension,
                Visualization = editor.Visualization,
                RangeDays = editor.RangeDays,
                SortOrder = nextOrder + 1,
                CreatedAt = timeProvider.GetUtcNow(),
                CreatedBy = owner
            };
            await db.BusinessIntelligenceWidgets.AddAsync(widget, cancellationToken);
        }

        widget.Title = title;
        widget.QueryShape = editor.QueryShape;
        widget.Metric = editor.Metric;
        widget.Dimension = editor.Dimension;
        widget.Visualization = editor.Visualization;
        widget.RangeDays = editor.RangeDays;
        await db.SaveChangesAsync(cancellationToken);
        return widget.Id;
    }

    public async Task DeleteWidgetAsync(string ownerUsername, Guid widgetId, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var widget = await db.BusinessIntelligenceWidgets
            .FirstOrDefaultAsync(item => item.Id == widgetId && item.OwnerUsername == owner, cancellationToken)
            ?? throw new InvalidOperationException("Dashboard widget was not found.");
        db.BusinessIntelligenceWidgets.Remove(widget);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Filters/orders/caps in SQL against CreatedAtUnixSeconds (a shadow column - SQLite can't
    // translate a range comparison or ORDER BY against CreatedAt itself, a DateTimeOffset
    // column). Loaded once per PreviewAsync/GetDashboardAsync call and shared across every
    // Deals-shaped widget in a dashboard load rather than re-queried per widget (see
    // GetDashboardAsync) - the shared call passes the widest cutoff any widget on the dashboard
    // needs, and QueryDeals re-applies each individual widget's own (possibly narrower) cutoff
    // in memory against this already-bounded set.
    private async Task<List<DealProjection>> LoadDealProjectionsAsync(DateTimeOffset from, CancellationToken cancellationToken)
    {
        var cutoff = from.ToUnixTimeSeconds();
        return await db.Deals.AsNoTracking()
            .Where(item => item.CreatedAtUnixSeconds >= cutoff)
            .OrderByDescending(item => item.CreatedAtUnixSeconds)
            .Take(MaxSourceRows)
            .Select(item => new DealProjection(item.Stage, item.ValueUsd, item.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<BiDataPoint> QueryDeals(
        IReadOnlyList<DealProjection> allDeals, BiWidgetEditor editor, DateTimeOffset from)
    {
        var deals = allDeals.Where(item => item.CreatedAt >= from).ToList();

        if (editor.Dimension == BiDimensions.Stage)
        {
            return DealStages.All
                .Select(stage => new BiDataPoint(stage, deals.Where(item => item.Stage == stage)
                    .Sum(item => editor.Metric == BiMetrics.Count ? 1m : item.ValueUsd)))
                .Where(point => point.Value > 0)
                .ToList();
        }

        return deals
            .GroupBy(item => new DateTime(item.CreatedAt.Year, item.CreatedAt.Month, 1))
            .OrderBy(group => group.Key)
            .Select(group => new BiDataPoint(group.Key.ToString("MMM yyyy"),
                group.Sum(item => editor.Metric == BiMetrics.Count ? 1m : item.ValueUsd)))
            .ToList();
    }

    private sealed record DealProjection(string Stage, decimal ValueUsd, DateTimeOffset CreatedAt);

    private async Task<IReadOnlyList<BiDataPoint>> QueryArticlesAsync(
        BiWidgetEditor editor,
        DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        var articlePaths = await db.Articles.AsNoTracking()
            .Where(item => item.TrashedAt == null && item.Status == ArticleStatuses.Published)
            .Select(item => new { Path = "/blog/" + item.Slug, item.Title })
            .ToDictionaryAsync(item => item.Path, item => item.Title, cancellationToken);
        var cutoff = from.ToUnixTimeSeconds();
        var events = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.OccurredAtUnixSeconds >= cutoff && item.EventName == WebAnalyticsEventNames.PageView)
            .OrderByDescending(item => item.OccurredAtUnixSeconds)
            .Take(MaxSourceRows)
            .Select(item => new { item.Path, item.VisitorKey })
            .ToListAsync(cancellationToken);

        return events
            .Where(item => articlePaths.ContainsKey(item.Path))
            .GroupBy(item => item.Path)
            .Select(group => new BiDataPoint(
                articlePaths[group.Key],
                editor.Metric == BiMetrics.PageViews ? group.Count() : group.Select(item => item.VisitorKey).Distinct().Count()))
            .OrderByDescending(point => point.Value)
            .ThenBy(point => point.Label)
            .Take(12)
            .ToList();
    }

    private async Task<IReadOnlyList<BiDataPoint>> QueryAffiliateAsync(
        BiWidgetEditor editor,
        DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        var cutoff = from.ToUnixTimeSeconds();
        var rows = await db.CjCommissionRecords.AsNoTracking()
            .Where(item => item.CreatedAtUnixSeconds >= cutoff)
            .OrderByDescending(item => item.CreatedAtUnixSeconds)
            .Take(MaxSourceRows)
            .Select(item => new { item.AdvertiserName, item.SaleAmount, item.CommissionAmount })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => string.IsNullOrWhiteSpace(item.AdvertiserName) ? "Unknown advertiser" : item.AdvertiserName)
            .Select(group => new BiDataPoint(group.Key, editor.Metric switch
            {
                BiMetrics.Commission => group.Sum(item => item.CommissionAmount),
                BiMetrics.Sales => group.Sum(item => item.SaleAmount),
                _ => group.Count()
            }))
            .OrderByDescending(point => point.Value)
            .ThenBy(point => point.Label)
            .Take(12)
            .ToList();
    }

    private static BiQueryShapeDefinition Validate(BiWidgetEditor editor)
    {
        var definition = QueryShapes.FirstOrDefault(item => item.Value == editor.QueryShape)
            ?? throw new InvalidOperationException("That report source is not supported.");
        if (!definition.Metrics.Any(item => item.Value == editor.Metric))
        {
            throw new InvalidOperationException("That metric is not available for the selected source.");
        }
        if (!definition.Dimensions.Any(item => item.Value == editor.Dimension))
        {
            throw new InvalidOperationException("That dimension is not available for the selected source.");
        }
        if (!BiVisualizations.All.Contains(editor.Visualization))
        {
            throw new InvalidOperationException("That visualization is not supported.");
        }
        if (!RangeOptions.Contains(editor.RangeDays))
        {
            throw new InvalidOperationException("That date range is not supported.");
        }
        return definition;
    }

    private static string NormalizeOwner(string ownerUsername)
    {
        var owner = ownerUsername.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(owner)
            ? throw new InvalidOperationException("An authenticated user is required.")
            : owner;
    }
}
