namespace GwsBusinessSuite.WorldNewsKit;

// B6 (13-11-26): the test set. Stories with a known right answer, run through exactly the code that
// labels a live story (StoryLabel.For and the alert gate). Each case is modelled on a pattern that
// has fooled newsrooms before: one wire story repeated everywhere, a state-media chorus, a rumour
// that never got a second source, a fact-checked hoax, conflicting death tolls. The red-team cases
// try to game the rules. A rules change that breaks any case fails the build (WorldNewsGoldSetTests),
// and the methodology page shows the result under the current rules version.
public sealed record GoldCase(
    string Name,
    string Why,
    IReadOnlyList<WorldArticle> Reports,
    StoryConfidenceLevel Expected,
    bool ExpectedAlert,
    int? ExpectedIndependent = null,
    bool RedTeam = false,
    TimeSpan? Age = null);

public sealed record GoldResult(GoldCase Case, StoryConfidence Got, int Independent, bool Alerted)
{
    public bool Passed => Got.Level == Case.Expected && Alerted == Case.ExpectedAlert
        && (Case.ExpectedIndependent is not { } expected || expected == Independent);
}

public static class GoldSet
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 6, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<GoldResult> Run(IEnumerable<GoldCase>? cases = null) =>
        (cases ?? Cases).Select(Run).ToList();

    public static GoldResult Run(GoldCase gold)
    {
        var first = gold.Reports.Min(report => report.SeenAt);
        var now = first + (gold.Age ?? TimeSpan.FromHours(2));
        var label = StoryLabel.For(gold.Reports, first, now, out var independence);
        var alerted = StoryConfidenceRules.MayAlert(independence, PrimarySources.InReports(gold.Reports));
        return new GoldResult(gold, label, independence.Independent, alerted);
    }

    private static WorldArticle R(string outlet, string title, string? snippet = null, int minutes = 0, string? url = null, string language = "English") =>
        new($"{outlet}-{minutes}-{title.GetHashCode(StringComparison.Ordinal):x}", url ?? $"https://{Slug(outlet)}.example/{minutes}",
            title, outlet, language, "International", Start.AddMinutes(minutes), "gold", null, snippet);

    private static string Slug(string outlet) => new(outlet.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private const string WireCopy = "The central bank raised its benchmark rate by half a point on Monday, its largest increase in two decades, citing persistent inflation and a weaker currency.";

    public static IReadOnlyList<GoldCase> Cases { get; } =
    [
        new("One wire story, eight outlets",
            "Eight sites ran the same Reuters copy. That is one source however many logos carry it.",
            [.. new[] { "The Hindu", "Japan Times", "Arab News", "Gulf News", "Straits Times", "Dawn", "Daily Sabah", "Bangkok Post" }
                .Select((outlet, i) => R(outlet, "Central bank makes biggest rate rise in 20 years", "(Reuters) - " + WireCopy, i * 7))],
            StoryConfidenceLevel.SingleSource, false, 1),
        new("Three newsrooms, own reporting",
            "Three outlets with different owners each reported it themselves.",
            [
                R("BBC News", "Bridge collapse closes main river crossing", "Engineers say the span gave way after overnight flooding; no one was on it at the time."),
                R("The Guardian", "River crossing shut after bridge partly collapses", "The council said inspections had been due next month.", 20),
                R("Al Jazeera", "Bridge collapses after heavy rain, officials say", "Residents described hearing a loud crack before dawn.", 45),
            ],
            StoryConfidenceLevel.Confirmed, true, 3),
        new("Two newsrooms",
            "Two independent outlets: reported, not yet confirmed.",
            [
                R("BBC News", "Minister resigns over procurement row", "The minister said she had done nothing wrong."),
                R("Der Spiegel", "Minister steps down amid contracts dispute", "Opposition parties had called for an inquiry.", 30),
            ],
            StoryConfidenceLevel.Reported, true, 2),
        new("Sister channels",
            "NBC News, MSNBC and CNBC share an owner, so they are one source.",
            [
                R("NBC News", "Retail chain to close 300 stores", "The company cited falling foot traffic."),
                R("MSNBC", "Retailer announces 300 store closures", "Shares fell in early trading.", 10),
                R("CNBC", "Chain shutting 300 locations as sales slide", "Analysts had expected cuts.", 15),
            ],
            StoryConfidenceLevel.SingleSource, false, 1),
        new("State-media chorus",
            "TASS, RIA Novosti and RT all answer to the same state: one source.",
            [
                R("TASS", "Air defences repel drone attack on border region", "The governor said there were no casualties."),
                R("RIA Novosti", "Drones shot down over border region, governor says", "Debris damaged a warehouse.", 5),
                R("RT", "Border region drone attack thwarted", "Officials said all drones were destroyed.", 12),
            ],
            StoryConfidenceLevel.SingleSource, false, 1),
        new("Rumour with no second source",
            "A celebrity-death rumour on one site, still alone after 13 hours: unconfirmed.",
            [R("Daily Star", "Veteran rock star dies aged 78, reports say", "Fans paid tribute online.")],
            StoryConfidenceLevel.SingleSource, false, 1, Age: TimeSpan.FromHours(13)),
        new("Fact-checked hoax",
            "Widely shared, then rated false by a fact-checker: disputed, whatever the coverage.",
            [
                R("Daily Mail", "Viral video shows shark swimming on flooded motorway", "The clip has been shared millions of times."),
                R("The Sun", "Shark spotted on flooded highway in storm video", "Drivers said they could not believe their eyes.", 15),
                R("New York Post", "Flood video appears to show shark on road", "The footage spread quickly.", 30),
                R("Snopes", "Fact Check: Shark on flooded highway video is fake", "The image is an old digital composite.", 240),
            ],
            StoryConfidenceLevel.Disputed, true),
        new("Conflicting death tolls",
            "Outlets give 12 and 31 dead for the same event: disputed until it settles.",
            [
                R("BBC News", "12 dead after factory explosion", "Rescue teams are still searching."),
                R("Al Jazeera", "Factory blast kills 31", "Officials gave the toll at a briefing.", 20),
            ],
            StoryConfidenceLevel.Disputed, true),
        new("WHO notice",
            "WHO's own outbreak notice is an official record: confirmed on its own.",
            [R(WhoOutbreakNews.Outlet, "Disease Outbreak News: Marburg virus disease", "The ministry notified WHO of a confirmed case.")],
            StoryConfidenceLevel.Confirmed, true),

        new("Press releases padding a story",
            "Five newswire releases and one outlet: releases aren't news, so it's one source.",
            [
                .. Enumerable.Range(0, 5).Select(i => R("PR Newswire", $"Company unveils breakthrough battery {i}", "FOR IMMEDIATE RELEASE", i, $"https://www.prnewswire.com/news/{i}")),
                R("TechCrunch", "Startup claims battery breakthrough", "The company has not published data.", 60),
            ],
            StoryConfidenceLevel.SingleSource, false, 1, RedTeam: true),
        new("Official statements as sources",
            "A government's newsroom plus one outlet: an official statement isn't independent corroboration.",
            [
                R("The White House", "Statement on the ceasefire agreement", "The President welcomed the agreement."),
                R("European Commission", "Statement by the President on the ceasefire", "The EU stands ready to help.", 10),
                R("CNN", "Ceasefire agreed, officials say", "Details have not been published.", 30),
            ],
            StoryConfidenceLevel.SingleSource, false, 1, RedTeam: true),
        new("Wire copy with new headlines",
            "Outlets rewrote the headline but kept the agency's text: still one source.",
            [
                R("Gulf News", "Rates up sharply", WireCopy),
                R("Arab News", "Bank shocks markets with big hike", WireCopy, 5),
                R("Dawn", "Largest rate rise in decades", WireCopy, 9),
            ],
            StoryConfidenceLevel.SingleSource, false, 1, RedTeam: true),
        new("Attribution to the wire",
            "\"Told Reuters\" and \"AFP reported\" mean the report rests on the agency, not the outlet.",
            [
                R("Gulf News", "Port strike halts shipments", "A union official told Reuters the strike would continue."),
                R("Arab News", "Dockworkers walk out", "The strike began at dawn, Reuters reported.", 10),
                R("Straits Times", "Port strike spreads", "Two more terminals joined, AFP reported.", 20),
            ],
            StoryConfidenceLevel.Reported, true, 2, RedTeam: true),
        new("One outlet, many reports",
            "One outlet filing ten updates is still one source.",
            [.. Enumerable.Range(0, 10).Select(i => R("Sky News", $"Live: storm latest, update {i}", $"Update number {i} from our correspondent.", i * 20))],
            StoryConfidenceLevel.SingleSource, false, 1, RedTeam: true),
        new("Listicles and service pages",
            "SEO pages ride a story's keywords; they aren't reports.",
            [
                R("Example Daily", "Top 10 ways to stay safe in the heatwave"),
                R("Example Weekly", "How to watch the heatwave briefing live stream free", minutes: 5),
                R("BBC News", "Heatwave: record temperature recorded", "The Met Office confirmed the reading.", 10),
            ],
            StoryConfidenceLevel.SingleSource, false, 1, RedTeam: true),
        new("A fact-checker isn't a source",
            "A fact-check that confirms a claim doesn't add a source; one outlet is still one.",
            [
                R("Example Times", "Mayor announces free transit for students"),
                R("Snopes", "Did the mayor announce free transit for students?", "The city's site confirms the plan.", 60),
            ],
            StoryConfidenceLevel.SingleSource, false, 1, RedTeam: true),
    ];
}
