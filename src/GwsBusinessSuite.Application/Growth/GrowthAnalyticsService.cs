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
        CancellationToken cancellationToken = default)
    {
        var fromUnix = from.ToUnixTimeSeconds();
        var toUnix = to.ToUnixTimeSeconds();
        var events = await db.WebAnalyticsEvents.AsNoTracking()
            .Where(item => item.OccurredAtUnixSeconds >= fromUnix && item.OccurredAtUnixSeconds < toUnix)
            .ToListAsync(cancellationToken);
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
            Trend = BuildTrend(pageViews, from, to),
            TopPages = Breakdown(pageViews, item => item.Path),
            TopSources = Breakdown(pageViews, item =>
                !string.IsNullOrWhiteSpace(item.Source)
                    ? item.Source
                    : string.IsNullOrWhiteSpace(item.ReferrerHost) ? "Direct" : item.ReferrerHost),
            Campaigns = Breakdown(pageViews.Where(item => !string.IsNullOrWhiteSpace(item.Campaign)), item => item.Campaign),
            Devices = Breakdown(pageViews, item => item.DeviceType),
            Browsers = Breakdown(pageViews, item => item.BrowserFamily),
            Goals = goalReport.Goals
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

    private static bool Matches(AnalyticsGoal goal, WebAnalyticsEvent item)
    {
        if (goal.MatchType == AnalyticsGoalMatchTypes.Event)
            return string.Equals(item.EventName, goal.MatchValue, StringComparison.OrdinalIgnoreCase);
        if (item.EventName != WebAnalyticsEventNames.PageView) return false;
        var prefix = goal.MatchValue.EndsWith('*');
        var expected = prefix ? goal.MatchValue[..^1] : goal.MatchValue;
        return prefix
            ? item.Path.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            : string.Equals(item.Path, expected, StringComparison.OrdinalIgnoreCase);
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
}
