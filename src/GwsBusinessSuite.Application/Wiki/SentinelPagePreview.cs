using System.Text.RegularExpressions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// One line of a hover preview: plain text only (the browser sets it with textContent), with a
// kind the popover styles - heading, bullet, number, todo, quote, code, callout, media, divider
// or paragraph.
public sealed record SentinelPreviewLine(string Kind, string Text, int Indent = 0, bool Checked = false);

public sealed record SentinelPagePreview(
    Guid Id, string Title, string? Icon, IReadOnlyList<SentinelPreviewLine> Lines, int MoreBlocks,
    DateTimeOffset UpdatedAt, bool Verified);

public interface ISentinelPagePreviewService
{
    // Null when the page doesn't exist, is in the trash, or the user can't view it.
    Task<SentinelPagePreview?> GetAsync(Guid pageId, string username, CancellationToken cancellationToken = default);
}

// Hover previews of [[links]], page mentions and linked cards (Sentinel item 14): the first few
// blocks of the target page as plain lines, never HTML.
public static partial class SentinelPagePreviewBuilder
{
    public const int MaxLines = 6;
    public const int MaxLineChars = 160;
    public const int MaxTotalChars = 700;

    public static SentinelPagePreview Build(WikiPage page, IReadOnlyList<WikiBlock> blocks, DateTimeOffset now)
    {
        var lines = new List<SentinelPreviewLine>();
        var total = 0;
        var used = 0;
        foreach (var block in blocks)
        {
            if (lines.Count >= MaxLines || total >= MaxTotalChars) break;
            used++;
            if (ToLine(block) is not { } line) continue;
            var room = Math.Min(MaxLineChars, MaxTotalChars - total);
            if (line.Text.Length > room) line = line with { Text = line.Text[..Math.Max(0, room - 1)].TrimEnd() + "…" };
            total += line.Text.Length;
            lines.Add(line);
        }
        var verified = page.VerifiedUntilUnix is { } until && until > now.ToUnixTimeSeconds();
        return new SentinelPagePreview(page.Id, string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title.Trim(), page.Icon,
            lines, Math.Max(0, blocks.Count - used), page.UpdatedAt ?? page.CreatedAt, verified);
    }

    private static SentinelPreviewLine? ToLine(WikiBlock block)
    {
        var text = Clean(block.PlainText);
        var indent = Math.Clamp(block.IndentLevel, 0, 4);
        return block.Type switch
        {
            WikiBlockTypes.Heading1 or WikiBlockTypes.Heading2 or WikiBlockTypes.Heading3 => text.Length == 0 ? null : new("heading", text),
            WikiBlockTypes.BulletedListItem => text.Length == 0 ? null : new("bullet", text, indent),
            WikiBlockTypes.NumberedListItem => text.Length == 0 ? null : new("number", text, indent),
            WikiBlockTypes.ToDo => text.Length == 0 ? null : new("todo", text, indent, block.Props.GetValueOrDefault("checked") == "true"),
            WikiBlockTypes.Quote => text.Length == 0 ? null : new("quote", text),
            WikiBlockTypes.Callout => text.Length == 0 ? null : new("callout", text),
            WikiBlockTypes.Code => text.Length == 0 ? null : new("code", text.Split('\n')[0]),
            WikiBlockTypes.Divider => new("divider", string.Empty),
            WikiBlockTypes.Image => new("media", "Image" + Caption(block)),
            WikiBlockTypes.Video => new("media", "Video" + Caption(block)),
            WikiBlockTypes.Audio => new("media", "Audio" + Caption(block)),
            WikiBlockTypes.File or WikiBlockTypes.Pdf => new("media", "File" + Caption(block)),
            WikiBlockTypes.Embed => new("media", "Link" + Caption(block)),
            WikiBlockTypes.Table => new("media", "Table"),
            WikiBlockTypes.Diagram => new("media", "Diagram"),
            WikiBlockTypes.Canvas => new("media", "Whiteboard"),
            WikiBlockTypes.Business => new("media", SentinelLiveBlockKinds.Describe(block)),
            WikiBlockTypes.LinkedDatabase or WikiBlockTypes.InlineDatabase => new("media", "Database" + Caption(block)),
            WikiBlockTypes.PageLink => new("media", "Page: " + Clean(block.Props.GetValueOrDefault("pageTitle") ?? text)),
            WikiBlockTypes.Markdown => FirstMarkdownLine(text),
            WikiBlockTypes.Breadcrumb or WikiBlockTypes.TableOfContents or WikiBlockTypes.Button or WikiBlockTypes.Columns
                or WikiBlockTypes.SyncedBlock or WikiBlockTypes.Equation => null,
            _ => text.Length == 0 ? null : new("paragraph", text)
        };
    }

    private static string Caption(WikiBlock block)
    {
        var caption = Clean(block.PlainText);
        if (caption.Length == 0) caption = Clean(block.Props.GetValueOrDefault("caption") ?? block.Props.GetValueOrDefault("title") ?? string.Empty);
        return caption.Length == 0 ? string.Empty : $": {caption}";
    }

    // Legacy Markdown blocks: their first non-empty line, with the heading/list markers stripped.
    private static SentinelPreviewLine? FirstMarkdownLine(string markdown)
    {
        var first = markdown.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null) return null;
        var isHeading = first.StartsWith('#');
        var stripped = MarkdownMarker().Replace(first, string.Empty).Trim();
        return stripped.Length == 0 ? null : new(isHeading ? "heading" : "paragraph", stripped);
    }

    private static string Clean(string text) => Whitespace().Replace(text ?? string.Empty, " ").Trim();

    [GeneratedRegex(@"^(#{1,6}\s+|[-*+]\s+|\d+[.)]\s+|>\s*)")]
    private static partial Regex MarkdownMarker();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Whitespace();
}
