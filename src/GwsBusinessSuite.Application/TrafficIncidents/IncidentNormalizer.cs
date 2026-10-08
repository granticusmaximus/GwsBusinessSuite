using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.TrafficIncidents;

public static class IncidentCategories
{
    public const string Crash = "crash";
    public const string Closure = "closure";
    public const string Roadwork = "roadwork";
    public const string Hazard = "hazard";
    public const string Event = "event";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Crash, Closure, Roadwork, Hazard, Event, Other];

    public static string Label(string category) => category switch
    {
        Crash => "Crashes",
        Closure => "Closures",
        Roadwork => "Roadwork",
        Hazard => "Hazards & weather",
        Event => "Events",
        _ => "Other"
    };
}

public static class IncidentSeverityLevels
{
    public const string Major = "major";
    public const string Moderate = "moderate";
    public const string Minor = "minor";
    public const string Unknown = "unknown";
}

// Every incident source has its own vocabulary (GDOT "accidentsAndIncidents", WZDx "work-zone",
// CARS511 styles, Thruway categories, Queensland priorities...). The raw EventType/Severity stay
// on the incident; this maps them onto one small shared set so the globe can filter by type and
// show a comparable severity. Keyword-based on purpose: a new source's wording usually lands in
// the right bucket without code changes, and anything unrecognized stays "other"/"unknown"
// rather than being guessed.
public static partial class IncidentNormalizer
{
    public static TrafficIncident Normalize(TrafficIncident incident)
    {
        var category = string.IsNullOrWhiteSpace(incident.Category) ? Categorize(incident) : incident.Category;
        var severity = string.IsNullOrWhiteSpace(incident.SeverityLevel) ? SeverityOf(incident, category) : incident.SeverityLevel;
        var lanes = incident.Lanes ?? LanesFrom(incident.Description);
        return incident with { Category = category, SeverityLevel = severity, Lanes = lanes };
    }

    public static string Categorize(TrafficIncident incident) =>
        FromText(incident.EventType) ?? FromText(incident.Description) ?? FromText(incident.Severity) ?? IncidentCategories.Other;

    // Order matters: a crash that closes the road is still a crash; roadwork with a lane
    // closure is still roadwork. A bare "closure" only wins when nothing more specific matched.
    private static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Spaced(text);
        if (CrashWords().IsMatch(t)) return IncidentCategories.Crash;
        if (RoadworkWords().IsMatch(t)) return IncidentCategories.Roadwork;
        if (EventWords().IsMatch(t)) return IncidentCategories.Event;
        if (HazardWords().IsMatch(t)) return IncidentCategories.Hazard;
        if (ClosureWords().IsMatch(t)) return IncidentCategories.Closure;
        return null;
    }

    public static string SeverityOf(TrafficIncident incident, string category)
    {
        var t = Spaced(incident.Severity);
        // "No delays" / "all lanes open" contain words the other levels look for, so the
        // explicit no-impact phrases are checked first.
        if (NoImpactWords().IsMatch(t)) return IncidentSeverityLevels.Minor;
        if (MajorWords().IsMatch(t)) return IncidentSeverityLevels.Major;
        if (ModerateWords().IsMatch(t)) return IncidentSeverityLevels.Moderate;
        if (MinorWords().IsMatch(t)) return IncidentSeverityLevels.Minor;
        // A full closure is major even when the source gives no severity.
        if (category == IncidentCategories.Closure || AllLanesClosed().IsMatch(Spaced(incident.Description)))
            return IncidentSeverityLevels.Major;
        return IncidentSeverityLevels.Unknown;
    }

    public static string? LanesFrom(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var m = LanePhrase().Match(description);
        if (!m.Success) return null;
        var phrase = Regex.Replace(m.Value.Trim(), @"\s+", " ");
        return char.ToUpperInvariant(phrase[0]) + phrase[1..].ToLowerInvariant();
    }

    // WZDx vehicle_impact values -> plain language.
    public static string? WzdxLanes(string? vehicleImpact) => vehicleImpact switch
    {
        "all-lanes-closed" => "All lanes closed",
        "some-lanes-closed" => "Some lanes closed",
        "all-lanes-open" => "All lanes open",
        "alternating-one-way" => "Alternating one-way traffic",
        "some-lanes-closed-merge-left" => "Some lanes closed, merge left",
        "some-lanes-closed-merge-right" => "Some lanes closed, merge right",
        "all-lanes-open-shift-left" => "All lanes open, shifted left",
        "all-lanes-open-shift-right" => "All lanes open, shifted right",
        "some-lanes-closed-split" => "Some lanes closed, traffic split",
        "flagging" => "Flagger controlling traffic",
        "temporary-traffic-signal" => "Temporary traffic signal",
        _ => null
    };

    // "accidentsAndIncidents" / "work-zone" / "road_work" -> "accidents and incidents" etc.
    private static string Spaced(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty
            : CamelBoundary().Replace(text, "$1 $2").Replace('-', ' ').Replace('_', ' ').ToLowerInvariant();

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"\b(accidents?|crash(es)?|collisions?|wreck|overturned|rollover|vehicle fire|incidents?)\b")]
    private static partial Regex CrashWords();

    [GeneratedRegex(@"\b(road ?works?|work ?zones?|construction|maintenance|paving|resurfacing|bridge (work|repair)|utility work|striping|mowing)\b")]
    private static partial Regex RoadworkWords();

    [GeneratedRegex(@"\b(special events?|events?|parade|concert|game day|marathon|festival)\b")]
    private static partial Regex EventWords();

    [GeneratedRegex(@"\b(weather|flood(ing)?|ice|icy|snow|fog|wind|debris|hazard|disabled vehicle|stalled|wildfire|smoke|downed|landslide|rock ?fall|animal)\b")]
    private static partial Regex HazardWords();

    [GeneratedRegex(@"\b(closures?|closed|road closed|lanes? closed)\b")]
    private static partial Regex ClosureWords();

    [GeneratedRegex(@"\b(major|high|severe|critical|serious|all lanes closed|full closure|closure|extreme)\b")]
    private static partial Regex MajorWords();

    [GeneratedRegex(@"\b(moderate|medium|some lanes closed|lane closure|delays?)\b")]
    private static partial Regex ModerateWords();

    [GeneratedRegex(@"\b(minor|low|shoulder)\b")]
    private static partial Regex MinorWords();

    [GeneratedRegex(@"\b(no delays?|all lanes open)\b")]
    private static partial Regex NoImpactWords();

    [GeneratedRegex(@"\ball lanes (are )?(closed|blocked)\b")]
    private static partial Regex AllLanesClosed();

    [GeneratedRegex(@"(?i)\b(all lanes|(?:(?:left|right|center|middle) |(?:\d+|one|two|three|four) (?:of \d+ )?)?(?:lanes?|shoulder))\s+(?:are\s+|is\s+)?(closed|blocked|open)\b")]
    private static partial Regex LanePhrase();
}
