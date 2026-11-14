namespace GwsBusinessSuite.WorldNewsKit;

// #7 (13-11-26): one story's chronology: the first report, each wave of outlets joining (grouped by
// hour so a busy story stays readable), official statements and fact-checks, every change of its
// label (from the tamper-evident ledger), headline corrections and articles changed after
// publication, and official records near it. Oldest first.
public sealed record TimelineEntry(DateTimeOffset At, string Kind, string Text, string? Url = null);

public static class StoryTimeline
{
    public const string Report = "report";
    public const string Outlets = "outlets";
    public const string Official = "official";
    public const string FactCheck = "fact-check";
    public const string Label = "label";
    public const string Edit = "edit";
    public const string Record = "record";
    public const int MaxNames = 5;

    public static IReadOnlyList<TimelineEntry> Build(
        IReadOnlyList<WorldArticle> reports,
        IReadOnlyList<LabelSnapshot>? labels = null,
        IReadOnlyList<ArticleEdit>? headlineEdits = null,
        IReadOnlyList<(WorldArticle Report, DateTimeOffset At)>? bodyEdits = null,
        IReadOnlyList<(DateTimeOffset At, string Source, string Title, string Url)>? records = null)
    {
        var entries = new List<TimelineEntry>();
        var ordered = reports.OrderBy(r => r.SeenAt).ToList();
        var news = ordered.Where(r => !OfficialStatements.IsOfficial(r.Outlet) && !FactChecks.IsFactChecker(r.Outlet)).ToList();
        if (news.FirstOrDefault() is { } first)
            entries.Add(new(first.SeenAt, Report, $"First report: {first.Outlet}, \"{first.Title}\"", first.Url));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (news.FirstOrDefault() is { } f) seen.Add(f.Outlet);
        foreach (var hour in news.Skip(1).GroupBy(r => new DateTimeOffset(r.SeenAt.UtcDateTime.Date.AddHours(r.SeenAt.UtcDateTime.Hour), TimeSpan.Zero)))
        {
            var joined = hour.Select(r => r.Outlet).Where(seen.Add).ToList();
            if (joined.Count == 0) continue;
            var names = string.Join(", ", joined.Take(MaxNames)) + (joined.Count > MaxNames ? $" and {joined.Count - MaxNames} more" : string.Empty);
            entries.Add(new(hour.Min(r => r.SeenAt), Outlets, joined.Count == 1 ? $"{names} reported it" : $"{joined.Count} more outlets: {names}"));
        }
        foreach (var r in ordered.Where(r => OfficialStatements.IsOfficial(r.Outlet)))
            entries.Add(new(r.SeenAt, Official, $"Official statement, {r.Outlet}: \"{r.Title}\"", r.Url));
        foreach (var r in ordered.Where(r => FactChecks.IsFactChecker(r.Outlet)))
            entries.Add(new(r.SeenAt, FactCheck, $"Fact-check, {r.Outlet}" + (FactChecks.VerdictOf(r.Title) is { } verdict ? $" ({verdict})" : string.Empty) + $": \"{r.Title}\"", r.Url));
        string? level = null;
        foreach (var snapshot in (labels ?? []).OrderBy(s => s.TakenAt))
        {
            if (snapshot.Level == level) continue;
            entries.Add(new(snapshot.TakenAt, Label, level is null
                ? $"Labelled {LevelText(snapshot.Level)}: {snapshot.Reason}"
                : $"Label changed from {LevelText(level)} to {LevelText(snapshot.Level)}: {snapshot.Reason}"));
            level = snapshot.Level;
        }
        var byId = reports.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var edit in headlineEdits ?? [])
        {
            var outlet = byId.TryGetValue(edit.ArticleId, out var article) ? article.Outlet : "An outlet";
            var what = edit.Kind switch { "correction" => "corrected its headline", "update" => "updated its headline", _ => "changed its headline" };
            entries.Add(new(edit.SeenAt, Edit, $"{outlet} {what}: \"{edit.Before}\" → \"{edit.After}\"", article?.Url));
        }
        foreach (var (report, at) in bodyEdits ?? [])
            entries.Add(new(at, Edit, $"{report.Outlet} changed its article after publication: \"{report.Title}\"", report.Url));
        foreach (var (at, source, title, url) in records ?? [])
            entries.Add(new(at, Record, $"{source}: {title}", url.StartsWith("http", StringComparison.Ordinal) ? url : null));
        return entries.OrderBy(e => e.At).ThenBy(e => e.Kind == Report ? 0 : 1).ToList();
    }

    public static string LevelText(string level) => level == nameof(StoryConfidenceLevel.SingleSource) ? "Single source" : level;
}
