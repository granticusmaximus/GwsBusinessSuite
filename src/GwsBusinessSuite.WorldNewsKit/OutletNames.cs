namespace GwsBusinessSuite.WorldNewsKit;

// One name per outlet, whichever feed a report came through (07-11-26, when the US outlets' own
// feeds were added). Google News names some outlets by their site's title ("ABC News - Breaking
// News, Latest News and Videos") or a short form ("WSJ"), so their reports didn't group with the
// outlet's own feed in a story's coverage. It also tells apart two outlets that share a name.
public static class OutletNames
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ABC News - Breaking News, Latest News and Videos"] = "ABC News (US)",
        ["ABC News & Headlines – Australian Broadcasting Corporation"] = "ABC News (Australia)",
        ["WSJ"] = "The Wall Street Journal",
        ["Bloomberg.com"] = "Bloomberg",
        ["POLITICO"] = "Politico",
        ["PBS"] = "PBS News",
        ["PBS NewsHour"] = "PBS News",
        ["CSMonitor.com"] = "The Christian Science Monitor",
        ["africanews"] = "Africanews",
    };

    // State-run outlets: government agencies, ruling-party papers and state-controlled broadcasters,
    // the usual meaning of "state media". Public broadcasters with independent editors (BBC, NHK,
    // DW, ABC) aren't. Written as each outlet appears in the feeds, native spellings included.
    private static readonly HashSet<string> StateRun = new(StringComparer.OrdinalIgnoreCase)
    {
        // Russia
        "TASS", "RIA Novosti", "РИА Новости", "RT", "RT en Español", "Sputnik", "Sputnik Mundo",
        // China
        "Xinhua", "Xinhua News Agency", "新华网", "新华社", "People's Daily", "人民网", "人民日报", "CGTN", "CCTV",
        "央视新闻", "China Daily", "Global Times", "环球时报",
        // Iran, North Korea, Syria
        "IRNA", "Press TV", "KCNA", "SANA",
        // Turkey
        "Anadolu Agency", "Anadolu Ajansı", "TRT World", "TRT Haber",
        // Belarus, Venezuela, Cuba
        "BelTA", "teleSUR", "Prensa Latina", "Granma",
    };

    public static string Canonical(string outlet) =>
        Aliases.TryGetValue(outlet.Trim(), out var name) ? name : outlet;

    public static bool IsStateMedia(string? outlet) => outlet is not null && StateRun.Contains(outlet.Trim());
}
