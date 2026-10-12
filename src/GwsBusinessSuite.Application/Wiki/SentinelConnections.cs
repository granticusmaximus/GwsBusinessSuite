using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Related notes + unlinked mentions (Sentinel item 3).
public sealed record SentinelRelatedPage(Guid Id, string Title, string? Icon, string Preview);

public sealed record SentinelUnlinkedMention(Guid PageId, string PageTitle, Guid BlockId, string Name, string Snippet);

public interface ISentinelConnectionsService
{
    // Pages close in meaning (the semantic index), among those the user can view.
    Task<IReadOnlyList<SentinelRelatedPage>> GetRelatedAsync(Guid pageId, string username, CancellationToken cancellationToken = default);

    // Lines on other viewable pages that name this page (title or alias) without linking to it.
    Task<IReadOnlyList<SentinelUnlinkedMention>> GetUnlinkedMentionsAsync(Guid pageId, string username, CancellationToken cancellationToken = default);

    // Turns the first unlinked mention on that line into a link. Needs edit access to the line's page.
    Task<bool> LinkMentionAsync(Guid sourcePageId, Guid blockId, Guid targetPageId, string username, CancellationToken cancellationToken = default);
}

public static class SentinelMentions
{
    // 2, not 3: initials like "GW" are exactly what aliases are for, and matching is whole-word.
    public const int MinNameLength = 2;

    private static readonly HashSet<string> TextBlocks =
    [
        WikiBlockTypes.Paragraph, WikiBlockTypes.Heading1, WikiBlockTypes.Heading2, WikiBlockTypes.Heading3,
        WikiBlockTypes.BulletedListItem, WikiBlockTypes.NumberedListItem, WikiBlockTypes.ToDo,
        WikiBlockTypes.Quote, WikiBlockTypes.Callout, WikiBlockTypes.Toggle
    ];

    // Title first, then aliases (single letters skipped), longest first so "Grant Watson"
    // wins over "Grant".
    public static IReadOnlyList<string> Names(string title, string? aliases) =>
        WikiPageAliases.Parse(aliases).Prepend(title ?? string.Empty)
            .Select(n => n.Trim()).Where(n => n.Length >= MinNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();

    // The name an unlinked part of this block mentions, or null.
    public static string? FindUnlinked(WikiBlock block, IReadOnlyList<string> names)
    {
        if (!TextBlocks.Contains(block.Type)) return null;
        foreach (var span in block.RichText.Where(s => string.IsNullOrEmpty(s.Link) && !s.Code))
        {
            foreach (var name in names)
            {
                if (Find(span.Text, name) >= 0) return name;
            }
        }
        return null;
    }

    // The block with its first unlinked mention of any name made into a link to the page;
    // null when there's no unlinked mention left.
    public static WikiBlock? LinkFirst(WikiBlock block, IReadOnlyList<string> names, Guid targetPageId)
    {
        if (!TextBlocks.Contains(block.Type)) return null;
        var spans = block.RichText.ToList();
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (!string.IsNullOrEmpty(span.Link) || span.Code) continue;
            foreach (var name in names)
            {
                var at = Find(span.Text, name);
                if (at < 0) continue;
                var replacement = new List<WikiRichTextSpan>();
                if (at > 0) replacement.Add(span with { Text = span.Text[..at] });
                replacement.Add(span with { Text = span.Text.Substring(at, name.Length), Link = $"wikilink:{targetPageId}" });
                if (at + name.Length < span.Text.Length) replacement.Add(span with { Text = span.Text[(at + name.Length)..] });
                spans.RemoveAt(i);
                spans.InsertRange(i, replacement);
                return block with { RichText = spans };
            }
        }
        return null;
    }

    // A short window of the line around the mention.
    public static string Snippet(string text, string name, int width = 120)
    {
        var at = Find(text, name);
        if (at < 0 || text.Length <= width) return text.Length <= width ? text : text[..width] + "…";
        var start = Math.Max(0, at - (width - name.Length) / 2);
        var end = Math.Min(text.Length, start + width);
        return (start > 0 ? "…" : string.Empty) + text[start..end] + (end < text.Length ? "…" : string.Empty);
    }

    // Whole-word, case-insensitive: "Grant" doesn't match "Granted".
    private static int Find(string text, string name)
    {
        var match = Regex.Match(text ?? string.Empty, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
        return match.Success ? match.Index : -1;
    }
}
