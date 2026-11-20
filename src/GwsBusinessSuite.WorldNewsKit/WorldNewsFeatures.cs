using System.Globalization;
using System.Text;

namespace GwsBusinessSuite.WorldNewsKit;

// World News features (20-11-26, Grant: "Build ... 6 features"): coverage spread replay, catch me
// up, the morning briefing page and watchlist dossiers.

/// <summary>5. How a story spread: hour by hour, the outlets and countries that joined.</summary>
public sealed record SpreadHour(DateTimeOffset Hour, IReadOnlyList<string> NewOutlets, IReadOnlyList<string> NewCountries, int Outlets, int Countries);

public static class StorySpread
{
    public const int MaxHours = 72;

    // From the first report's hour; hours with nobody new are skipped, and the list stops after
    // MaxHours of hours with someone new.
    public static IReadOnlyList<SpreadHour> Hourly(IEnumerable<WorldArticle> articles)
    {
        var seenOutlets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hours = new List<SpreadHour>();
        foreach (var group in articles.OrderBy(a => a.SeenAt).GroupBy(a => Floor(a.SeenAt)))
        {
            var outlets = group.Select(a => a.Outlet).Where(o => !string.IsNullOrWhiteSpace(o) && seenOutlets.Add(o)).ToList();
            var countries = group.Select(a => a.Country).Where(c => !string.IsNullOrWhiteSpace(c) && seenCountries.Add(c)).ToList();
            if (outlets.Count == 0 && countries.Count == 0) continue;
            hours.Add(new SpreadHour(group.Key, outlets, countries, seenOutlets.Count, seenCountries.Count));
            if (hours.Count == MaxHours) break;
        }
        return hours;
    }

    public static DateTimeOffset Floor(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, at.UtcDateTime.Day, at.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
}

/// <summary>1. Catch me up: what the reports since someone last read the story add.</summary>
public static class CatchUp
{
    public const int MaxReports = 40;

    public const string SystemPrompt =
        "You catch a reader up on a news story. You get the story's headline and the reports that arrived since they last read it. " +
        "In 2 to 4 plain sentences, say what is new: developments, new figures, new claims and who reports them. " +
        "Use only these reports, name outlets as given, and say so plainly if the new reports add nothing.";

    public static string Prompt(string headline, DateTimeOffset since, IEnumerable<WorldArticle> newReports, Func<DateTimeOffset, string> formatTime)
    {
        var text = new StringBuilder($"Story: {headline}\nLast read: {formatTime(since)}\n\nNew reports:\n");
        foreach (var report in newReports.OrderBy(r => r.SeenAt).Take(MaxReports))
        {
            var title = report.English?.Headline ?? report.Title;
            var description = report.English?.Description ?? report.Snippet;
            text.Append("- ").Append(report.Outlet).Append(" (").Append(formatTime(report.SeenAt)).Append("): ").Append(title);
            if (!string.IsNullOrWhiteSpace(description)) text.Append(" - ").Append(description.Length > 300 ? description[..300] : description);
            text.Append('\n');
        }
        return text.ToString();
    }
}

/// <summary>2. The morning briefing, a personal Sentinel page each morning.</summary>
public static class MorningBriefing
{
    public const string PageSystemKey = "world-news-morning-briefing";
    public const string PageTitle = "Morning world briefing";
    public const int Hour = 7;
    public const int LeadStories = 8;

    // Due once a day from 07:00 local.
    public static bool Due(DateTimeOffset? lastBuilt, DateTimeOffset nowLocal)
    {
        var sevenToday = new DateTimeOffset(nowLocal.Date.AddHours(Hour), nowLocal.Offset);
        return nowLocal >= sevenToday && (lastBuilt is null || lastBuilt.Value < sevenToday);
    }

    public sealed record Lead(EventOverview Story, string? Summary);

    public static string Markdown(DateTimeOffset nowLocal, IReadOnlyList<Lead> leads, IReadOnlyList<(string Query, IReadOnlyList<EventOverview> Stories)> watches,
        IReadOnlyList<CoverageSilence> quiet, Func<string, string> storyLink)
    {
        var md = new StringBuilder();
        md.AppendLine($"The world this morning, {nowLocal.ToString("dd-MM-yy", CultureInfo.InvariantCulture)}: the stories most outlets carried in the last day, what matched your watchlist, and countries gone quiet. Rebuilt every morning at 07:00.");
        md.AppendLine();
        md.AppendLine("## Lead stories").AppendLine();
        if (leads.Count == 0) md.AppendLine("Nothing new arrived in the last day.").AppendLine();
        foreach (var lead in leads)
        {
            md.AppendLine($"- **[{Escape(lead.Story.Headline ?? lead.Story.Title)}]({storyLink(lead.Story.Id)})** · {lead.Story.OutletCount} outlets in {lead.Story.Languages.Count} languages");
            if (!string.IsNullOrWhiteSpace(lead.Summary)) md.AppendLine($"  {Escape(lead.Summary!)}");
        }
        md.AppendLine();
        if (watches.Count > 0)
        {
            md.AppendLine("## Your watchlist").AppendLine();
            foreach (var (query, stories) in watches)
            {
                md.AppendLine($"**{Escape(query)}**");
                md.AppendLine();
                if (stories.Count == 0) md.AppendLine("- Nothing new.");
                foreach (var story in stories) md.AppendLine($"- [{Escape(story.Headline ?? story.Title)}]({storyLink(story.Id)})");
                md.AppendLine();
            }
        }
        if (quiet.Count > 0)
        {
            md.AppendLine("## Unusually quiet").AppendLine();
            md.AppendLine(string.Join(", ", quiet.Select(q => q.Country)) + ": far fewer reports than usual from their outlets.");
            md.AppendLine();
        }
        return md.ToString();
    }

    public static string Escape(string text) => text.Replace("[", "\\[").Replace("]", "\\]").Replace("\n", " ");
}

/// <summary>6. A watchlist dossier: every story matching one watch, kept in Sentinel.</summary>
public static class WatchDossier
{
    public const string PageSystemKeyPrefix = "world-news-dossier:";
    public const int Window = 30;
    public const int MaxStories = 60;

    public static string KeyFor(string query) =>
        PageSystemKeyPrefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(query.Trim().ToLowerInvariant())))[..16].ToLowerInvariant();

    public static string Title(string query) => $"Watchlist: {query.Trim()}";

    public static string Markdown(string query, IReadOnlyList<EventOverview> stories, DateTimeOffset nowLocal, Func<string, string> storyLink)
    {
        var md = new StringBuilder();
        md.AppendLine($"Every World News story in the last {Window} days whose reports match **{MorningBriefing.Escape(query)}**, newest first. Updated hourly; last {nowLocal.ToString("dd-MM-yy HH:mm", CultureInfo.InvariantCulture)}.");
        md.AppendLine();
        if (stories.Count == 0) md.AppendLine("No matching stories yet.");
        foreach (var story in stories.Take(MaxStories))
            md.AppendLine($"- {story.LastSeen.ToLocalTime().ToString("dd-MM-yy", CultureInfo.InvariantCulture)} · [{MorningBriefing.Escape(story.Headline ?? story.Title)}]({storyLink(story.Id)}) · {story.OutletCount} outlets");
        return md.ToString();
    }
}
