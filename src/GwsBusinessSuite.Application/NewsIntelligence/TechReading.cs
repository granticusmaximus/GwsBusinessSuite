using System.Text;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Application.NewsIntelligence;

// Media Watch as a reading desk for software development (20-11-26, Grant: "Build 1-7").

/// <summary>1. The software development source pack: a Technical topic with key-less feeds.</summary>
public static class TechReadingPack
{
    public const string TopicName = "Software development";
    public const string ColorHex = "#22c55e";

    // Searched on Hacker News and dev.to, and matched against the curated engineering blogs.
    public const string Keywords = "software engineering, .NET, C#, Blazor, architecture, distributed systems, programming languages, AI coding, security, performance";

    // Fetched in full (the reader picked them). Each answered with items on 20-11-26.
    public static readonly IReadOnlyList<(string Name, string Url)> Feeds =
    [
        ("Lobsters", "https://lobste.rs/rss"),
        ("InfoQ", "https://feed.infoq.com/"),
        (".NET Blog", "https://devblogs.microsoft.com/dotnet/feed/"),
        ("GitHub Engineering", "https://github.blog/engineering/feed/"),
        ("Martin Fowler", "https://martinfowler.com/feed.atom"),
        ("Simon Willison", "https://simonwillison.net/atom/everything/"),
        ("Andrew Lock", "https://andrewlock.net/rss.xml"),
        ("The Pragmatic Engineer", "https://blog.pragmaticengineer.com/rss/"),
        ("The Cloudflare Blog", "https://blog.cloudflare.com/rss/"),
        ("Netflix TechBlog", "https://netflixtechblog.com/feed"),
    ];

    // The topic's trusted feeds with the pack's added (existing ones kept, none twice).
    public static string MergeFeeds(string? existing)
    {
        var lines = (existing ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var (_, url) in Feeds)
            if (!lines.Contains(url, StringComparer.OrdinalIgnoreCase)) lines.Add(url);
        return string.Join('\n', lines);
    }
}

/// <summary>2. Best reads first: community signal and depth, gently favouring the recent.</summary>
public static class TechReadingRank
{
    public static double Score(int? communityScore, int? minutes, DateTimeOffset? publishedAt, DateTimeOffset now)
    {
        var signal = Math.Log2(1 + Math.Max(0, communityScore ?? 0));          // 100 points ≈ 6.7, 1000 ≈ 10
        var depth = Math.Min(Math.Max(0, minutes ?? 0), 20) / 2.0;              // up to 10 for a 20-minute read
        var ageDays = publishedAt is { } at ? Math.Max(0, (now - at).TotalDays) : 3;
        return (signal + depth + 1) / Math.Pow(1 + ageDays / 3, 0.8);
    }

    public static IReadOnlyList<T> Order<T>(IEnumerable<T> items, Func<T, (int? Score, int? Minutes, DateTimeOffset? PublishedAt)> facts, DateTimeOffset now) =>
        items.OrderByDescending(item => { var f = facts(item); return Score(f.Score, f.Minutes, f.PublishedAt, now); }).ToList();

    public static int MinutesFor(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, (int)Math.Round(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length / 230.0));
}

/// <summary>4. Your stack as filters, and what's muted.</summary>
public static class ReadingFilters
{
    public sealed record StackTag(string Key, string Label, IReadOnlyList<string> Terms);

    public static readonly IReadOnlyList<StackTag> Tags =
    [
        new("dotnet", ".NET / Blazor", [".net", "dotnet", "c#", "csharp", "blazor", "asp.net", "entity framework", "ef core", "nuget", "f#"]),
        new("ai", "AI and local models", ["llm", "ai ", " ai", "agent", "ollama", "gpt", "claude", "gemini", "llama", "machine learning", "language model", " rag ", "embedding"]),
        new("security", "Security", ["security", "vulnerab", "cve", "exploit", "auth", "oauth", "passkey", "encryption", "malware", "supply chain", "zero-day"]),
        new("architecture", "Architecture", ["architecture", "microservice", "distributed", "event sourcing", "ddd", "domain-driven", "design pattern", "scalab", "system design", "monolith"]),
        new("apple", "Apple / MAUI", ["swift", "swiftui", "ios", "macos", "xcode", "maui", "catalyst", "apple", "visionos"]),
        new("web", "Web", ["javascript", "typescript", "css", "html", "react", "browser", "webassembly", "wasm", "node", "web "]),
    ];

    public static bool Matches(string tagKey, string title, string? description)
    {
        var tag = Tags.FirstOrDefault(t => t.Key == tagKey);
        if (tag is null) return false;
        var text = " " + (title + " " + description).ToLowerInvariant() + " ";
        return tag.Terms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    // An article passes when it matches any chosen tag (or none are chosen).
    public static bool MatchesAny(IReadOnlyCollection<string> tagKeys, string title, string? description) =>
        tagKeys.Count == 0 || tagKeys.Any(key => Matches(key, title, description));

    public static IReadOnlyList<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.Length > 0).ToList();

    public static bool IsMuted(string title, string? description, string? source, string? url, IReadOnlyList<string> mutedKeywords, IReadOnlyList<string> mutedSources)
    {
        var text = (title + " " + description).ToLowerInvariant();
        if (mutedKeywords.Any(k => text.Contains(k.ToLowerInvariant(), StringComparison.Ordinal))) return true;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;
        return mutedSources.Any(s =>
            string.Equals(source, s, StringComparison.OrdinalIgnoreCase)
            || (host.Length > 0 && (host == s.ToLowerInvariant() || host.EndsWith("." + s.ToLowerInvariant(), StringComparison.Ordinal))));
    }
}

/// <summary>3. The reading queue: what fits the time you have, shortest or longest first.</summary>
public static class ReadingQueue
{
    public const string SortSaved = "saved";
    public const string SortShortest = "shortest";
    public const string SortLongest = "longest";

    public static IReadOnlyList<SavedArticleView> Arrange(IEnumerable<SavedArticleView> saved, int? fitsInMinutes, string sort, bool showFinished)
    {
        var items = saved.Where(s => showFinished || s.FinishedAt is null)
            .Where(s => fitsInMinutes is not { } limit || (s.ReadingMinutes is { } m && m <= limit));
        return (sort switch
        {
            SortShortest => items.OrderBy(s => s.ReadingMinutes ?? int.MaxValue).ThenByDescending(s => s.SavedAt),
            SortLongest => items.OrderByDescending(s => s.ReadingMinutes ?? -1).ThenByDescending(s => s.SavedAt),
            _ => items.OrderBy(s => s.FinishedAt is not null).ThenByDescending(s => s.SavedAt),
        }).ToList();
    }
}

/// <summary>7. The weekly reading page: the week's best unread technical reads.</summary>
public static class ReadingDigest
{
    public const string PageSystemKey = "media-watch-weekly-reading";
    public const string PageTitle = "Weekly reading";
    public const int Picks = 10;

    public sealed record Candidate(string Title, string Url, string Source, string TopicName, DateTimeOffset? PublishedAt, int? Score, int? Minutes, string Take);

    public static IReadOnlyList<Candidate> Pick(IEnumerable<Candidate> candidates, IReadOnlySet<string> readUrlHashes, Func<string, string> hash, DateTimeOffset now) =>
        TechReadingRank.Order(
                candidates.Where(c => !readUrlHashes.Contains(hash(c.Url)))
                    .GroupBy(c => hash(c.Url)).Select(g => g.First()),
                c => (c.Score, c.Minutes, c.PublishedAt), now)
            .Take(Picks).ToList();

    public static string Markdown(IReadOnlyList<Candidate> picks, DateTimeOffset nowLocal)
    {
        var md = new StringBuilder();
        md.AppendLine($"The best technical reads from Media Watch for the week of {nowLocal.ToGwsDate()} that you haven't opened yet, deepest and most discussed first. Rebuilt every Monday.");
        md.AppendLine();
        if (picks.Count == 0)
        {
            md.AppendLine("Nothing new this week. Add the software development pack in Media Watch for more sources.");
            return md.ToString();
        }
        for (var i = 0; i < picks.Count; i++)
        {
            var p = picks[i];
            var facts = new List<string> { p.Source };
            if (p.Minutes is > 0) facts.Add($"{p.Minutes} min read");
            if (p.Score is > 0) facts.Add($"{p.Score} points");
            if (p.PublishedAt is { } at) facts.Add(at.ToLocalTime().ToGwsDate());
            md.AppendLine($"{i + 1}. [{Escape(p.Title)}]({p.Url}) · {string.Join(" · ", facts.Select(Escape))}");
            if (!string.IsNullOrWhiteSpace(p.Take)) md.AppendLine($"   {Escape(p.Take.Trim())}");
        }
        return md.ToString();
    }

    private static string Escape(string text) => text.Replace("[", "\\[").Replace("]", "\\]").Replace("\n", " ");
}

/// <summary>5. "Why it matters to me": three takeaways and one line for GWS.</summary>
public sealed record ReaderTakeaways(IReadOnlyList<string> Points, string? ForGws, string? Problem);

/// <summary>6. What a clip carries beyond the article: the reader's notes and highlights.</summary>
public sealed record ClipExtras(string? Notes, IReadOnlyList<string> Highlights);
