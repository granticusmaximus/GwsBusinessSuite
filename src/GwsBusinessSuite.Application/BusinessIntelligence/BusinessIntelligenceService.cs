using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Application.BusinessIntelligence;

public sealed class BusinessIntelligenceService(IAppDbContext db, TimeProvider timeProvider, IMemoryCache cache) : IBusinessIntelligenceService
{
    private const int MaxSourceRows = 50_000;
    private const int MaxDisplayPoints = 50;
    private const int MaxDrillRows = 200;
    private const int MaxCustomRangeDays = 731;
    private static readonly TimeSpan ResultCacheDuration = TimeSpan.FromMinutes(5);
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
            [new(BiDimensions.Advertiser, "Advertiser")]),
        new(BiQueryShapes.SupportTickets, "Support tickets", "Ticket volume, SLA breaches, or average CSAT (1-5) by status, priority, or month opened.",
            [new(BiMetrics.Count, "Tickets"), new(BiMetrics.SlaBreaches, "SLA breaches"), new(BiMetrics.AverageSatisfaction, "Average CSAT")],
            [new(BiDimensions.Status, "Status"), new(BiDimensions.Priority, "Priority"), new(BiDimensions.Month, "Month opened")]),
        new(BiQueryShapes.FormSubmissions, "Form submissions", "Submissions to site forms by form page or month.",
            [new(BiMetrics.Count, "Submissions")],
            [new(BiDimensions.Form, "Form page"), new(BiDimensions.Month, "Month")]),
        new(BiQueryShapes.AutomationRuns, "Automation runs", "Workflow runs by status, workflow, or month started.",
            [new(BiMetrics.Count, "Runs")],
            [new(BiDimensions.Status, "Status"), new(BiDimensions.Workflow, "Workflow"), new(BiDimensions.Month, "Month started")])
    ];

    public IReadOnlyList<BiQueryShapeDefinition> GetQueryShapes() => QueryShapes;
    public IReadOnlyList<int> GetRangeOptions() => RangeOptions;

    public async Task<IReadOnlyList<BiDashboardWidget>> GetDashboardAsync(
        string ownerUsername,
        BiDateRange? range = null,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        if (range is not null) ValidateRange(range);
        var widgets = await db.BusinessIntelligenceWidgets
            .AsNoTracking()
            .Where(item => item.OwnerUsername == owner)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        // Chart results are cached per report definition + window (not per user - it's the same
        // suite data for everyone) so reopening the dashboard doesn't rescan the analytics/deals
        // tables every time; "Refresh" (refresh: true) recomputes. Preset ranges are keyed by
        // RangeDays, so a cached "last 30 days" slides forward when the entry expires.
        var charts = new Dictionary<Guid, BiChartResult>();
        var misses = new List<(BusinessIntelligenceWidget Widget, BiWidgetEditor Editor, string Key)>();
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
                RangeDays = widget.RangeDays,
                IsWide = widget.IsWide,
                GoalValue = widget.GoalValue,
                GoalIsCeiling = widget.GoalIsCeiling
            };
            var key = CacheKey(editor, range);
            if (!refresh && cache.TryGetValue(key, out BiChartResult? cached) && cached is not null)
            {
                charts[widget.Id] = cached;
            }
            else
            {
                misses.Add((widget, editor, key));
            }
        }

        // The deals table is read once (back to the earliest comparison-period start any missed
        // Deals widget needs) and shared across them, instead of re-scanned once per widget;
        // QueryDeals applies each widget's own window in memory.
        var dealWindows = misses.Where(miss => miss.Widget.QueryShape == BiQueryShapes.Deals)
            .Select(miss => ResolveWindow(miss.Widget.RangeDays, range)).ToList();
        var dealsCache = dealWindows.Count > 0
            ? await LoadDealProjectionsAsync(dealWindows.Min(window => window.PreviousFrom), cancellationToken)
            : null;
        foreach (var (widget, editor, key) in misses)
        {
            var chart = await PreviewCoreAsync(editor, range, dealsCache, cancellationToken);
            cache.Set(key, chart, ResultCacheDuration);
            charts[widget.Id] = chart;
        }

        var results = new List<BiDashboardWidget>(widgets.Count);
        foreach (var widget in widgets)
        {
            results.Add(new BiDashboardWidget(widget.Id, widget.Title, widget.QueryShape, widget.Metric,
                widget.Dimension, widget.Visualization, widget.RangeDays, widget.SortOrder, charts[widget.Id],
                widget.IsWide, widget.GoalValue, widget.GoalIsCeiling));
        }

        return results;
    }

    public Task<BiChartResult> PreviewAsync(BiWidgetEditor editor, CancellationToken cancellationToken = default) =>
        PreviewCoreAsync(editor, range: null, preloadedDeals: null, cancellationToken);

    private async Task<BiChartResult> PreviewCoreAsync(
        BiWidgetEditor editor, BiDateRange? range, IReadOnlyList<DealProjection>? preloadedDeals, CancellationToken cancellationToken)
    {
        var definition = Validate(editor);
        var window = ResolveWindow(editor.RangeDays, range);
        var deals = editor.QueryShape == BiQueryShapes.Deals
            ? preloadedDeals ?? await LoadDealProjectionsAsync(window.PreviousFrom, cancellationToken)
            : null;

        // Totals come from every group, before the display cap, so a long tail of small
        // articles/advertisers beyond the 50 shown still counts toward the total and its period-over-period change.
        var points = await QueryPointsAsync(editor, deals, window.From, window.To, cancellationToken);
        decimal total, previousTotal;
        if (editor.Metric == BiMetrics.AverageSatisfaction)
        {
            // An average's "total" is the overall average across every rated ticket, not a sum.
            total = AverageRating(await LoadTicketsAsync(window.From, window.To, cancellationToken));
            previousTotal = AverageRating(await LoadTicketsAsync(window.PreviousFrom, window.From, cancellationToken));
        }
        else
        {
            previousTotal = (await QueryPointsAsync(editor, deals, window.PreviousFrom, window.From, cancellationToken))
                .Sum(point => point.Value);
            total = points.Sum(point => point.Value);
        }
        if (editor.QueryShape != BiQueryShapes.Deals) points = points.Take(MaxDisplayPoints).ToList();

        var metricLabel = definition.Metrics.Single(option => option.Value == editor.Metric).Label;
        var dimensionLabel = definition.Dimensions.Single(option => option.Value == editor.Dimension).Label;
        var valueFormat = editor.Metric switch
        {
            BiMetrics.PipelineValue or BiMetrics.Commission or BiMetrics.Sales => "Currency",
            BiMetrics.AverageSatisfaction => "Rating",
            _ => "Number"
        };
        return new BiChartResult(metricLabel, dimensionLabel, valueFormat, total, window.From, window.To, points, previousTotal,
            timeProvider.GetUtcNow());
    }

    // The records behind one chart point, newest first (capped at MaxDrillRows). Matches points the
    // same way the chart groups them, so the rows always add up to the bar that was clicked.
    public async Task<IReadOnlyList<BiDrillRow>> DrillDownAsync(
        BiWidgetEditor editor, string pointLabel, BiDateRange? range = null, CancellationToken cancellationToken = default)
    {
        Validate(editor);
        if (range is not null) ValidateRange(range);
        var window = ResolveWindow(editor.RangeDays, range);
        var cutoff = window.From.ToUnixTimeSeconds();
        var end = window.To.ToUnixTimeSeconds();

        switch (editor.QueryShape)
        {
            case BiQueryShapes.Deals:
            {
                var deals = await db.Deals.AsNoTracking()
                    .Where(item => item.CreatedAtUnixSeconds >= cutoff && item.CreatedAtUnixSeconds < end)
                    .OrderByDescending(item => item.CreatedAtUnixSeconds)
                    .Take(MaxSourceRows)
                    .Select(item => new { item.Title, item.Stage, item.ValueUsd, item.CreatedAt })
                    .ToListAsync(cancellationToken);
                return deals
                    .Where(item => editor.Dimension == BiDimensions.Stage
                        ? item.Stage == pointLabel
                        : new DateTime(item.CreatedAt.Year, item.CreatedAt.Month, 1).ToString("MMM yyyy") == pointLabel)
                    .Take(MaxDrillRows)
                    .Select(item => new BiDrillRow(item.Title, item.Stage,
                        editor.Metric == BiMetrics.Count ? 1m : item.ValueUsd, item.CreatedAt))
                    .ToList();
            }
            case BiQueryShapes.AffiliateRevenue:
            {
                var rows = await db.CjCommissionRecords.AsNoTracking()
                    .Where(item => item.CreatedAtUnixSeconds >= cutoff && item.CreatedAtUnixSeconds < end)
                    .OrderByDescending(item => item.CreatedAtUnixSeconds)
                    .Take(MaxSourceRows)
                    .Select(item => new { item.AdvertiserName, item.OrderId, item.ActionStatus, item.SaleAmount, item.CommissionAmount, item.EventDate, item.CreatedAt })
                    .ToListAsync(cancellationToken);
                return rows
                    .Where(item => (string.IsNullOrWhiteSpace(item.AdvertiserName) ? "Unknown advertiser" : item.AdvertiserName) == pointLabel)
                    .Take(MaxDrillRows)
                    .Select(item => new BiDrillRow(
                        string.IsNullOrWhiteSpace(item.OrderId) ? "Order" : $"Order {item.OrderId}",
                        item.ActionStatus,
                        editor.Metric switch
                        {
                            BiMetrics.Commission => item.CommissionAmount,
                            BiMetrics.Sales => item.SaleAmount,
                            _ => 1m
                        },
                        item.EventDate ?? item.CreatedAt))
                    .ToList();
            }
            case BiQueryShapes.ArticlePerformance:
            {
                var slugs = await db.Articles.AsNoTracking()
                    .Where(item => item.TrashedAt == null && item.Status == ArticleStatuses.Published && item.Title == pointLabel)
                    .Select(item => "/blog/" + item.Slug)
                    .ToListAsync(cancellationToken);
                var events = await db.WebAnalyticsEvents.AsNoTracking()
                    .Where(item => item.OccurredAtUnixSeconds >= cutoff && item.OccurredAtUnixSeconds < end
                        && item.EventName == WebAnalyticsEventNames.PageView && slugs.Contains(item.Path))
                    .OrderByDescending(item => item.OccurredAtUnixSeconds)
                    .Take(MaxSourceRows)
                    .Select(item => new { item.OccurredAtUnixSeconds, item.VisitorKey })
                    .ToListAsync(cancellationToken);
                return events
                    .GroupBy(item => DateTimeOffset.FromUnixTimeSeconds(item.OccurredAtUnixSeconds).UtcDateTime.Date)
                    .OrderByDescending(group => group.Key)
                    .Take(MaxDrillRows)
                    .Select(group => new BiDrillRow(group.Key.ToString("ddd, MMM d"), "Daily traffic",
                        editor.Metric == BiMetrics.PageViews ? group.Count() : group.Select(item => item.VisitorKey).Distinct().Count(),
                        new DateTimeOffset(group.Key, TimeSpan.Zero)))
                    .ToList();
            }
            case BiQueryShapes.SupportTickets:
                return (await LoadTicketsAsync(window.From, window.To, cancellationToken))
                    .Where(item => TicketGroup(item, editor.Dimension) == pointLabel)
                    .Where(item => editor.Metric switch
                    {
                        BiMetrics.SlaBreaches => item.Breached,
                        BiMetrics.AverageSatisfaction => item.SatisfactionRating is not null,
                        _ => true
                    })
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(MaxDrillRows)
                    .Select(item => new BiDrillRow(item.Subject, $"{item.Status} · {item.Priority}",
                        editor.Metric == BiMetrics.AverageSatisfaction ? item.SatisfactionRating!.Value : 1m, item.CreatedAt))
                    .ToList();
            case BiQueryShapes.FormSubmissions:
            {
                var titles = await FormPageTitlesAsync(cancellationToken);
                return (await LoadFormSubmissionsAsync(window.From, window.To, cancellationToken))
                    .Where(item => (editor.Dimension == BiDimensions.Form
                        ? titles.GetValueOrDefault(item.PageId, "Deleted page")
                        : MonthLabel(item.CreatedAt)) == pointLabel)
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(MaxDrillRows)
                    .Select(item => new BiDrillRow(
                        string.IsNullOrWhiteSpace(item.Name) ? item.Email ?? "Anonymous" : item.Name,
                        titles.GetValueOrDefault(item.PageId, "Deleted page"), 1m, item.CreatedAt))
                    .ToList();
            }
            case BiQueryShapes.AutomationRuns:
            {
                var names = await WorkflowNamesAsync(cancellationToken);
                return (await LoadAutomationRunsAsync(window.From, window.To, cancellationToken))
                    .Where(item => editor.Dimension switch
                    {
                        BiDimensions.Status => item.Status,
                        BiDimensions.Workflow => names.GetValueOrDefault(item.WorkflowId, "Deleted workflow"),
                        _ => MonthLabel(item.StartedAt)
                    } == pointLabel)
                    .Take(MaxDrillRows)
                    .Select(item => new BiDrillRow(names.GetValueOrDefault(item.WorkflowId, "Deleted workflow"), item.Status, 1m, item.StartedAt))
                    .ToList();
            }
            default:
                throw new InvalidOperationException("That report source is not supported.");
        }
    }

    private static string CacheKey(BiWidgetEditor editor, BiDateRange? range) =>
        $"bi:{editor.QueryShape}:{editor.Metric}:{editor.Dimension}:" +
        (range is null ? $"last{editor.RangeDays}" : $"{range.From.UtcTicks}-{range.To.UtcTicks}");

    private async Task<IReadOnlyList<BiDataPoint>> QueryPointsAsync(
        BiWidgetEditor editor, IReadOnlyList<DealProjection>? deals, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) => editor.QueryShape switch
    {
        BiQueryShapes.Deals => QueryDeals(deals!, editor, from, to),
        BiQueryShapes.ArticlePerformance => await QueryArticlesAsync(editor, from, to, cancellationToken),
        BiQueryShapes.AffiliateRevenue => await QueryAffiliateAsync(editor, from, to, cancellationToken),
        BiQueryShapes.SupportTickets => QueryTickets(await LoadTicketsAsync(from, to, cancellationToken), editor),
        BiQueryShapes.FormSubmissions => await QueryFormSubmissionsAsync(editor, from, to, cancellationToken),
        BiQueryShapes.AutomationRuns => await QueryAutomationRunsAsync(editor, from, to, cancellationToken),
        _ => throw new InvalidOperationException("That report source is not supported.")
    };

    private static string MonthLabel(DateTimeOffset value) => new DateTime(value.Year, value.Month, 1).ToString("MMM yyyy");

    // Points for a "by month" dimension stay in calendar order; everything else is largest first.
    private static List<BiDataPoint> Ordered(IEnumerable<(string Label, decimal Value, DateTimeOffset? Sort)> groups, bool byMonth) =>
        (byMonth
            ? groups.OrderBy(group => group.Sort)
            : groups.OrderByDescending(group => group.Value).ThenBy(group => group.Label))
        .Select(group => new BiDataPoint(group.Label, group.Value)).ToList();

    // SupportTickets/FormSubmissions have no unix-seconds shadow column, and SQLite can't
    // translate a DateTimeOffset range, so these load a narrow projection and filter in memory.
    private sealed record TicketProjection(string Subject, string Status, string Priority, DateTimeOffset CreatedAt,
        int? SatisfactionRating, bool Breached);

    private async Task<List<TicketProjection>> LoadTicketsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        (await db.SupportTickets.AsNoTracking()
            .Take(MaxSourceRows)
            .Select(item => new TicketProjection(item.Subject, item.Status, item.Priority, item.CreatedAt, item.SatisfactionRating,
                item.FirstResponseBreachNotifiedAt != null || item.ResolutionBreachNotifiedAt != null))
            .ToListAsync(cancellationToken))
        .Where(item => item.CreatedAt >= from && item.CreatedAt < to)
        .ToList();

    private static decimal AverageRating(IEnumerable<TicketProjection> tickets)
    {
        var ratings = tickets.Where(item => item.SatisfactionRating is not null).Select(item => (decimal)item.SatisfactionRating!.Value).ToList();
        return ratings.Count == 0 ? 0m : Math.Round(ratings.Average(), 2);
    }

    private static string TicketGroup(TicketProjection ticket, string dimension) => dimension switch
    {
        BiDimensions.Status => ticket.Status,
        BiDimensions.Priority => ticket.Priority,
        _ => MonthLabel(ticket.CreatedAt)
    };

    private static IReadOnlyList<BiDataPoint> QueryTickets(List<TicketProjection> tickets, BiWidgetEditor editor) =>
        Ordered(tickets
            .GroupBy(item => TicketGroup(item, editor.Dimension))
            .Select(group => (group.Key, editor.Metric switch
            {
                BiMetrics.AverageSatisfaction => AverageRating(group),
                BiMetrics.SlaBreaches => group.Count(item => item.Breached),
                _ => (decimal)group.Count()
            }, (DateTimeOffset?)group.Min(item => item.CreatedAt)))
            .Where(group => group.Item2 > 0), editor.Dimension == BiDimensions.Month);

    private async Task<Dictionary<Guid, string>> FormPageTitlesAsync(CancellationToken cancellationToken) =>
        await db.CmsPages.AsNoTracking().Select(page => new { page.Id, page.Title })
            .ToDictionaryAsync(page => page.Id, page => page.Title, cancellationToken);

    private async Task<List<(Guid PageId, string? Name, string? Email, DateTimeOffset CreatedAt)>> LoadFormSubmissionsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        (await db.FormSubmissions.AsNoTracking()
            .Take(MaxSourceRows)
            .Select(item => new { item.PageId, item.FullName, item.Email, item.CreatedAt })
            .ToListAsync(cancellationToken))
        .Where(item => item.CreatedAt >= from && item.CreatedAt < to)
        .Select(item => (item.PageId, item.FullName, item.Email, item.CreatedAt))
        .ToList();

    private async Task<IReadOnlyList<BiDataPoint>> QueryFormSubmissionsAsync(
        BiWidgetEditor editor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var submissions = await LoadFormSubmissionsAsync(from, to, cancellationToken);
        var titles = editor.Dimension == BiDimensions.Form ? await FormPageTitlesAsync(cancellationToken) : [];
        return Ordered(submissions
            .GroupBy(item => editor.Dimension == BiDimensions.Form
                ? titles.GetValueOrDefault(item.PageId, "Deleted page")
                : MonthLabel(item.CreatedAt))
            .Select(group => (group.Key, (decimal)group.Count(), (DateTimeOffset?)group.Min(item => item.CreatedAt))),
            editor.Dimension == BiDimensions.Month);
    }

    private async Task<List<(Guid WorkflowId, string Status, DateTimeOffset StartedAt)>> LoadAutomationRunsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var cutoff = from.ToUnixTimeSeconds();
        var end = to.ToUnixTimeSeconds();
        return (await db.AutomationExecutions.AsNoTracking()
                .Where(item => item.StartedAtUnixSeconds >= cutoff && item.StartedAtUnixSeconds < end)
                .OrderByDescending(item => item.StartedAtUnixSeconds)
                .Take(MaxSourceRows)
                .Select(item => new { item.WorkflowId, item.Status, item.StartedAtUnixSeconds })
                .ToListAsync(cancellationToken))
            .Select(item => (item.WorkflowId, item.Status, DateTimeOffset.FromUnixTimeSeconds(item.StartedAtUnixSeconds!.Value)))
            .ToList();
    }

    private async Task<Dictionary<Guid, string>> WorkflowNamesAsync(CancellationToken cancellationToken) =>
        await db.AutomationWorkflows.AsNoTracking().Select(item => new { item.Id, item.Name })
            .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

    private async Task<IReadOnlyList<BiDataPoint>> QueryAutomationRunsAsync(
        BiWidgetEditor editor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var runs = await LoadAutomationRunsAsync(from, to, cancellationToken);
        var names = editor.Dimension == BiDimensions.Workflow ? await WorkflowNamesAsync(cancellationToken) : [];
        return Ordered(runs
            .GroupBy(item => editor.Dimension switch
            {
                BiDimensions.Status => item.Status,
                BiDimensions.Workflow => names.GetValueOrDefault(item.WorkflowId, "Deleted workflow"),
                _ => MonthLabel(item.StartedAt)
            })
            .Select(group => (group.Key, (decimal)group.Count(), (DateTimeOffset?)group.Min(item => item.StartedAt))),
            editor.Dimension == BiDimensions.Month);
    }

    private sealed record BiWindow(DateTimeOffset From, DateTimeOffset To, DateTimeOffset PreviousFrom);

    private BiWindow ResolveWindow(int rangeDays, BiDateRange? range)
    {
        var (from, to) = range is null
            ? (timeProvider.GetUtcNow().AddDays(-rangeDays), timeProvider.GetUtcNow())
            : (range.From, range.To);
        return new BiWindow(from, to, from - (to - from));
    }

    private static void ValidateRange(BiDateRange range)
    {
        if (range.To <= range.From)
        {
            throw new InvalidOperationException("The end date must be after the start date.");
        }
        if ((range.To - range.From).TotalDays > MaxCustomRangeDays)
        {
            throw new InvalidOperationException("A custom range can cover at most two years.");
        }
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
        widget.IsWide = editor.IsWide;
        if (widget.GoalValue != editor.GoalValue || widget.GoalIsCeiling != editor.GoalIsCeiling)
        {
            // A changed goal starts fresh: the next check records its state without "crossing".
            widget.GoalLastMet = null;
        }
        widget.GoalValue = editor.GoalValue;
        widget.GoalIsCeiling = editor.GoalIsCeiling;
        await db.SaveChangesAsync(cancellationToken);
        return widget.Id;
    }

    public async Task MoveWidgetAsync(string ownerUsername, Guid widgetId, int direction, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var widgets = await db.BusinessIntelligenceWidgets
            .Where(item => item.OwnerUsername == owner)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var index = widgets.FindIndex(item => item.Id == widgetId);
        if (index < 0) throw new InvalidOperationException("Dashboard widget was not found.");
        var target = Math.Clamp(index + Math.Sign(direction), 0, widgets.Count - 1);
        if (target == index) return;
        var moving = widgets[index];
        widgets.RemoveAt(index);
        widgets.Insert(target, moving);
        for (var order = 0; order < widgets.Count; order++) widgets[order].SortOrder = order;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetWidgetWideAsync(string ownerUsername, Guid widgetId, bool isWide, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var widget = await db.BusinessIntelligenceWidgets
            .FirstOrDefaultAsync(item => item.Id == widgetId && item.OwnerUsername == owner, cancellationToken)
            ?? throw new InvalidOperationException("Dashboard widget was not found.");
        widget.IsWide = isWide;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BiGoalCrossing>> EvaluateGoalsAsync(CancellationToken cancellationToken = default)
    {
        var widgets = await db.BusinessIntelligenceWidgets
            .Where(item => item.GoalValue != null)
            .ToListAsync(cancellationToken);
        var crossings = new List<BiGoalCrossing>();
        foreach (var widget in widgets)
        {
            var editor = new BiWidgetEditor
            {
                QueryShape = widget.QueryShape,
                Metric = widget.Metric,
                Dimension = widget.Dimension,
                Visualization = widget.Visualization,
                RangeDays = widget.RangeDays
            };
            BiChartResult chart;
            try
            {
                chart = await PreviewCoreAsync(editor, range: null, preloadedDeals: null, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                continue; // a widget whose definition no longer validates is skipped, not fatal
            }
            var goal = widget.GoalValue!.Value;
            var met = widget.GoalIsCeiling ? chart.Total <= goal : chart.Total >= goal;
            if (widget.GoalLastMet is { } lastMet && lastMet != met)
            {
                crossings.Add(new BiGoalCrossing(widget.Id, widget.OwnerUsername, widget.Title, chart.MetricLabel,
                    chart.Total, goal, widget.GoalIsCeiling, met, chart.ValueFormat));
            }
            widget.GoalLastMet = met;
        }
        await db.SaveChangesAsync(cancellationToken);
        return crossings;
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
        IReadOnlyList<DealProjection> allDeals, BiWidgetEditor editor, DateTimeOffset from, DateTimeOffset to)
    {
        var deals = allDeals.Where(item => item.CreatedAt >= from && item.CreatedAt < to).ToList();

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
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var articlePaths = await db.Articles.AsNoTracking()
            .Where(item => item.TrashedAt == null && item.Status == ArticleStatuses.Published)
            .Select(item => new { Path = "/blog/" + item.Slug, item.Title })
            .ToDictionaryAsync(item => item.Path, item => item.Title, cancellationToken);
        var cutoff = from.ToUnixTimeSeconds();
        var end = to.ToUnixTimeSeconds();
        var events = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.OccurredAtUnixSeconds >= cutoff && item.OccurredAtUnixSeconds < end && item.EventName == WebAnalyticsEventNames.PageView)
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
            .ToList();
    }

    private async Task<IReadOnlyList<BiDataPoint>> QueryAffiliateAsync(
        BiWidgetEditor editor,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var cutoff = from.ToUnixTimeSeconds();
        var end = to.ToUnixTimeSeconds();
        var rows = await db.CjCommissionRecords.AsNoTracking()
            .Where(item => item.CreatedAtUnixSeconds >= cutoff && item.CreatedAtUnixSeconds < end)
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
