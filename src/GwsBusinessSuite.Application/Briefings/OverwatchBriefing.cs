using System.Text;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Briefings;

// One thing a briefing may mention, numbered so the model can cite it as [n].
public sealed record BriefingFact(int Number, string Kind, string Text, double? Latitude = null, double? Longitude = null);

public sealed record BriefingFacts(string Scope, DateTimeOffset At, IReadOnlyList<BriefingFact> Facts);

// A model-written briefing after its citations were checked against the facts.
public sealed record BriefingText(string Text, IReadOnlyList<int> Cited);

public static partial class OverwatchBriefing
{
    public const int MaxWords = 120;
    private const int MaxIncidents = 8;
    private const int MaxAlerts = 4;
    private const int MaxHazards = 4;
    private const int MaxCameras = 4;
    // A camera-watch reading older than this says nothing about now.
    private static readonly TimeSpan CameraReadingAge = TimeSpan.FromHours(2);
    private static readonly HashSet<string> BriefedCategories =
        [IncidentCategories.Crash, IncidentCategories.Closure, IncidentCategories.Hazard];

    public static readonly string SystemPrompt =
        "You write short traffic and weather briefings for a driver. Use ONLY the numbered facts you are given; " +
        "never add places, numbers or events that aren't in them. After each statement, cite the facts it comes from " +
        "as [n]. Lead with what most affects travel (closures, crashes, severe weather), group similar items, and say " +
        $"plainly when things look quiet. At most {MaxWords} words, plain sentences, no headings, no lists, no markdown.";

    // ---- Facts ----

    public static BriefingFacts ForArea(string scope, DateTimeOffset at,
        IEnumerable<TrafficIncident> incidents, IEnumerable<WeatherAlert> alerts,
        IEnumerable<HazardFeature> hazards, IEnumerable<OverwatchCameraWatchState> cameras)
    {
        var builder = new FactList();
        foreach (var incident in RankIncidents(incidents).Take(MaxIncidents))
            builder.Add("incident", IncidentText(incident), incident.Latitude, incident.Longitude);
        AddCommon(builder, at, alerts, hazards, cameras);
        return new BriefingFacts(scope, at, builder.Facts);
    }

    public static BriefingFacts ForRoute(string scope, DateTimeOffset at, RouteWatchResult route, IEnumerable<OverwatchCameraWatchState> cameras)
    {
        var builder = new FactList();
        builder.Add("route", $"Route {route.From} to {route.To}: {route.DistanceMiles:0} miles, about {DurationText(route.DurationMinutes)} without delays");
        var ranked = RankIncidents(route.Incidents.Select(i => i.Incident)).Take(MaxIncidents).ToHashSet();
        foreach (var item in route.Incidents.Where(i => ranked.Contains(i.Incident)).OrderBy(i => i.MilesAlong))
            builder.Add("incident", $"{IncidentText(item.Incident)}, mile {item.MilesAlong:0} of the drive", item.Incident.Latitude, item.Incident.Longitude);
        var onRoute = route.Path.Count == 0 ? cameras : cameras.Where(c => NearPath(route.Path, c.Latitude, c.Longitude));
        AddCommon(builder, at, route.Alerts, [], onRoute);
        return new BriefingFacts(scope, at, builder.Facts);
    }

    private static void AddCommon(FactList builder, DateTimeOffset at, IEnumerable<WeatherAlert> alerts,
        IEnumerable<HazardFeature> hazards, IEnumerable<OverwatchCameraWatchState> cameras)
    {
        foreach (var alert in alerts.DistinctBy(a => a.Event + "|" + a.AreaDescription)
                     .OrderBy(a => a.Severity switch { "Extreme" => 0, "Severe" => 1, "Moderate" => 2, _ => 3 }).Take(MaxAlerts))
            builder.Add("weather", $"NWS {alert.Event} ({alert.Severity.ToLowerInvariant()}) for {Shorten(alert.AreaDescription, 120)}");
        foreach (var hazard in hazards.Where(h => h.Severity is IncidentSeverityLevels.Major or IncidentSeverityLevels.Moderate)
                     .OrderBy(h => h.Severity == IncidentSeverityLevels.Major ? 0 : 1).Take(MaxHazards))
            builder.Add("hazard", hazard.Title, hazard.Latitude, hazard.Longitude);
        var freshSince = at.Subtract(CameraReadingAge).ToUnixTimeSeconds();
        foreach (var camera in cameras.Where(c => c.LastCheckedUnix >= freshSince && c.LastLevel is CameraConditions.BadLevel or CameraConditions.CautionLevel)
                     .OrderBy(c => c.LastLevel == CameraConditions.BadLevel ? 0 : 1).Take(MaxCameras))
            builder.Add("camera", $"Camera {camera.CameraName} looked {camera.LastLevel}: {camera.LastSummary} (read {DateTimeOffset.FromUnixTimeSeconds(camera.LastCheckedUnix).ToLocalGwsTime()})",
                camera.Latitude, camera.Longitude);
    }

    private static IEnumerable<TrafficIncident> RankIncidents(IEnumerable<TrafficIncident> incidents) =>
        incidents.Where(i => BriefedCategories.Contains(i.Category))
            .OrderBy(i => i.SeverityLevel switch { IncidentSeverityLevels.Major => 0, IncidentSeverityLevels.Moderate => 1, IncidentSeverityLevels.Unknown => 2, _ => 3 })
            .ThenBy(i => i.Category == IncidentCategories.Closure ? 0 : i.Category == IncidentCategories.Crash ? 1 : 2);

    private static string IncidentText(TrafficIncident incident)
    {
        var text = $"{IncidentCategories.SingularLabel(incident.Category)} on {incident.RoadwayName}";
        if (incident.SeverityLevel is IncidentSeverityLevels.Major or IncidentSeverityLevels.Moderate) text += $" ({incident.SeverityLevel})";
        var detail = Shorten(incident.Description, 100);
        return detail.Length > 0 && !detail.Equals(incident.RoadwayName, StringComparison.OrdinalIgnoreCase) ? $"{text}: {detail}" : text;
    }

    // Within about 2 miles of the drawn route.
    private static bool NearPath(IReadOnlyList<RoutePoint> path, double latitude, double longitude)
    {
        const double Limit = 0.03;
        var step = Math.Max(1, path.Count / 400);
        for (var i = 0; i < path.Count; i += step)
        {
            if (Math.Abs(path[i].Latitude - latitude) < Limit && Math.Abs(path[i].Longitude - longitude) < Limit) return true;
        }
        return false;
    }

    // ---- Writing ----

    public static string UserPrompt(BriefingFacts facts)
    {
        var text = new StringBuilder($"Briefing for {facts.Scope}, {facts.At.ToLocalGwsDateTime()}.\n");
        if (facts.Facts.Count == 0)
        {
            text.Append("Facts: none - no crashes, closures, weather alerts, hazards or camera warnings were found.");
            return text.ToString();
        }
        text.Append("Facts:\n");
        foreach (var fact in facts.Facts) text.Append('[').Append(fact.Number).Append("] ").Append(fact.Text).Append('\n');
        return text.ToString();
    }

    // Drops citations to facts that don't exist, so a model can't point at something it made up,
    // and falls back to the plain summary when nothing usable came back.
    public static BriefingText Clean(string? modelOutput, BriefingFacts facts)
    {
        var valid = facts.Facts.Select(f => f.Number).ToHashSet();
        var cited = new List<int>();
        var text = Citation().Replace((modelOutput ?? string.Empty).Trim(), match =>
        {
            var numbers = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(n => int.TryParse(n, out var value) ? value : -1).Where(valid.Contains).Distinct().ToList();
            cited.AddRange(numbers);
            return numbers.Count == 0 ? string.Empty : $" [{string.Join(", ", numbers)}]";
        });
        text = Whitespace().Replace(text, " ").Replace(" .", ".").Replace(" ,", ",").Trim();
        return text.Length == 0
            ? new BriefingText(PlainText(facts), [])
            : new BriefingText(text, cited.Distinct().Order().ToList());
    }

    // The no-model summary: the morning briefing, and the fallback when the model gives nothing.
    public static string PlainText(BriefingFacts facts)
    {
        if (facts.Facts.All(f => f.Kind == "route"))
            return "All quiet: no crashes, closures, weather alerts, hazards or camera warnings.";
        var counts = new List<string>();
        AddCount(counts, facts, "incident", "crash or closure", "crashes or closures");
        AddCount(counts, facts, "weather", "weather alert", "weather alerts");
        AddCount(counts, facts, "hazard", "hazard", "hazards");
        AddCount(counts, facts, "camera", "camera needing a look", "cameras needing a look");
        var top = facts.Facts.Where(f => f.Kind != "route").Take(3).Select(f => f.Text);
        return $"{string.Join(", ", counts)}. {string.Join("; ", top)}.";
    }

    private static void AddCount(List<string> counts, BriefingFacts facts, string kind, string one, string many)
    {
        var count = facts.Facts.Count(f => f.Kind == kind);
        if (count > 0) counts.Add($"{count} {(count == 1 ? one : many)}");
    }

    private static string DurationText(double minutes) =>
        minutes >= 60 ? $"{(int)(minutes / 60)} h {(int)(minutes % 60):00} min" : $"{Math.Max(1, (int)Math.Round(minutes))} min";

    private static string Shorten(string? text, int max)
    {
        var value = Whitespace().Replace(text ?? string.Empty, " ").Trim();
        return value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s*\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class FactList
    {
        public List<BriefingFact> Facts { get; } = [];
        public void Add(string kind, string text, double? latitude = null, double? longitude = null) =>
            Facts.Add(new BriefingFact(Facts.Count + 1, kind, text, latitude, longitude));
    }
}
