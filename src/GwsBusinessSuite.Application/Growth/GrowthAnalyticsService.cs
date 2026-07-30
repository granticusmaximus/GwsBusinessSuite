using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Growth;

public sealed class GrowthAnalyticsService(IAppDbContext db) : IGrowthAnalyticsService
{
    private static readonly HashSet<string> ReservedEvents =
        new([WebAnalyticsEventNames.PageView, WebAnalyticsEventNames.Engagement], StringComparer.OrdinalIgnoreCase);

    public async Task RecordAsync(
        WebAnalyticsEventInput input,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var eventName = Clean(input.EventName, 64).ToLowerInvariant();
        if (eventName.Length == 0 || (!ReservedEvents.Contains(eventName) && !IsValidCustomEvent(eventName)))
        {
            throw new ArgumentException("Invalid analytics event name.", nameof(input));
        }
        if (string.IsNullOrWhiteSpace(input.VisitorKey) || string.IsNullOrWhiteSpace(input.SessionKey))
        {
            throw new ArgumentException("Analytics session identifiers are required.", nameof(input));
        }

        var path = NormalizePath(input.Path);
        if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var referrerHost = string.Empty;
        if (Uri.TryCreate(input.Referrer, UriKind.Absolute, out var referrer))
        {
            referrerHost = Clean(referrer.Host, 160);
        }

        var (device, browser) = Classify(userAgent);
        var now = DateTimeOffset.UtcNow;
        await db.WebAnalyticsEvents.AddAsync(new WebAnalyticsEvent
        {
            EventName = eventName,
            VisitorKey = Clean(input.VisitorKey, 64),
            SessionKey = Clean(input.SessionKey, 64),
            Path = path,
            PageTitle = Clean(input.PageTitle, 180),
            ReferrerHost = referrerHost,
            Source = Clean(input.Source, 100),
            Medium = Clean(input.Medium, 100),
            Campaign = Clean(input.Campaign, 120),
            DeviceType = device,
            BrowserFamily = browser,
            EngagementSeconds = Math.Clamp(input.EngagementSeconds, 0, 86_400),
            CreatedAt = now,
            OccurredAtUnixSeconds = now.ToUnixTimeSeconds(),
            CreatedBy = "public-analytics"
        }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<GrowthAnalyticsDashboard> GetDashboardAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? segmentId = null,
        CancellationToken cancellationToken = default)
    {
        var fromUnix = from.ToUnixTimeSeconds();
        var toUnix = to.ToUnixTimeSeconds();
        var events = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.OccurredAtUnixSeconds >= fromUnix && item.OccurredAtUnixSeconds < toUnix)
            .ToListAsync(cancellationToken);
        if (segmentId is { } selectedSegmentId)
        {
            var segment = await db.AnalyticsSegments.AsNoTracking()
                .Include(item => item.Rules)
                .FirstOrDefaultAsync(item => item.Id == selectedSegmentId, cancellationToken)
                ?? throw new InvalidOperationException("The selected audience segment no longer exists.");
            events = ApplySegment(events, segment);
        }
        var pageViews = events.Where(item => item.EventName == WebAnalyticsEventNames.PageView).ToList();
        var sessions = pageViews.GroupBy(item => item.SessionKey).ToList();
        var sessionKeys = sessions.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var engagement = events.Where(item => item.EventName == WebAnalyticsEventNames.Engagement).ToList();
        var visitors = pageViews.Select(item => item.VisitorKey).Distinct(StringComparer.Ordinal).Count();
        var nowCutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var goalDefinitions = await db.AnalyticsGoals.AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var goalReport = BuildGoalReport(goalDefinitions, events, sessionKeys);
        var activeGoals = goalReport.Goals.Where(item => item.IsActive).ToList();
        var funnelDefinitions = await db.AnalyticsFunnels.AsNoTracking()
            .Include(item => item.Steps)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var retention = await BuildRetentionAsync(pageViews, from, to, cancellationToken);

        return new GrowthAnalyticsDashboard
        {
            Visitors = visitors,
            PageViews = pageViews.Count,
            Sessions = sessions.Count,
            BounceRate = sessions.Count == 0
                ? 0
                : Math.Round(sessions.Count(group => group.Count() == 1) * 100m / sessions.Count, 1),
            ViewsPerSession = sessions.Count == 0 ? 0 : Math.Round(pageViews.Count / (decimal)sessions.Count, 2),
            AverageEngagement = engagement.Count == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(engagement.Average(item => item.EngagementSeconds)),
            VisitorsNow = pageViews
                .Where(item => item.CreatedAt >= nowCutoff)
                .Select(item => item.VisitorKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            TotalConversions = activeGoals.Sum(item => item.Conversions),
            OverallConversionRate = sessions.Count == 0
                ? 0
                : Math.Round(goalReport.ActiveConvertingSessions.Count * 100m / sessions.Count, 1),
            NewVisitors = retention.NewVisitors,
            ReturningVisitors = retention.ReturningVisitors,
            ReturningVisitorRate = retention.ReturningVisitorRate,
            RetentionPeriodLabel = retention.PeriodLabel,
            RetentionCohorts = retention.Cohorts,
            Trend = BuildTrend(pageViews, from, to),
            TopPages = Breakdown(pageViews, item => item.Path),
            TopSources = Breakdown(pageViews, item =>
                !string.IsNullOrWhiteSpace(item.Source)
                    ? item.Source
                    : string.IsNullOrWhiteSpace(item.ReferrerHost) ? "Direct" : item.ReferrerHost),
            Campaigns = Breakdown(pageViews.Where(item => !string.IsNullOrWhiteSpace(item.Campaign)), item => item.Campaign),
            Devices = Breakdown(pageViews, item => item.DeviceType),
            Browsers = Breakdown(pageViews, item => item.BrowserFamily),
            Goals = goalReport.Goals,
            Funnels = BuildFunnelReport(funnelDefinitions, events)
        };
    }

    public async Task<IReadOnlyList<AnalyticsGoalView>> GetGoalsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var fromUnix = from.ToUnixTimeSeconds();
        var toUnix = to.ToUnixTimeSeconds();
        var events = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.OccurredAtUnixSeconds >= fromUnix && item.OccurredAtUnixSeconds < toUnix)
            .ToListAsync(cancellationToken);
        var definitions = await db.AnalyticsGoals.AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        var sessionKeys = events
            .Where(item => item.EventName == WebAnalyticsEventNames.PageView)
            .Select(item => item.SessionKey)
            .ToHashSet(StringComparer.Ordinal);
        return BuildGoalReport(definitions, events, sessionKeys).Goals;
    }

    public async Task SaveGoalAsync(AnalyticsGoalInput input, CancellationToken cancellationToken = default)
    {
        var name = Clean(input.Name, 120);
        var matchType = Clean(input.MatchType, 24);
        var matchValue = Clean(input.MatchValue, 500);
        if (name.Length == 0) throw new ArgumentException("Goal name is required.", nameof(input));
        if (!AnalyticsGoalMatchTypes.All.Contains(matchType, StringComparer.Ordinal))
            throw new ArgumentException("Choose an event or page-path goal.", nameof(input));
        if (matchValue.Length == 0)
            throw new ArgumentException("Enter an event name or public page path.", nameof(input));
        matchValue = matchType == AnalyticsGoalMatchTypes.Event
            ? matchValue.ToLowerInvariant()
            : NormalizeGoalPath(matchValue);
        if ((matchType == AnalyticsGoalMatchTypes.Event && !IsValidCustomEvent(matchValue))
            || (matchType == AnalyticsGoalMatchTypes.PagePath
                && matchValue.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Enter a valid event name or public page path.", nameof(input));

        var duplicateName = await db.AnalyticsGoals.AsNoTracking().AnyAsync(
            item => item.Name.ToLower() == name.ToLower() && (!input.Id.HasValue || item.Id != input.Id.Value),
            cancellationToken);
        if (duplicateName) throw new InvalidOperationException("A goal with that name already exists.");
        var duplicateMatch = await db.AnalyticsGoals.AsNoTracking().AnyAsync(
            item => item.MatchType == matchType
                && item.MatchValue == matchValue
                && (!input.Id.HasValue || item.Id != input.Id.Value),
            cancellationToken);
        if (duplicateMatch) throw new InvalidOperationException("A goal already tracks that event or page path.");

        var goal = input.Id is { } id
            ? await db.AnalyticsGoals.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Goal no longer exists.")
            : new AnalyticsGoal { Name = name, MatchValue = matchValue };
        goal.Name = name;
        goal.MatchType = matchType;
        goal.MatchValue = matchValue;
        goal.IsActive = input.IsActive;
        goal.UpdatedAt = DateTimeOffset.UtcNow;
        if (input.Id is null) await db.AnalyticsGoals.AddAsync(goal, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteGoalAsync(Guid goalId, CancellationToken cancellationToken = default)
    {
        var goal = await db.AnalyticsGoals.FirstOrDefaultAsync(item => item.Id == goalId, cancellationToken);
        if (goal is null) return;
        db.AnalyticsGoals.Remove(goal);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AnalyticsFunnelView>> GetFunnelsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var fromUnix = from.ToUnixTimeSeconds();
        var toUnix = to.ToUnixTimeSeconds();
        var events = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.OccurredAtUnixSeconds >= fromUnix && item.OccurredAtUnixSeconds < toUnix)
            .ToListAsync(cancellationToken);
        var definitions = await db.AnalyticsFunnels.AsNoTracking()
            .Include(item => item.Steps)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return BuildFunnelReport(definitions, events);
    }

    public async Task SaveFunnelAsync(AnalyticsFunnelInput input, CancellationToken cancellationToken = default)
    {
        var name = Clean(input.Name, 120);
        if (name.Length == 0) throw new ArgumentException("Funnel name is required.", nameof(input));
        if (input.Steps.Count is < 2 or > 8)
            throw new ArgumentException("A funnel must contain between 2 and 8 ordered steps.", nameof(input));

        var steps = input.Steps.Select((step, index) =>
        {
            var stepName = Clean(step.Name, 120);
            if (stepName.Length == 0) throw new ArgumentException($"Step {index + 1} needs a name.", nameof(input));
            var (matchType, matchValue) = NormalizeMatchRule(step.MatchType, step.MatchValue, input);
            return new AnalyticsFunnelStep
            {
                Name = stepName,
                MatchType = matchType,
                MatchValue = matchValue,
                SortOrder = index
            };
        }).ToList();

        var duplicateName = await db.AnalyticsFunnels.AsNoTracking().AnyAsync(
            item => item.Name.ToLower() == name.ToLower() && (!input.Id.HasValue || item.Id != input.Id.Value),
            cancellationToken);
        if (duplicateName) throw new InvalidOperationException("A funnel with that name already exists.");

        await using var transaction = input.Id.HasValue
            ? await db.BeginTransactionAsync(cancellationToken)
            : null;
        AnalyticsFunnel funnel;
        if (input.Id is { } id)
        {
            funnel = await db.AnalyticsFunnels
                .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Funnel no longer exists.");
            await db.AnalyticsFunnelSteps
                .Where(item => item.AnalyticsFunnelId == id)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            funnel = new AnalyticsFunnel { Name = name };
            await db.AnalyticsFunnels.AddAsync(funnel, cancellationToken);
        }

        funnel.Name = name;
        funnel.IsActive = input.IsActive;
        funnel.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var step in steps)
        {
            step.AnalyticsFunnelId = funnel.Id;
            await db.AnalyticsFunnelSteps.AddAsync(step, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteFunnelAsync(Guid funnelId, CancellationToken cancellationToken = default)
    {
        var funnel = await db.AnalyticsFunnels.FirstOrDefaultAsync(item => item.Id == funnelId, cancellationToken);
        if (funnel is null) return;
        db.AnalyticsFunnels.Remove(funnel);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AnalyticsSegmentView>> GetSegmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var segments = await db.AnalyticsSegments.AsNoTracking()
            .Include(item => item.Rules)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return segments.Select(ToView).ToList();
    }

    public async Task<Guid> SaveSegmentAsync(
        AnalyticsSegmentInput input,
        CancellationToken cancellationToken = default)
    {
        var name = Clean(input.Name, 120);
        if (name.Length == 0) throw new ArgumentException("Segment name is required.", nameof(input));
        if (input.Rules.Count is < 1 or > 5)
            throw new ArgumentException("A segment must contain between 1 and 5 rules.", nameof(input));

        var rules = input.Rules.Select((rule, index) =>
        {
            var dimension = AnalyticsSegmentDimensions.All.FirstOrDefault(
                item => string.Equals(item, Clean(rule.Dimension, 24), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Rule {index + 1} has an unsupported dimension.", nameof(input));
            var matchOperator = AnalyticsSegmentOperators.All.FirstOrDefault(
                item => string.Equals(item, Clean(rule.Operator, 24), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Rule {index + 1} has an unsupported operator.", nameof(input));
            var value = Clean(rule.Value, 500);
            if (value.Length == 0) throw new ArgumentException($"Rule {index + 1} needs a value.", nameof(input));
            if (dimension == AnalyticsSegmentDimensions.PagePath)
            {
                value = NormalizePath(value);
                if (value.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Segments cannot target authenticated admin routes.", nameof(input));
            }
            else if (dimension == AnalyticsSegmentDimensions.Event)
            {
                value = value.ToLowerInvariant();
                if (!ReservedEvents.Contains(value) && !IsValidCustomEvent(value))
                    throw new ArgumentException($"Rule {index + 1} has an invalid event name.", nameof(input));
            }

            return new AnalyticsSegmentRule
            {
                Dimension = dimension,
                Operator = matchOperator,
                Value = value,
                SortOrder = index
            };
        }).ToList();

        var duplicateName = await db.AnalyticsSegments.AsNoTracking().AnyAsync(
            item => item.Name.ToLower() == name.ToLower() && (!input.Id.HasValue || item.Id != input.Id.Value),
            cancellationToken);
        if (duplicateName) throw new InvalidOperationException("A segment with that name already exists.");

        await using var transaction = input.Id.HasValue
            ? await db.BeginTransactionAsync(cancellationToken)
            : null;
        AnalyticsSegment segment;
        if (input.Id is { } id)
        {
            segment = await db.AnalyticsSegments.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Segment no longer exists.");
            await db.AnalyticsSegmentRules
                .Where(item => item.AnalyticsSegmentId == id)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            segment = new AnalyticsSegment { Name = name };
            await db.AnalyticsSegments.AddAsync(segment, cancellationToken);
        }

        segment.Name = name;
        segment.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var rule in rules)
        {
            rule.AnalyticsSegmentId = segment.Id;
            await db.AnalyticsSegmentRules.AddAsync(rule, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return segment.Id;
    }

    public async Task DeleteSegmentAsync(Guid segmentId, CancellationToken cancellationToken = default)
    {
        var segment = await db.AnalyticsSegments.FirstOrDefaultAsync(item => item.Id == segmentId, cancellationToken);
        if (segment is null) return;
        db.AnalyticsSegments.Remove(segment);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<WebAnalyticsEvent> ApplySegment(
        IReadOnlyCollection<WebAnalyticsEvent> events,
        AnalyticsSegment segment)
    {
        var rules = segment.Rules.OrderBy(rule => rule.SortOrder).ToList();
        if (rules.Count == 0) return [];
        var matchingSessions = events
            .GroupBy(item => item.SessionKey, StringComparer.Ordinal)
            .Where(session => rules.All(rule => session.Any(item => Matches(rule, item))))
            .Select(session => session.Key)
            .ToHashSet(StringComparer.Ordinal);
        return events.Where(item => matchingSessions.Contains(item.SessionKey)).ToList();
    }

    private async Task<RetentionReport> BuildRetentionAsync(
        IReadOnlyCollection<WebAnalyticsEvent> pageViews,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var visitorKeys = pageViews
            .Select(item => item.VisitorKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (visitorKeys.Count == 0) return new(0, 0, 0, "Day", []);

        var firstVisits = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.EventName == WebAnalyticsEventNames.PageView
                && visitorKeys.Contains(item.VisitorKey))
            .GroupBy(item => item.VisitorKey)
            .Select(group => new
            {
                VisitorKey = group.Key,
                FirstVisitUnixSeconds = group.Min(item => item.OccurredAtUnixSeconds)
            })
            .ToDictionaryAsync(
                item => item.VisitorKey,
                item => item.FirstVisitUnixSeconds,
                StringComparer.Ordinal,
                cancellationToken);

        var fromUnix = from.ToUnixTimeSeconds();
        var newVisitorKeys = visitorKeys
            .Where(key => firstVisits.GetValueOrDefault(key, long.MaxValue) >= fromUnix)
            .ToHashSet(StringComparer.Ordinal);
        var returningVisitors = visitorKeys.Count - newVisitorKeys.Count;
        var rangeDays = Math.Max(1, (int)Math.Round((to - from).TotalDays, MidpointRounding.AwayFromZero));
        var periodDays = rangeDays <= 14 ? 1 : 7;
        var periodLabel = periodDays == 1 ? "Day" : "Week";
        var periodCount = Math.Min(8, (int)Math.Ceiling(rangeDays / (double)periodDays));
        var reportStart = DateOnly.FromDateTime(from.UtcDateTime);
        var reportEnd = DateOnly.FromDateTime(to.AddTicks(-1).UtcDateTime);
        var activityByVisitor = pageViews
            .GroupBy(item => item.VisitorKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => DateOnly.FromDateTime(
                        DateTimeOffset.FromUnixTimeSeconds(item.OccurredAtUnixSeconds).UtcDateTime))
                    .Distinct()
                    .ToList(),
                StringComparer.Ordinal);

        var cohorts = newVisitorKeys
            .Select(key =>
            {
                var firstDate = DateOnly.FromDateTime(
                    DateTimeOffset.FromUnixTimeSeconds(firstVisits[key]).UtcDateTime);
                var bucket = Math.Max(0, (firstDate.DayNumber - reportStart.DayNumber) / periodDays);
                return new { VisitorKey = key, CohortStart = reportStart.AddDays(bucket * periodDays) };
            })
            .GroupBy(item => item.CohortStart)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var cohortKeys = group.Select(item => item.VisitorKey).ToHashSet(StringComparer.Ordinal);
                var cells = Enumerable.Range(0, periodCount)
                    .Select(periodIndex =>
                    {
                        var periodStart = group.Key.AddDays(periodIndex * periodDays);
                        if (periodStart > reportEnd)
                            return new AnalyticsRetentionCell(periodIndex, null, null);
                        var periodEnd = periodStart.AddDays(periodDays);
                        var activeVisitors = cohortKeys.Count(key =>
                            activityByVisitor.GetValueOrDefault(key, [])
                                .Any(date => date >= periodStart && date < periodEnd));
                        return new AnalyticsRetentionCell(
                            periodIndex,
                            activeVisitors,
                            cohortKeys.Count == 0
                                ? 0
                                : Math.Round(activeVisitors * 100m / cohortKeys.Count, 1));
                    })
                    .ToList();
                return new AnalyticsRetentionCohort(group.Key, cohortKeys.Count, cells);
            })
            .ToList();

        return new(
            newVisitorKeys.Count,
            returningVisitors,
            visitorKeys.Count == 0 ? 0 : Math.Round(returningVisitors * 100m / visitorKeys.Count, 1),
            periodLabel,
            cohorts);
    }

    private static bool Matches(AnalyticsSegmentRule rule, WebAnalyticsEvent item)
    {
        var value = rule.Dimension switch
        {
            AnalyticsSegmentDimensions.PagePath when item.EventName == WebAnalyticsEventNames.PageView => item.Path,
            AnalyticsSegmentDimensions.Event => item.EventName,
            AnalyticsSegmentDimensions.Source when item.EventName == WebAnalyticsEventNames.PageView => SourceLabel(item),
            AnalyticsSegmentDimensions.Medium when item.EventName == WebAnalyticsEventNames.PageView =>
                string.IsNullOrWhiteSpace(item.Medium) ? "Direct" : item.Medium,
            AnalyticsSegmentDimensions.Campaign when item.EventName == WebAnalyticsEventNames.PageView => item.Campaign,
            AnalyticsSegmentDimensions.Referrer when item.EventName == WebAnalyticsEventNames.PageView =>
                string.IsNullOrWhiteSpace(item.ReferrerHost) ? "Direct" : item.ReferrerHost,
            AnalyticsSegmentDimensions.Device when item.EventName == WebAnalyticsEventNames.PageView => item.DeviceType,
            AnalyticsSegmentDimensions.Browser when item.EventName == WebAnalyticsEventNames.PageView => item.BrowserFamily,
            _ => null
        };
        if (value is null) return false;
        return rule.Operator switch
        {
            AnalyticsSegmentOperators.Is => string.Equals(value, rule.Value, StringComparison.OrdinalIgnoreCase),
            AnalyticsSegmentOperators.Contains => value.Contains(rule.Value, StringComparison.OrdinalIgnoreCase),
            AnalyticsSegmentOperators.StartsWith => value.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static AnalyticsSegmentView ToView(AnalyticsSegment segment) =>
        new(
            segment.Id,
            segment.Name,
            segment.Rules
                .OrderBy(rule => rule.SortOrder)
                .Select(rule => new AnalyticsSegmentRuleView(
                    rule.Id,
                    rule.Dimension,
                    rule.Operator,
                    rule.Value,
                    rule.SortOrder))
                .ToList());

    private static IReadOnlyList<AnalyticsPoint> BuildTrend(
        IReadOnlyCollection<WebAnalyticsEvent> pageViews,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var byDay = pageViews
            .GroupBy(item => DateOnly.FromDateTime(item.CreatedAt.UtcDateTime))
            .ToDictionary(
                group => group.Key,
                group => new AnalyticsPoint(
                    group.Key,
                    group.Select(item => item.VisitorKey).Distinct(StringComparer.Ordinal).Count(),
                    group.Count()));

        var result = new List<AnalyticsPoint>();
        for (var day = DateOnly.FromDateTime(from.UtcDateTime);
             day <= DateOnly.FromDateTime(to.AddTicks(-1).UtcDateTime);
             day = day.AddDays(1))
        {
            result.Add(byDay.GetValueOrDefault(day) ?? new AnalyticsPoint(day, 0, 0));
        }
        return result;
    }

    private static IReadOnlyList<AnalyticsBreakdownRow> Breakdown(
        IEnumerable<WebAnalyticsEvent> source,
        Func<WebAnalyticsEvent, string> keySelector)
    {
        var rows = source.ToList();
        if (rows.Count == 0) return [];

        return rows
            .GroupBy(item => string.IsNullOrWhiteSpace(keySelector(item)) ? "Unknown" : keySelector(item))
            .Select(group => new AnalyticsBreakdownRow(
                group.Key,
                group.Select(item => item.VisitorKey).Distinct(StringComparer.Ordinal).Count(),
                group.Count(),
                Math.Round(group.Count() * 100m / rows.Count, 1)))
            .OrderByDescending(item => item.Views)
            .ThenBy(item => item.Label)
            .Take(12)
            .ToList();
    }

    private static GoalReport BuildGoalReport(
        IReadOnlyCollection<AnalyticsGoal> definitions,
        IReadOnlyCollection<WebAnalyticsEvent> events,
        IReadOnlySet<string> sessionKeys)
    {
        var activeConvertingSessions = new HashSet<string>(StringComparer.Ordinal);
        var goals = definitions.Select(goal =>
        {
            var matches = events.Where(item => Matches(goal, item)).ToList();
            var convertingSessions = matches
                .Where(item => sessionKeys.Contains(item.SessionKey))
                .Select(item => item.SessionKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (goal.IsActive) activeConvertingSessions.UnionWith(convertingSessions);
            var topSource = matches
                .Select(SourceLabel)
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .FirstOrDefault() ?? "—";
            return new AnalyticsGoalView(
                goal.Id,
                goal.Name,
                goal.MatchType,
                goal.MatchValue,
                goal.IsActive,
                matches.Count,
                matches.Select(item => item.VisitorKey).Distinct(StringComparer.Ordinal).Count(),
                sessionKeys.Count == 0 ? 0 : Math.Round(convertingSessions.Count * 100m / sessionKeys.Count, 1),
                matches.Count == 0 ? null : matches.Max(item => item.CreatedAt),
                topSource);
        }).ToList();
        return new(goals, activeConvertingSessions);
    }

    private static IReadOnlyList<AnalyticsFunnelView> BuildFunnelReport(
        IReadOnlyCollection<AnalyticsFunnel> definitions,
        IReadOnlyCollection<WebAnalyticsEvent> events)
    {
        var sessions = events
            .GroupBy(item => item.SessionKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.OccurredAtUnixSeconds)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToList())
            .ToList();

        return definitions.Select(funnel =>
        {
            var orderedSteps = funnel.Steps.OrderBy(step => step.SortOrder).ToList();
            var reached = new int[orderedSteps.Count];
            foreach (var session in sessions)
            {
                var nextStep = 0;
                foreach (var analyticsEvent in session)
                {
                    if (nextStep >= orderedSteps.Count) break;
                    var step = orderedSteps[nextStep];
                    if (!Matches(step.MatchType, step.MatchValue, analyticsEvent)) continue;
                    reached[nextStep]++;
                    nextStep++;
                }
            }

            var stepViews = orderedSteps.Select((step, index) =>
            {
                var previous = index == 0 ? reached[index] : reached[index - 1];
                var next = index + 1 < reached.Length ? reached[index + 1] : reached[index];
                var dropOff = reached[index] - next;
                return new AnalyticsFunnelStepView(
                    step.Id,
                    step.Name,
                    step.MatchType,
                    step.MatchValue,
                    step.SortOrder,
                    reached[index],
                    dropOff,
                    previous == 0 ? 0 : Math.Round(reached[index] * 100m / previous, 1),
                    reached[index] == 0 ? 0 : Math.Round(dropOff * 100m / reached[index], 1));
            }).ToList();
            var started = reached.Length == 0 ? 0 : reached[0];
            var completed = reached.Length == 0 ? 0 : reached[^1];
            return new AnalyticsFunnelView(
                funnel.Id,
                funnel.Name,
                funnel.IsActive,
                started,
                completed,
                started == 0 ? 0 : Math.Round(completed * 100m / started, 1),
                stepViews);
        }).ToList();
    }

    private static bool Matches(AnalyticsGoal goal, WebAnalyticsEvent item) =>
        Matches(goal.MatchType, goal.MatchValue, item);

    private static bool Matches(string matchType, string matchValue, WebAnalyticsEvent item)
    {
        if (matchType == AnalyticsGoalMatchTypes.Event)
            return string.Equals(item.EventName, matchValue, StringComparison.OrdinalIgnoreCase);
        if (item.EventName != WebAnalyticsEventNames.PageView) return false;
        var prefix = matchValue.EndsWith('*');
        var expected = prefix ? matchValue[..^1] : matchValue;
        return prefix
            ? item.Path.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            : string.Equals(item.Path, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (string MatchType, string MatchValue) NormalizeMatchRule(
        string? rawMatchType,
        string? rawMatchValue,
        object input)
    {
        var matchType = Clean(rawMatchType, 24);
        var matchValue = Clean(rawMatchValue, 500);
        if (!AnalyticsGoalMatchTypes.All.Contains(matchType, StringComparer.Ordinal))
            throw new ArgumentException("Choose an event or page-path match for every step.", nameof(input));
        if (matchValue.Length == 0)
            throw new ArgumentException("Every step needs an event name or public page path.", nameof(input));
        matchValue = matchType == AnalyticsGoalMatchTypes.Event
            ? matchValue.ToLowerInvariant()
            : NormalizeGoalPath(matchValue);
        if ((matchType == AnalyticsGoalMatchTypes.Event && !IsValidCustomEvent(matchValue))
            || (matchType == AnalyticsGoalMatchTypes.PagePath
                && matchValue.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Enter a valid event name or public page path for every step.", nameof(input));
        return (matchType, matchValue);
    }

    private static string SourceLabel(WebAnalyticsEvent item) =>
        !string.IsNullOrWhiteSpace(item.Source)
            ? item.Source
            : string.IsNullOrWhiteSpace(item.ReferrerHost) ? "Direct" : item.ReferrerHost;

    private static string NormalizePath(string? value)
    {
        var path = Clean(value, 500);
        if (!path.StartsWith('/')) path = $"/{path}";
        var queryIndex = path.IndexOf('?');
        return queryIndex < 0 ? path : path[..queryIndex];
    }

    private static string NormalizeGoalPath(string? value)
    {
        var clean = Clean(value, 500);
        var wildcard = clean.EndsWith('*');
        var path = NormalizePath(wildcard ? clean[..^1] : clean);
        return wildcard ? $"{path}*" : path;
    }

    private static bool IsValidCustomEvent(string eventName) =>
        eventName.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string Clean(string? value, int maxLength)
    {
        var clean = (value ?? string.Empty).Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static (string Device, string Browser) Classify(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        var device = ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
            ? "Mobile"
            : ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
                ? "Tablet"
                : "Desktop";
        var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? "Opera"
            : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "Other";
        return (device, browser);
    }

    private sealed record GoalReport(
        IReadOnlyList<AnalyticsGoalView> Goals,
        HashSet<string> ActiveConvertingSessions);

    private sealed record RetentionReport(
        int NewVisitors,
        int ReturningVisitors,
        decimal ReturningVisitorRate,
        string PeriodLabel,
        IReadOnlyList<AnalyticsRetentionCohort> Cohorts);
}
