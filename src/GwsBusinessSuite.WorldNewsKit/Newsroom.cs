using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// World News trust plan, Phase B first slice (13-11-26): the evidence board (B1), sensor checks of
// physical claims (B2), and the newsroom: verification checklist (N1), story budget (N3), tips (N4).

// B1: something an official record or a sensor saw near a story's place, with how far away.
public sealed record EvidenceSignal(string Source, string Kind, string Title, double Latitude, double Longitude, DateTimeOffset? At, string Url, double Kilometres);

public sealed record EvidenceBoard(IReadOnlyList<EvidenceSignal> Signals, IReadOnlyList<SensorCheck> Checks, string Place);

public static class EvidenceBoards
{
    public const double CityKilometres = 300;
    public const double CountryKilometres = 1500;
    public const int MaxSignals = 40;

    // Signals within reach of the story's place, seen from two days before its first report until
    // now (or undated, for current conditions such as an outage), nearest first in time order.
    public static IReadOnlyList<EvidenceSignal> Near(IEnumerable<PrimarySignal> signals, double latitude, double longitude, DateTimeOffset firstSeen,
        DateTimeOffset now, double maxKilometres) =>
        signals
            .Where(signal => signal.ObservedAt is not { } at || (at >= firstSeen - PrimarySources.MaxGap && at <= now))
            .Select(signal => new EvidenceSignal(signal.Source, signal.Kind, signal.Title, signal.Latitude, signal.Longitude, signal.ObservedAt, signal.Url,
                PrimarySources.Kilometres(latitude, longitude, signal.Latitude, signal.Longitude)))
            .Where(signal => signal.Kilometres <= maxKilometres)
            .OrderBy(signal => signal.At ?? now).ThenBy(signal => signal.Kilometres)
            .Take(MaxSignals)
            .ToList();
}

public enum SensorVerdict
{
    Supported,
    NoSignal,
}

// B2: a physical claim in the headline and what the sensors near it show. Kinds match the
// sensors' HazardKinds exactly, and only claims some sensor can see are checked (none reports power
// cuts, so those aren't checked). "No signal" is not a
// contradiction: most sensors don't cover every place, so silence proves nothing.
public sealed record SensorCheck(string Claim, SensorVerdict Verdict, string Detail);

public static partial class SensorChecks
{
    private static readonly (string Claim, Regex Pattern, string[] Kinds, string Sensor)[] Claims =
    [
        ("Internet shutdown", InternetCut(), ["internet"], "IODA internet measurements"),
        ("Airport disruption", AirportCut(), ["airport"], "FAA airport status"),
        ("Fire", Fire(), ["hotspot", "wildfire"], "NASA FIRMS fire hotspots and wildfire incidents"),
        ("Earthquake", Quake(), ["earthquake"], "USGS earthquakes"),
        ("Flood", Flood(), ["flood", "river"], "GDACS floods and river gauges"),
        ("Storm", Storm(), ["cyclone"], "GDACS and NOAA storms"),
    ];

    // The signals worth showing on a story's board: those a claim in its headline is about, or,
    // with no physical claim, everything but satellite fire hotspots (detected every day across most
    // of the world, so they'd bury an election or a court story in noise).
    public static IReadOnlyList<EvidenceSignal> Relevant(string headline, IReadOnlyList<EvidenceSignal> nearby)
    {
        var kinds = Claims.Where(claim => claim.Pattern.IsMatch(headline)).SelectMany(claim => claim.Kinds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return kinds.Count > 0
            ? nearby.Where(signal => kinds.Contains(signal.Kind)).ToList()
            : nearby.Where(signal => !signal.Kind.Equals("hotspot", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static IReadOnlyList<SensorCheck> Check(string headline, IReadOnlyList<EvidenceSignal> nearby)
    {
        var checks = new List<SensorCheck>();
        foreach (var (claim, pattern, kinds, sensor) in Claims)
        {
            if (!pattern.IsMatch(headline)) continue;
            var match = nearby.Where(signal => kinds.Contains(signal.Kind, StringComparer.OrdinalIgnoreCase))
                .OrderBy(signal => signal.Kilometres).FirstOrDefault();
            checks.Add(match is not null
                ? new SensorCheck(claim, SensorVerdict.Supported, $"{match.Source}: {match.Title} ({match.Kilometres:0} km away)")
                : new SensorCheck(claim, SensorVerdict.NoSignal, $"No signal from {sensor} near here. That doesn't rule it out: coverage is partial."));
        }
        return checks;
    }

    [GeneratedRegex(@"\b(internet (shutdown|outage|blackout|cut)s?|internet (was |is )?(cut|down)|connectivity (loss|outage))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternetCut();

    [GeneratedRegex(@"\b(airports? (closed|shut|closes|suspends?)|flights? (cancelled|canceled|grounded|suspended|halted)|ground stop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AirportCut();

    [GeneratedRegex(@"\b(wildfires?|bushfires?|forest fires?|blaze)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Fire();

    [GeneratedRegex(@"\b(earthquakes?|quakes?|tremors?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Quake();

    [GeneratedRegex(@"\b(floods?|flooding)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Flood();

    [GeneratedRegex(@"\b(cyclones?|hurricanes?|typhoons?|tropical storms?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Storm();
}

// N1: the desk's verification checklist. Each item is ticked by a person with the evidence they
// used; Suggest offers evidence the system already has, but never ticks anything itself.
public sealed record DeskCheck(string EventId, string Item, bool Done, string? Evidence, string? CheckedBy, DateTimeOffset? CheckedAt);

public static class DeskChecklist
{
    public static readonly IReadOnlyList<(string Key, string Label, string Help)> Items =
    [
        ("origin", "Origin found", "Who first reported it, and did they see it themselves?"),
        ("second-source", "Second independent source", "Someone who didn't take it from the first source."),
        ("official-record", "Official record or statement", "A government, agency or sensor record of the event."),
        ("location", "Place confirmed", "Where it happened, checked against the reports and the map."),
        ("media", "Photos and video checked", "Not recycled, not from somewhere else, not edited."),
        ("numbers", "Numbers reconciled", "Casualty and other figures agree, or the range is stated."),
    ];

    public static bool IsComplete(IEnumerable<DeskCheck> checks) =>
        Items.All(item => checks.Any(check => check.Item == item.Key && check.Done && !string.IsNullOrWhiteSpace(check.Evidence)));

    // Evidence the system already has for an item, offered to the person ticking it.
    public static string? Suggest(string item, StoryTrustSummary? trust, EvidenceBoard? board, WorldArticle? firstReport) => item switch
    {
        "origin" when firstReport is not null => $"First report: {firstReport.Outlet}, {firstReport.Url}",
        "second-source" when trust is { Independence.Independent: >= 2 } t => $"{t.Independence.Independent} independent sources: {string.Join(", ", t.Independence.Origins.Take(4).Select(o => o.Name))}",
        "official-record" when trust?.PrimarySource is { } primary => primary,
        "official-record" when board?.Signals.FirstOrDefault() is { } signal => $"{signal.Source}: {signal.Title}",
        "location" when board is { Place.Length: > 0 } b => b.Place,
        _ => null,
    };
}

// N3: a story on the desk's budget: who has it, where it stands and what's next.
public sealed record BudgetItem(string EventId, string Title, string Owner, string Status, string NextAction, DateTimeOffset UpdatedAt, string UpdatedBy);

public static class BudgetStatuses
{
    public static readonly IReadOnlyList<string> All = ["watching", "verifying", "ready", "published"];
}

// N4: a tip from a reader or contact. Links only (no files), so nothing harmful is stored or served.
public sealed record NewsTip(string Id, string? Name, string? Contact, string Message, string? Url, DateTimeOffset CreatedAt, string Status, string? EventId, string? HandledBy);

public static class TipStatuses
{
    public const string New = "new";
    public const string Following = "following";
    public const string Closed = "closed";
}

public static class TipRules
{
    public const int MaxMessage = 4000;
    public const int MaxName = 120;
    public const int MaxContact = 200;
    public const int MaxUrl = 500;

    // Null when the tip can be kept; otherwise why not.
    // The story a tip is matched to: a bare event id, or a World News link with ?event=<id>.
    public static string? StoryIdFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var at = text.IndexOf("event=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text;
        var id = text[(at + 6)..];
        var end = id.IndexOfAny(['&', '#']);
        id = Uri.UnescapeDataString(end < 0 ? id : id[..end]);
        return id.Length == 0 ? null : id;
    }

    public static string? Problem(string? message, string? url)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Trim().Length < 10) return "Tell us a little more (at least a sentence).";
        if (message.Length > MaxMessage) return $"Please keep it under {MaxMessage} characters.";
        if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || url.Length > MaxUrl))
            return "The link needs to start with https://";
        return null;
    }
}

// N2: a verified story as a draft article: the confirmed facts, the figures and how sure they are,
// the sources with links, how it was verified (label, independent sources, record, the desk's
// checklist with its evidence) and what isn't known yet. AI-written text is marked as such.
public static class StoryDraft
{
    public static string Markdown(StoryDocument story, IReadOnlyList<DeskCheck> checks, EvidenceBoard? board, Func<DateTimeOffset, string> formatTime)
    {
        var md = new System.Text.StringBuilder();
        var e = story.Event;
        if (story.Analysis is { } analysis)
        {
            md.AppendLine(analysis.Summary.Trim()).AppendLine();
            md.AppendLine($"*Summary drafted by AI ({analysis.Model}) from {analysis.ArticleCount} reports; every point below links to its source.*").AppendLine();
            if (analysis.Agreed.Count > 0)
            {
                md.AppendLine("## What's confirmed").AppendLine();
                foreach (var fact in analysis.Agreed) md.AppendLine($"- {fact.Text}{(fact.Outlets.Count > 0 ? $" ({string.Join(", ", fact.Outlets)})" : string.Empty)}");
                md.AppendLine();
            }
        }
        else if (story.Summary is { } summary)
        {
            md.AppendLine(summary.Text.Trim()).AppendLine();
            md.AppendLine($"*Summary drafted by AI ({summary.Model}) from {summary.ArticleCount} reports.*").AppendLine();
        }

        var figures = story.Figures.Where(f => f.Values.Count > 0).ToList();
        if (figures.Count > 0)
        {
            md.AppendLine("## The figures").AppendLine();
            foreach (var figure in figures)
            {
                var values = string.Join("; ", figure.Values.Take(3).Select(v => $"{EventDocument.ValueText(v)} ({string.Join(", ", v.Sources.Take(3).Select(s => s.Article.Outlet))})"));
                md.AppendLine($"- **{figure.Label}:** {values}{(figure.Disputed ? " (reports disagree)" : figure.Consensus ? " (agreed)" : string.Empty)}");
            }
            md.AppendLine();
        }

        md.AppendLine("## How we verified this").AppendLine();
        if (story.Trust is { } trust)
        {
            md.AppendLine($"- **{trust.Confidence.Label}:** {trust.Confidence.Reason}");
            md.AppendLine($"- {trust.Independence.Outlets} outlets, {trust.Independence.Independent} independent sources: {string.Join(", ", trust.Independence.Origins.Take(8).Select(o => o.Name))}");
            if (trust.PrimarySource is { } primary) md.AppendLine($"- Official record: {primary}");
            foreach (var check in trust.FactChecks ?? []) md.AppendLine($"- Fact-checked by {check.Outlet}{(check.Verdict is null ? string.Empty : $" ({check.Verdict})")}: [{check.Title}]({check.Url})");
        }
        foreach (var check in board?.Checks ?? []) md.AppendLine($"- {check.Claim}: {(check.Verdict == SensorVerdict.Supported ? "supported" : "no sensor signal")}. {check.Detail}");
        foreach (var (key, label, _) in DeskChecklist.Items)
        {
            var check = checks.FirstOrDefault(c => c.Item == key);
            md.AppendLine(check is { Done: true }
                ? $"- [x] {label}: {check.Evidence} ({check.CheckedBy}{(check.CheckedAt is { } at ? ", " + formatTime(at) : string.Empty)})"
                : $"- [ ] {label}: not checked yet");
        }
        md.AppendLine();

        var unknowns = figures.Where(f => f.Disputed).Select(f => $"- {f.Label}: reports give {string.Join(", ", f.Values.Take(3).Select(EventDocument.ValueText))}").ToList();
        if (story.Trust?.Confidence.Level is StoryConfidenceLevel.SingleSource) unknowns.Add("- The story rests on a single source so far.");
        if (unknowns.Count > 0)
        {
            md.AppendLine("## What we don't know yet").AppendLine();
            foreach (var line in unknowns) md.AppendLine(line);
            md.AppendLine();
        }

        md.AppendLine("## Sources").AppendLine();
        foreach (var source in story.Sources.Take(25))
            md.AppendLine($"- [{source.Report.Outlet}: {source.Report.Title}]({source.Report.Url}), {formatTime(source.Report.SeenAt)}");
        md.AppendLine();
        md.AppendLine($"*First reported by {story.FirstReport?.Outlet ?? "unknown"}, {formatTime(e.FirstSeen)}. Rules {story.Trust?.RulesVersion ?? StoryConfidenceRules.RulesVersion}.*");
        return md.ToString();
    }
}
