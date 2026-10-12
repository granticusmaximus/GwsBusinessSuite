using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// "Clean up a messy note" (Sentinel item 26): the model restructures a page into headings,
// bullets and to-dos without adding facts. The page goes to the model as Markdown and comes back
// as Markdown, parsed into blocks; a guard rejects output that brings in new words (likely new
// facts), and the user sees before/after before anything is saved.

public sealed record SentinelCleanupResult(IReadOnlyList<WikiBlock> Blocks, double NewWordShare, double DroppedWordShare, string? Warning);

public interface ISentinelNoteCleanupService
{
    // The page as Markdown for the model. Needs edit access.
    Task<string> PrepareAsync(Guid pageId, string username, CancellationToken cancellationToken = default);
    Task<string> RunOnServerAsync(string pageMarkdown, CancellationToken cancellationToken = default);
    // Replaces the page's blocks (a restore point is kept in its history).
    Task ApplyAsync(Guid pageId, IReadOnlyList<WikiBlock> blocks, string username, CancellationToken cancellationToken = default);
}

public static partial class SentinelNoteCleanup
{
    public const int MaxInputCharacters = 8_000;
    // More new words than this share of the result means the model added content: refused.
    public const double MaxNewWordShare = 0.08;
    // Losing more than this share of the original's words gets a warning (shown, not refused).
    public const double WarnDroppedWordShare = 0.25;

    public const string SystemPrompt =
        """
        You tidy up messy notes into a clear structure. Return ONLY the tidied note as Markdown.

        Rules, all mandatory:
        - Use "## " headings to group related points, "- " bullets for points, and "- [ ] " for
          anything that is a task or action item ("- [x] " if it's marked done).
        - Keep every fact, name, number, date and link exactly as written. Do not add facts,
          examples, explanations, or a summary that isn't in the note. Do not drop content.
        - You may fix obvious typos and merge duplicate points.
        - No preamble, no commentary, no code fences around the result.
        """;

    public static string UserPrompt(string pageMarkdown) => $"Note:\n{pageMarkdown}";

    public static string ToMarkdown(IReadOnlyList<WikiBlock> blocks)
    {
        var markdown = WikiMarkdownExporter.ExportPage(string.Empty, blocks).Trim();
        // ExportPage starts with "# {title}"; with an empty title that's a bare "#" line.
        markdown = Regex.Replace(markdown, @"^#\s*\n", string.Empty);
        return markdown.Length <= MaxInputCharacters ? markdown : markdown[..MaxInputCharacters];
    }

    public static SentinelCleanupResult Check(string original, string? output)
    {
        var text = (output ?? string.Empty).Trim();
        text = Fence().Replace(text, string.Empty).Trim();
        if (text.Length == 0) return new SentinelCleanupResult([], 0, 1, "The model returned nothing. Try again.");

        var before = Words(original);
        var after = Words(text);
        var newWords = after.Count == 0 ? 0 : after.Count(w => !before.Contains(w)) / (double)after.Count;
        var dropped = before.Count == 0 ? 0 : before.Count(w => !after.Contains(w)) / (double)before.Count;
        var blocks = NotionMarkdownBlockParser.Parse(text);
        if (newWords > MaxNewWordShare)
            return new SentinelCleanupResult([], newWords, dropped,
                $"The model added content that isn't in the note ({newWords:P0} new words), so this result was refused. Try again or use a different model.");
        var warning = dropped > WarnDroppedWordShare ? $"About {dropped:P0} of the original words are missing from the result. Check nothing important was dropped." : null;
        return new SentinelCleanupResult(blocks, newWords, dropped, warning);
    }

    // Distinct content words (3+ letters or any number), lowercase.
    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length >= 3 || w.Any(char.IsDigit))
            .Where(w => !Structural.Contains(w))
            .ToHashSet();

    // Words the restructuring itself may add.
    private static readonly HashSet<string> Structural = ["notes", "note", "tasks", "task", "todo", "action", "actions", "items", "item", "summary", "other", "misc", "general", "the", "and", "for"];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^```[a-z]*\s*|\s*```$")]
    private static partial Regex Fence();
}
