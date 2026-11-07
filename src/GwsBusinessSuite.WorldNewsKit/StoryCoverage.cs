namespace GwsBusinessSuite.WorldNewsKit;

// One report under an outlet in a story's coverage: the outlet's own (AlsoCarriedBy: other outlets
// that ran the same wire copy), or a wire copy it ran that another outlet published first
// (FirstPublishedBy).
public sealed record OutletReport(WorldArticle Report, IReadOnlyList<string> AlsoCarriedBy, string? FirstPublishedBy);

// Everything a story's coverage shows about one outlet (07-11-26, the story page reorganized: one
// place per outlet instead of four sections). States: which agreed facts its report states, in the
// grid's columns (StoryCoverage.GridNumbers), or null when SentinelGPT's comparison didn't name it.
// LeavesOut: the agreed facts' numbers it doesn't state.
public sealed record OutletCoverage(StoryOutlet Outlet, IReadOnlyList<bool>? States, IReadOnlyList<int> LeavesOut, string? Adds,
    IReadOnlyList<OutletReport> Reports);

public static class StoryCoverage
{
    // The numbers (as listed, from 1) of the agreed facts the grid shows: those that name outlets.
    public static IReadOnlyList<int> GridNumbers(StoryDocument story) =>
        story.Analysis?.Agreed.Select((fact, index) => (fact, index)).Where(x => x.fact.Outlets.Count > 0).Select(x => x.index + 1).ToList() ?? [];

    // Every outlet, in the story's order (most reports first), with its place in the comparison and
    // its reports, oldest first.
    public static IReadOnlyList<OutletCoverage> ByOutlet(StoryDocument story)
    {
        var rows = story.Grid.GroupBy(row => row.Outlet, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var comparisons = story.Comparisons.GroupBy(comparison => comparison.Outlet, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (fact, index) in (story.Analysis?.Agreed ?? []).Select((fact, index) => (fact, index))) numbers.TryAdd(fact.Text, index + 1);
        var own = story.Sources.ToLookup(source => source.Report.Outlet, StringComparer.OrdinalIgnoreCase);
        var carried = story.Sources.SelectMany(source => source.AlsoCarriedBy.Select(outlet => (Outlet: outlet, Source: source)))
            .ToLookup(pair => pair.Outlet, pair => pair.Source, StringComparer.OrdinalIgnoreCase);

        return story.Outlets.Select(outlet =>
        {
            rows.TryGetValue(outlet.Name, out var row);
            comparisons.TryGetValue(outlet.Name, out var comparison);
            var reports = own[outlet.Name].Select(source => new OutletReport(source.Report, source.AlsoCarriedBy, null))
                .Concat(carried[outlet.Name].Select(source => new OutletReport(source.Report, [], source.Report.Outlet)))
                .OrderBy(report => report.Report.SeenAt)
                .ToList();
            var leavesOut = comparison?.LeavesOut.Select(text => numbers.GetValueOrDefault(text)).Where(number => number > 0).Order().ToList() ?? [];
            return new OutletCoverage(outlet, row?.States, leavesOut, comparison?.Adds, reports);
        }).ToList();
    }

    // What SentinelGPT said about outlets it named that aren't among the story's (its own spelling of
    // a name), so nothing it wrote goes missing.
    public static IReadOnlyList<OutletComparison> Unmatched(StoryDocument story) =>
        story.Comparisons.Where(comparison => story.Outlet(comparison.Outlet) is null).ToList();

    // How many outlets had covered the story by the end of each slice of time from its first report to
    // its latest: how fast it spread.
    public static IReadOnlyList<double> Spread(StoryDocument story, int slices = 24)
    {
        if (story.Outlets.Count == 0 || slices < 2) return [];
        var start = story.Outlets.Min(outlet => outlet.FirstSeen);
        var end = story.Event.LastSeen > start ? story.Event.LastSeen : start.AddHours(1);
        var step = (end - start) / (slices - 1);
        return Enumerable.Range(0, slices)
            .Select(slice => (double)story.Outlets.Count(outlet => outlet.FirstSeen <= start + step * slice))
            .ToList();
    }
}
