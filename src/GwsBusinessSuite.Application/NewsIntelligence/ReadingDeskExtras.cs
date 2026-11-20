using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.NewsIntelligence;

// Media Watch's reading desk, part two (20-11-26): the release radar, authors you follow, your
// library, and old highlights brought back.

/// <summary>The GitHub release feeds of what GWS is built on (each package is referenced by the repo).</summary>
public static class ReleaseRadar
{
    public const string TopicName = "Release radar";
    public const string ColorHex = "#a855f7";
    public const string Keywords = "release";

    // Package is the NuGet package the repo references (a test checks each one is still used);
    // null for a runtime the app talks to rather than a package.
    public static readonly IReadOnlyList<(string Name, string? Package, string Repo)> Sources =
    [
        ("ASP.NET Core", "Microsoft.AspNetCore.DataProtection", "dotnet/aspnetcore"),
        ("Entity Framework Core", "Microsoft.EntityFrameworkCore", "dotnet/efcore"),
        (".NET MAUI", "Microsoft.Maui.Controls", "dotnet/maui"),
        (".NET MAUI Community Toolkit", "CommunityToolkit.Maui", "CommunityToolkit/Maui"),
        ("Playwright for .NET", "Microsoft.Playwright", "microsoft/playwright-dotnet"),
        ("MailKit", "MailKit", "jstedfast/MailKit"),
        ("Markdig", "Markdig", "xoofx/markdig"),
        ("QuestPDF", "QuestPDF", "QuestPDF/QuestPDF"),
        ("Stripe.net", "Stripe.net", "stripe/stripe-dotnet"),
        ("SmartReader", "SmartReader", "Strumenta/SmartReader"),
        ("Serilog for ASP.NET Core", "Serilog.AspNetCore", "serilog/serilog-aspnetcore"),
        ("PdfPig", "PdfPig", "UglyToad/PdfPig"),
        ("FIDO2", "Fido2", "passwordless-lib/fido2-net-lib"),
        ("HtmlSanitizer", "HtmlSanitizer", "mganss/HtmlSanitizer"),
        ("Ical.Net", "Ical.Net", "ical-org/ical.net"),
        ("SkiaSharp", "SkiaSharp", "mono/SkiaSharp"),
        ("Ollama", null, "ollama/ollama"),
    ];

    public static string FeedUrl(string repo) => $"https://github.com/{repo}/releases.atom";

    public static string MergeFeeds(string? existing)
    {
        var lines = (existing ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var (_, _, repo) in Sources)
        {
            var url = FeedUrl(repo);
            if (!lines.Contains(url, StringComparer.OrdinalIgnoreCase)) lines.Add(url);
        }
        return string.Join('\n', lines);
    }
}

/// <summary>Writers' names as feeds and pages give them, made comparable.</summary>
public static partial class NewsAuthors
{
    // "jane@example.com (Jane Doe)" -> "Jane Doe"; "By Jane Doe" -> "Jane Doe"; emails alone and
    // generic bylines ("Staff", "admin") are dropped.
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var bracketed = BracketedName().Match(text);
        if (bracketed.Success) text = bracketed.Groups[1].Value;
        text = ByPrefix().Replace(text, string.Empty).Trim().Trim(',', ';', '|').Trim();
        text = Regex.Replace(text, @"\s+", " ");
        if (text.Length is < 3 or > 80 || text.Contains('@') || text.Contains("://", StringComparison.Ordinal)) return null;
        return GenericNames.Contains(text) ? null : text;
    }

    public static bool Matches(string? author, string followed) =>
        Clean(author) is { } name && string.Equals(name, followed.Trim(), StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "staff", "editor", "editors", "editorial", "team", "news desk", "guest", "unknown", "anonymous",
    };

    [GeneratedRegex(@"\(([^)]+)\)\s*$")]
    private static partial Regex BracketedName();

    [GeneratedRegex(@"^(by|written by|posted by)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ByPrefix();
}

/// <summary>Something in your own library that matched a search: queued, clipped or read.</summary>
public sealed record LibraryHit(string Title, string Url, string Source, string Where, string? Snippet, Guid? ClippedPageId, DateTimeOffset? At);

public static class LibraryWhere
{
    public const string Queued = "Queued";
    public const string Clipped = "Clipped";
    public const string Read = "Read";
}

public static partial class ReadingLibrary
{
    public const int MaxHits = 40;

    // Every word must appear in one of the texts (title, summary, source, clip text).
    public static bool Matches(string query, params string?[] texts)
    {
        var words = Words(query);
        if (words.Count == 0) return false;
        var all = string.Join(' ', texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        return words.All(w => all.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public static string? Snippet(string? text, string query, int radius = 120)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = Words(query);
        var at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at < 0) return null;
        var start = Math.Max(0, at - radius);
        var end = Math.Min(text.Length, at + radius);
        return (start > 0 ? "…" : string.Empty) + Regex.Replace(text[start..end], @"\s+", " ").Trim() + (end < text.Length ? "…" : string.Empty);
    }

    public static IReadOnlyList<string> Words(string? query) =>
        (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length >= 2).Take(8).ToList();

    // 6. Old highlights brought back: the quote lines of clips at least a week old, three a week,
    // a different three each week (picked by the week number, so a rebuild in the same week agrees).
    public const int ResurfacedPerWeek = 3;

    public static IReadOnlyList<(string Quote, string Title)> Resurface(IEnumerable<(string Title, string Markdown, DateTimeOffset ClippedAt)> clips, DateTimeOffset now)
    {
        var quotes = clips.Where(c => now - c.ClippedAt >= TimeSpan.FromDays(7))
            .OrderBy(c => c.ClippedAt)
            .SelectMany(c => QuoteLines(c.Markdown).Select(q => (Quote: q, c.Title)))
            .ToList();
        if (quotes.Count <= ResurfacedPerWeek) return quotes;
        var week = (int)(now.ToUnixTimeSeconds() / (7 * 24 * 3600));
        var start = week * ResurfacedPerWeek % quotes.Count;
        return Enumerable.Range(0, ResurfacedPerWeek).Select(i => quotes[(start + i) % quotes.Count]).ToList();
    }

    // "> a highlight" lines (a clip's highlights), joined when a quote runs over several lines.
    public static IReadOnlyList<string> QuoteLines(string? markdown)
    {
        var quotes = new List<string>();
        var current = new List<string>();
        foreach (var line in (markdown ?? string.Empty).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('>'))
            {
                current.Add(trimmed.TrimStart('>').Trim());
                continue;
            }
            if (current.Count > 0) quotes.Add(string.Join(' ', current).Trim());
            current.Clear();
        }
        if (current.Count > 0) quotes.Add(string.Join(' ', current).Trim());
        return quotes.Where(q => q.Length >= 20).ToList();
    }
}
