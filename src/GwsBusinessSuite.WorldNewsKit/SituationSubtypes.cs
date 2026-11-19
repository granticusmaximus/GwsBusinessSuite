using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// What kind of disaster, transport or conflict story a situation is (18-11-26): its kind narrowed by
// the words its headlines and summary use, with the Bootstrap icon Overwatch draws on its pin.
// Confidence says how much the words agree: High is two or more of the subtype's words, Medium one,
// Low none (the kind's own icon, no subtype).
public sealed record SituationTag(string Kind, string? Subtype, string Label, string Icon, string Confidence)
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    // Chosen by SentinelGPT for a story the words couldn't tag (SubtypeRefinement, 19-11-26).
    public const string Model = "model";
}

// SentinelGPT tags a story the keyword rules left untagged (19-11-26, deferred from the grouping
// plan): it picks one of the kind's subtypes, or none. Its answer is saved per story, so each is
// asked once, and the keyword tag still wins whenever it finds one.
public static class SubtypeRefinement
{
    public const int MaxPerRead = 5;
    public const string None = "none";

    public const string SystemPrompt =
        "You label news stories. Reply with exactly one id from the list, or none if no id clearly fits. " +
        "Reply with the id only: no other words, no punctuation.";

    public static string Prompt(string kind, IReadOnlyList<string> headlines) =>
        $"Story ({SituationKinds.Label(kind)}):\n{string.Join('\n', headlines.Take(6).Select(h => "- " + h))}\n\nIds:\n" +
        string.Join('\n', SituationSubtypes.Choices(kind).Select(c => $"{c.Id}: {c.Label}")) + $"\n{None}: none of these";

    // The id the model gave, if it's one of the kind's (or "none"); anything else is no answer.
    public static string? Parse(string kind, string? answer)
    {
        var word = (answer ?? "").Trim().Trim('.', '"', '\'', '`').ToLowerInvariant();
        if (word == None) return None;
        return SituationSubtypes.Choices(kind).Any(c => c.Id == word) ? word : null;
    }
}

public static partial class SituationSubtypes
{
    private sealed record Subtype(string Kind, string Id, string Label, string Icon, string[] Words);

    // Within a kind, the subtype with the most hits wins, and on a tie the one listed first.
    // Icons are Bootstrap Icons 1.11.3 names (the set App.razor loads), so each one has a glyph.
    private static readonly Subtype[] All =
    [
        new(SituationKinds.Disaster, "earthquake", "Earthquake", "activity", ["earthquake", "earthquakes", "quake", "quakes", "tremor", "aftershock", "seismic", "magnitude"]),
        new(SituationKinds.Disaster, "tsunami", "Tsunami", "tsunami", ["tsunami", "tsunamis"]),
        new(SituationKinds.Disaster, "hurricane", "Hurricane or typhoon", "hurricane", ["hurricane", "typhoon", "cyclone", "landfall"]),
        new(SituationKinds.Disaster, "tornado", "Tornado", "tornado", ["tornado", "tornadoes", "twister"]),
        new(SituationKinds.Disaster, "wildfire", "Wildfire", "fire", ["wildfire", "wildfires", "bushfire", "bushfires", "blaze", "firefighters", "forest fire"]),
        new(SituationKinds.Disaster, "flood", "Flood", "cloud-rain-heavy-fill", ["flood", "floods", "flooding", "flash flood", "torrential", "landslide", "landslides", "mudslide"]),
        new(SituationKinds.Disaster, "storm", "Storm", "cloud-lightning-rain", ["storm", "storms", "thunderstorm", "hailstorm", "gale"]),
        new(SituationKinds.Disaster, "winter", "Snow and ice", "snow", ["snow", "snowstorm", "blizzard", "avalanche", "ice storm"]),
        new(SituationKinds.Disaster, "heat", "Heat or drought", "thermometer-sun", ["heatwave", "heat wave", "drought", "record temperatures"]),
        new(SituationKinds.Disaster, "explosion", "Explosion", "exclamation-octagon-fill", ["explosion", "explosions", "blast", "exploded"]),
        new(SituationKinds.Disaster, "nuclear", "Radiation", "radioactive", ["radiation", "radioactive", "nuclear plant", "nuclear power plant"]),
        new(SituationKinds.Disaster, "volcano", "Volcano", "exclamation-triangle-fill", ["volcano", "volcanic", "eruption", "erupts", "lava"]),

        new(SituationKinds.Transport, "aviation", "Aviation", "airplane-fill", ["plane", "planes", "flight", "flights", "airport", "airports", "airline", "airlines", "aircraft", "jet", "runway"]),
        new(SituationKinds.Transport, "rail", "Rail", "train-front-fill", ["train", "trains", "rail", "railway", "railways", "derailment", "derails", "derailed", "metro", "subway"]),
        new(SituationKinds.Transport, "maritime", "Shipping", "life-preserver", ["ship", "ships", "ferry", "vessel", "vessels", "boat", "tanker", "cargo", "shipping", "port", "sinks", "capsized", "cruise"]),
        new(SituationKinds.Transport, "bus", "Bus", "bus-front-fill", ["bus", "buses", "coach crash"]),
        new(SituationKinds.Transport, "road", "Roads", "car-front-fill", ["motorway", "highway", "road", "roads", "pileup", "pile-up", "car crash", "traffic"]),
        new(SituationKinds.Transport, "strike", "Transport strike", "signpost-split", ["strike", "strikes", "walkout", "industrial action"]),

        new(SituationKinds.Cyber, "ransomware", "Ransomware or malware", "bug-fill", ["ransomware", "malware", "spyware", "trojan"]),
        new(SituationKinds.Cyber, "outage", "Outage", "wifi-off", ["outage", "outages", "offline", "ddos", "down for"]),
        new(SituationKinds.Cyber, "breach", "Data breach", "shield-lock-fill", ["breach", "breached", "leak", "leaked", "stolen data", "data theft", "exposed"]),

        new(SituationKinds.Conflict, "strikes", "Strikes and shelling", "crosshair", ["missile", "missiles", "airstrike", "airstrikes", "air strike", "drone", "drones", "shelling", "rocket", "rockets", "bombardment"]),
        new(SituationKinds.Conflict, "unrest", "Protest or unrest", "megaphone-fill", ["protest", "protests", "protesters", "protestors", "demonstrators", "demonstration", "rally", "riot", "riots", "clashes", "unrest"]),
        new(SituationKinds.Conflict, "attack", "Attack", "exclamation-octagon-fill", ["shooting", "gunmen", "gunman", "bombing", "suicide bomber", "terror", "terrorist", "hostage", "hostages", "stabbing", "massacre"]),
        new(SituationKinds.Conflict, "fighting", "Fighting", "crosshair", ["troops", "fighting", "offensive", "front line", "frontline", "battle", "invasion"]),

        new(SituationKinds.Health, "outbreak", "Outbreak", "virus", ["outbreak", "outbreaks", "virus", "ebola", "cholera", "measles", "mpox", "epidemic", "pandemic", "dengue", "bird flu", "influenza", "cases"]),
        new(SituationKinds.Health, "hospitals", "Hospitals", "hospital", ["hospital", "hospitals", "patients", "a&e"]),

        new(SituationKinds.Politics, "election", "Election", "check2-square", ["election", "elections", "vote", "votes", "voters", "ballot", "referendum", "polls"]),

        new(SituationKinds.Business, "energy", "Energy", "fuel-pump-fill", ["oil", "gas", "fuel", "opec", "petrol", "crude", "pipeline"]),
        new(SituationKinds.Business, "markets-down", "Markets falling", "graph-down-arrow", ["fall", "falls", "fell", "plunge", "plunges", "slump", "slumps", "tumble", "tumbles", "sell-off", "selloff", "losses"]),
        new(SituationKinds.Business, "markets-up", "Markets rising", "graph-up-arrow", ["rise", "rises", "rose", "surge", "surges", "soar", "soars", "rally", "rallies", "record high", "gains"]),
    ];

    public static string KindIcon(string? kind) => kind switch
    {
        SituationKinds.Disaster => "exclamation-triangle-fill",
        SituationKinds.Transport => "signpost-split",
        SituationKinds.Cyber => "shield-lock-fill",
        SituationKinds.Conflict => "crosshair",
        SituationKinds.Health => "heart-pulse-fill",
        SituationKinds.Politics => "bank2",
        SituationKinds.Business => "graph-up-arrow",
        SituationKinds.Sport => "trophy-fill",
        _ => "newspaper"
    };

    // Every icon a tag can name, for a check that each is in the loaded icon set.
    public static IReadOnlyCollection<string> Icons =>
        All.Select(subtype => subtype.Icon).Concat(SituationKinds.All.Select(KindIcon)).ToHashSet(StringComparer.Ordinal);

    // The subtypes a kind has, for SentinelGPT to choose from.
    public static IReadOnlyList<(string Id, string Label)> Choices(string? kind) =>
        All.Where(subtype => subtype.Kind == kind).Select(subtype => (subtype.Id, subtype.Label)).ToList();

    // The tag for a subtype SentinelGPT chose; null when the id isn't one of the kind's.
    public static SituationTag? FromModel(string kind, string subtypeId) =>
        All.FirstOrDefault(subtype => subtype.Kind == kind && subtype.Id == subtypeId) is { } chosen
            ? new SituationTag(kind, chosen.Id, chosen.Label, chosen.Icon, SituationTag.Model)
            : null;

    public static SituationTag Tag(string? kind, params string?[] texts)
    {
        var resolved = kind ?? SituationKinds.Other;
        var text = " " + string.Join(' ', Word().Matches(string.Join(' ', texts.Where(t => !string.IsNullOrWhiteSpace(t))).ToLowerInvariant())
            .Select(match => match.Value)) + " ";
        var best = All
            .Where(subtype => subtype.Kind == resolved)
            .Select((subtype, order) => (Subtype: subtype, Order: order, Hits: subtype.Words.Count(word => text.Contains(" " + word + " ", StringComparison.Ordinal))))
            .Where(candidate => candidate.Hits > 0)
            .OrderByDescending(candidate => candidate.Hits)
            .ThenBy(candidate => candidate.Order)
            .FirstOrDefault();
        return best.Subtype is null
            ? new SituationTag(resolved, null, SituationKinds.Label(resolved), KindIcon(resolved), SituationTag.Low)
            : new SituationTag(resolved, best.Subtype.Id, best.Subtype.Label, best.Subtype.Icon, best.Hits >= 2 ? SituationTag.High : SituationTag.Medium);
    }

    [GeneratedRegex(@"[\p{L}\p{N}&-]+")]
    private static partial Regex Word();
}
