using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.NewsIntelligence;

// One story as reported by one or more outlets. Lead is the earliest-published report with an AI
// take (falling back to the earliest); OtherReports are the rest, newest first.
public sealed record NewsStory(NewsItemDto Lead, IReadOnlyList<NewsItemDto> OtherReports, bool IsBreaking)
{
    public int OutletCount => 1 + OtherReports.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase)
        .Count(s => !string.Equals(s, Lead.Source, StringComparison.OrdinalIgnoreCase));
}

public static partial class NewsStoryClusterer
{
    // Two headlines are the same story when at least half of their meaningful words overlap.
    public const double SimilarityThreshold = 0.5;

    // "Breaking": several outlets reporting the same story within a few hours is a real signal;
    // a signal word in the headline only counts when the story is also fresh.
    public const int BreakingOutletCount = 3;
    public static readonly TimeSpan BreakingWindow = TimeSpan.FromHours(6);
    public static readonly TimeSpan SignalWordWindow = TimeSpan.FromHours(3);
    private static readonly string[] SignalWords = ["breaking", "developing", "live updates", "just in"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "into", "over", "after", "about", "says", "said",
        "will", "what", "when", "where", "who", "why", "how", "are", "was", "were", "has", "have", "had", "its",
        "their", "his", "her", "they", "than", "then", "but", "not", "you", "your", "our", "out", "new", "more",
        "amid", "says", "could", "would", "should", "may", "can", "just", "now", "via", "per", "up", "off"
    };

    public static IReadOnlyList<NewsStory> Cluster(IReadOnlyList<NewsItemDto> items, DateTimeOffset now)
    {
        var groups = new List<(HashSet<string> Words, List<NewsItemDto> Items)>();
        foreach (var item in items.OrderBy(i => i.PublishedAt ?? i.FetchedAt))
        {
            var words = SignificantWords(item.Title);
            var match = words.Count < 3 ? default : groups.FirstOrDefault(g => Similarity(g.Words, words) >= SimilarityThreshold);
            if (match.Items is not null)
            {
                match.Items.Add(item);
                match.Words.UnionWith(words);
            }
            else
            {
                groups.Add((words, [item]));
            }
        }

        return groups
            .Select(g =>
            {
                var lead = g.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.OllamaSummary)) ?? g.Items[0];
                var others = g.Items.Where(i => i != lead).OrderByDescending(i => i.PublishedAt ?? i.FetchedAt).ToList();
                var story = new NewsStory(lead, others, false);
                return story with { IsBreaking = IsBreaking(story, now) };
            })
            .OrderByDescending(s => s.IsBreaking)
            .ThenByDescending(s => Latest(s))
            .ToList();
    }

    public static bool IsBreaking(NewsStory story, DateTimeOffset now)
    {
        var reports = story.OtherReports.Prepend(story.Lead).ToList();
        var recentOutlets = reports
            .Where(r => (r.PublishedAt ?? r.FetchedAt) >= now - BreakingWindow)
            .Select(r => r.Source.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (recentOutlets >= BreakingOutletCount) return true;

        return reports.Any(r => (r.PublishedAt ?? r.FetchedAt) >= now - SignalWordWindow
            && SignalWords.Any(w => r.Title.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    // Google News appends " - Outlet Name" to every headline; that would make every story from one
    // outlet look alike, so it's dropped before comparing.
    public static HashSet<string> SignificantWords(string title)
    {
        var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 20) title = title[..dash];
        return WordPattern().Matches(title.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static double Similarity(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return (double)shared / Math.Min(a.Count, b.Count);
    }

    // Normalized so http/https, "www." and a trailing slash don't make the same article look new.
    public static string UrlHash(string url)
    {
        var normalized = url.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            normalized = $"{uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}".ToLowerInvariant();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
    }

    private static DateTimeOffset Latest(NewsStory story) =>
        story.OtherReports.Prepend(story.Lead).Max(r => r.PublishedAt ?? r.FetchedAt);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'+#.-]*[\p{L}\p{N}+#]|[\p{L}\p{N}]")]
    private static partial Regex WordPattern();
}

// Terms used by the Trends view: the same significant words, counted per day.
public static class NewsTrendTerms
{
    public static Dictionary<string, int> Count(IEnumerable<string> titles)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in titles)
        {
            foreach (var word in NewsStoryClusterer.SignificantWords(title))
            {
                counts[word] = counts.GetValueOrDefault(word) + 1;
            }
        }
        return counts;
    }

    // Keeps the stored per-day term list small: the top terms only.
    public static Dictionary<string, int> Merge(Dictionary<string, int> existing, Dictionary<string, int> added, int keep = 60)
    {
        foreach (var (term, count) in added) existing[term] = existing.GetValueOrDefault(term) + count;
        return existing.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(keep)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // Terms used noticeably more in the last `recentDays` than their daily average before that.
    public static IReadOnlyList<RisingTerm> Rising(IReadOnlyList<(DateOnly Day, Dictionary<string, int> Terms)> days, DateOnly today,
        int recentDays = 2, int minimumRecent = 3, int take = 10)
    {
        var recentStart = today.AddDays(-(recentDays - 1));
        var recent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var baseline = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var baselineDays = days.Where(d => d.Day < recentStart).Select(d => d.Day).Distinct().Count();
        foreach (var (day, terms) in days)
        {
            var target = day >= recentStart ? recent : baseline;
            foreach (var (term, count) in terms) target[term] = target.GetValueOrDefault(term) + count;
        }

        return recent
            .Where(kv => kv.Value >= minimumRecent)
            .Select(kv =>
            {
                var recentPerDay = kv.Value / (double)recentDays;
                var baselinePerDay = baselineDays == 0 ? 0 : baseline.GetValueOrDefault(kv.Key) / (double)baselineDays;
                return new RisingTerm(kv.Key, kv.Value, Math.Round(baselinePerDay, 1), baselinePerDay == 0 ? null : recentPerDay / baselinePerDay);
            })
            .Where(t => t.Ratio is null || t.Ratio >= 2)
            .OrderByDescending(t => t.Ratio ?? double.MaxValue)
            .ThenByDescending(t => t.RecentCount)
            .Take(take)
            .ToList();
    }
}

public sealed record RisingTerm(string Term, int RecentCount, double BaselinePerDay, double? Ratio);
