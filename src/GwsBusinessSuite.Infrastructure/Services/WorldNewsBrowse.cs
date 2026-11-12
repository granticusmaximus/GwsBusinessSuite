using System.Globalization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// The World News page's three tabs, in the reader's words (12-11-26). Reports is the archive the
// search box opens, not a tab.
public enum WorldNewsTab { Today, Developing, Latest, Reports }

// What a list row shows beside its headline: the kind of story, where the headlines place it,
// and how widely it's covered.
public sealed record WorldNewsRowFacts(string Kind, string KindLabel, string? Place, string? PlaceCountry);

// Everything the World News page keeps in its address, so Back, a reload, the Mac app reopening
// and a shared link all land on the same view. Empty strings mean "not set".
public sealed record WorldNewsBrowseState(
    WorldNewsTab Tab = WorldNewsTab.Today,
    string Query = "",
    int RangeHours = 0,
    string Kind = "",
    string Where = "",
    string Country = "",
    string Language = "",
    string Outlet = "",
    string? EventId = null,
    string? SituationId = null);

// The World News page's presentation rules, kept out of the component so they can be tested.
public static class WorldNewsBrowse
{
    public const int TodayRangeHours = 3;
    public const int DefaultRangeHours = 24;

    public static string TabKey(WorldNewsTab tab) => tab switch
    {
        WorldNewsTab.Developing => "developing",
        WorldNewsTab.Latest => "latest",
        WorldNewsTab.Reports => "reports",
        _ => "today"
    };

    public static string TabLabel(WorldNewsTab tab) => tab switch
    {
        WorldNewsTab.Developing => "Developing",
        WorldNewsTab.Latest => "Latest",
        WorldNewsTab.Reports => "Reports",
        _ => "Today"
    };

    // Old links still work: ?situation= opens Developing, ?event= opens Latest. "events",
    // "situations" and "world" are the tab names before 12-11-26.
    public static WorldNewsTab ParseTab(string? key, string? eventId = null, string? situationId = null) =>
        key?.Trim().ToLowerInvariant() switch
        {
            "developing" or "situations" => WorldNewsTab.Developing,
            "latest" or "events" => WorldNewsTab.Latest,
            "reports" => WorldNewsTab.Reports,
            "today" or "world" => WorldNewsTab.Today,
            _ when !string.IsNullOrWhiteSpace(situationId) => WorldNewsTab.Developing,
            _ when !string.IsNullOrWhiteSpace(eventId) => WorldNewsTab.Latest,
            _ => WorldNewsTab.Today
        };

    // The ranges each tab offers. Today is "right now"; the archive reaches back a month.
    public static IReadOnlyList<(int Hours, string Label)> Ranges(WorldNewsTab tab) => tab switch
    {
        WorldNewsTab.Today => [(3, "Last 3 hours"), (24, "Last 24 hours")],
        WorldNewsTab.Reports => [(24, "Last 24 hours"), (72, "Last 3 days"), (168, "Last 7 days"), (720, "Last 30 days")],
        _ => [(24, "Last 24 hours"), (72, "Last 3 days"), (168, "Last 7 days")]
    };

    // A range the tab offers: the asked-for one, else the tab's default.
    public static int RangeFor(WorldNewsTab tab, int hours)
    {
        var ranges = Ranges(tab);
        if (ranges.Any(range => range.Hours == hours)) return hours;
        return tab == WorldNewsTab.Today ? TodayRangeHours : DefaultRangeHours;
    }

    // The page's address for a state: only what differs from the defaults, in a fixed order.
    public static string Url(WorldNewsBrowseState state)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
        if (state.Tab != WorldNewsTab.Today) Add("view", TabKey(state.Tab));
        Add("q", state.Query);
        var range = RangeFor(state.Tab, state.RangeHours);
        if (range != RangeFor(state.Tab, 0)) Add("range", range.ToString(CultureInfo.InvariantCulture));
        Add("kind", state.Kind);
        Add("where", state.Where);
        Add("country", state.Country);
        Add("language", state.Language);
        Add("outlet", state.Outlet);
        Add("event", state.EventId);
        Add("situation", state.SituationId);
        return parts.Count == 0 ? "admin/world-news" : "admin/world-news?" + string.Join('&', parts);
    }

    // One word per chip, so the row of kinds fits on one line; the story itself keeps the full label.
    public static string ChipLabel(string kind) => kind switch
    {
        SituationKinds.Disaster => "Disaster",
        SituationKinds.Transport => "Transport",
        SituationKinds.Cyber => "Cyber",
        SituationKinds.Conflict => "Conflict",
        SituationKinds.Health => "Health",
        SituationKinds.Politics => "Politics",
        SituationKinds.Business => "Business",
        SituationKinds.Sport => "Sport",
        _ => SituationKinds.Label(kind)
    };

    public static WorldNewsRowFacts Facts(EventOverview e) =>
        Facts(SituationKinds.FromWords($"{e.Headline} {e.Title}"), WorldNewsMapService.Locate(e.Headline ?? e.Title, e.Title));

    public static WorldNewsRowFacts Facts(SituationOverview s) =>
        Facts(WorldNewsMapService.KindOf(s), WorldNewsMapService.Named(s));

    private static WorldNewsRowFacts Facts(string kind, WorldNewsLocation? place) =>
        new(kind, SituationKinds.Label(kind), place?.Label, place?.Country);

    // Today's times as "14:05"; anything earlier in full, "11-11-26 14:05" (the app's date rule).
    public static string TimeLabel(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        return local.Date == today.Date ? local.ToGwsTime() : local.ToGwsDateTime();
    }

    // "Seen by 14 outlets in 6 languages", the row's one line of numbers.
    public static string Seen(int outlets, int languages) =>
        languages > 1
            ? $"Seen by {Count(outlets, "outlet")} in {languages:N0} languages"
            : $"Seen by {Count(outlets, "outlet")}";

    // How wide the coverage bar is, against the busiest row in view: a square-root scale, so a
    // 4-outlet story still shows next to a 60-outlet one, and never less than a sliver.
    public static int ReachPercent(int outlets, int busiest)
    {
        if (busiest <= 0 || outlets <= 0) return 6;
        var share = Math.Sqrt(Math.Min(outlets, busiest) / (double)busiest);
        return Math.Clamp((int)Math.Round(share * 100), 6, 100);
    }

    public static bool Contains(string? text, string query) =>
        string.IsNullOrWhiteSpace(query) || text?.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) == true;

    // An event or situation passes the page's filters: words, kind, where it happened, and the
    // source country and language of its coverage.
    public static bool Matches(string title, string? other, WorldNewsRowFacts facts, IReadOnlyList<string> countries,
        IReadOnlyList<string> languages, WorldNewsBrowseState state) =>
        (Contains(title, state.Query) || Contains(other, state.Query))
        && (state.Kind.Length == 0 || facts.Kind == state.Kind)
        && (state.Where.Length == 0 || string.Equals(facts.PlaceCountry, state.Where, StringComparison.OrdinalIgnoreCase))
        && (state.Country.Length == 0 || countries.Any(c => string.Equals(c, state.Country.Trim(), StringComparison.OrdinalIgnoreCase)))
        && (state.Language.Length == 0 || languages.Any(l => string.Equals(l, state.Language.Trim(), StringComparison.OrdinalIgnoreCase)));

    public static string Count(int n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";
}
