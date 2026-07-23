using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

/// <summary>
/// Converts the Markdown shape produced by Notion exports into Sentinel's native editable
/// blocks. Notion mixes CommonMark with HTML for structures that have no Markdown equivalent
/// (notably callouts and toggles), so a line-only plain-text conversion loses both appearance
/// and behavior.
/// </summary>
public static partial class NotionMarkdownBlockParser
{
    public static IReadOnlyList<WikiBlock> Parse(string markdown, string? pageTitle = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var blocks = new List<WikiBlock>();
        ParseLines(lines, blocks, 0);

        if (!string.IsNullOrWhiteSpace(pageTitle)
            && blocks.FirstOrDefault() is { Type: WikiBlockTypes.Heading1 } first
            && string.Equals(first.PlainText.Trim(), pageTitle.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            blocks.RemoveAt(0);
        }

        return blocks;
    }

    private static void ParseLines(
        IReadOnlyList<string> lines,
        ICollection<WikiBlock> blocks,
        int inheritedIndent)
    {
        var paragraph = new List<string>();
        var paragraphIndent = inheritedIndent;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            var content = string.Join("\n", paragraph).Trim();
            paragraph.Clear();
            if (content.Length == 0)
            {
                return;
            }

            var attachment = StandaloneLinkPattern().Match(content);
            if (attachment.Success && IsAttachmentUrl(attachment.Groups["url"].Value))
            {
                blocks.Add(NewBlock(
                    WikiBlockTypes.Embed,
                    paragraphIndent,
                    [new WikiRichTextSpan(WebUtility.HtmlDecode(attachment.Groups["label"].Value))],
                    new Dictionary<string, string>
                    {
                        ["url"] = WebUtility.HtmlDecode(attachment.Groups["url"].Value),
                        ["fileName"] = WebUtility.HtmlDecode(attachment.Groups["label"].Value)
                    }));
                return;
            }

            blocks.Add(NewBlock(
                WikiBlockTypes.Paragraph,
                paragraphIndent,
                ParseRichText(content)));
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var rawLine = lines[index];
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var leadingWidth = IndentWidth(rawLine);
            var indent = inheritedIndent + (leadingWidth == 0 ? 0 : (leadingWidth + 3) / 4);
            var contentLine = rawLine.TrimStart();

            if (contentLine.StartsWith("```", StringComparison.Ordinal)
                || contentLine.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = contentLine[..3];
                var language = contentLine[3..].Trim();
                var code = new List<string>();
                while (++index < lines.Count && !lines[index].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                }
                blocks.Add(NewBlock(
                    WikiBlockTypes.Code,
                    indent,
                    [new WikiRichTextSpan(string.Join("\n", code))],
                    new Dictionary<string, string> { ["language"] = language }));
                continue;
            }

            if (contentLine.StartsWith("<aside", StringComparison.OrdinalIgnoreCase))
            {
                FlushParagraph();
                var html = CollectHtmlBlock(lines, ref index, "</aside>");
                AddCallout(blocks, html, indent);
                continue;
            }

            if (contentLine.StartsWith("<details", StringComparison.OrdinalIgnoreCase))
            {
                FlushParagraph();
                var html = CollectHtmlBlock(lines, ref index, "</details>");
                AddToggle(blocks, html, indent);
                continue;
            }

            var heading = HeadingPattern().Match(contentLine);
            if (heading.Success)
            {
                FlushParagraph();
                var type = heading.Groups["marks"].Length switch
                {
                    1 => WikiBlockTypes.Heading1,
                    2 => WikiBlockTypes.Heading2,
                    _ => WikiBlockTypes.Heading3
                };
                blocks.Add(NewBlock(type, indent, ParseRichText(heading.Groups["text"].Value)));
                continue;
            }

            if (index + 1 < lines.Count
                && LooksLikeTableRow(contentLine)
                && IsTableSeparator(lines[index + 1].Trim()))
            {
                FlushParagraph();
                var tableRows = new List<IReadOnlyList<IReadOnlyList<WikiRichTextSpan>>>
                {
                    ParseTableRow(contentLine)
                };
                index += 2;
                while (index < lines.Count && LooksLikeTableRow(lines[index].Trim()))
                {
                    tableRows.Add(ParseTableRow(lines[index].Trim()));
                    index++;
                }
                index--;
                blocks.Add(NewBlock(
                    WikiBlockTypes.Table,
                    indent,
                    [new WikiRichTextSpan(string.Join(
                        "\n",
                        tableRows.Select(row => string.Join(
                            " | ",
                            row.Select(cell => string.Concat(cell.Select(span => span.Text)))))))],
                    new Dictionary<string, string>
                    {
                        ["hasColumnHeader"] = "true",
                        ["tableJson"] = JsonSerializer.Serialize(tableRows, WikiBlockJson.Options)
                    }));
                continue;
            }

            var image = ImagePattern().Match(contentLine);
            if (image.Success)
            {
                FlushParagraph();
                blocks.Add(NewBlock(
                    WikiBlockTypes.Image,
                    indent,
                    ParseRichText(image.Groups["alt"].Value),
                    new Dictionary<string, string>
                    {
                        ["url"] = WebUtility.HtmlDecode(image.Groups["url"].Value)
                    }));
                continue;
            }

            var task = ToDoPattern().Match(contentLine);
            if (task.Success)
            {
                FlushParagraph();
                blocks.Add(NewBlock(
                    WikiBlockTypes.ToDo,
                    indent,
                    ParseRichText(task.Groups["text"].Value),
                    new Dictionary<string, string>
                    {
                        ["checked"] = task.Groups["state"].Value.Equals("x", StringComparison.OrdinalIgnoreCase)
                            ? "true"
                            : "false"
                    }));
                continue;
            }

            var bullet = BulletedPattern().Match(contentLine);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(NewBlock(
                    WikiBlockTypes.BulletedListItem,
                    indent,
                    ParseRichText(bullet.Groups["text"].Value)));
                continue;
            }

            var numbered = NumberedPattern().Match(contentLine);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(NewBlock(
                    WikiBlockTypes.NumberedListItem,
                    indent,
                    ParseRichText(numbered.Groups["text"].Value),
                    new Dictionary<string, string>
                    {
                        ["number"] = numbered.Groups["number"].Value
                    }));
                continue;
            }

            if (contentLine.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(NewBlock(
                    WikiBlockTypes.Quote,
                    indent,
                    ParseRichText(contentLine[1..].TrimStart())));
                continue;
            }

            if (contentLine is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(NewBlock(WikiBlockTypes.Divider, indent, []));
                continue;
            }

            if (contentLine.StartsWith('<') && contentLine.EndsWith('>'))
            {
                contentLine = HtmlToMarkdown(contentLine);
            }
            if (paragraph.Count == 0)
            {
                paragraphIndent = indent;
            }
            paragraph.Add(contentLine);
        }

        FlushParagraph();
    }

    private static IReadOnlyList<WikiRichTextSpan> ParseRichText(string value)
    {
        var normalized = HtmlToMarkdown(value);
        var spans = new List<WikiRichTextSpan>();
        ParseInline(normalized, new InlineMarks(), spans);
        return spans;
    }

    private static void ParseInline(
        string value,
        InlineMarks marks,
        ICollection<WikiRichTextSpan> spans)
    {
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }
            AddSpan(spans, plain.ToString(), marks);
            plain.Clear();
        }

        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '\\' && index + 1 < value.Length)
            {
                plain.Append(value[index + 1]);
                index += 2;
                continue;
            }

            if (TryDelimited(value, index, "**", out var strongContent, out var strongEnd)
                || TryDelimited(value, index, "__", out strongContent, out strongEnd))
            {
                FlushPlain();
                ParseInline(strongContent, marks with { Bold = true }, spans);
                index = strongEnd;
                continue;
            }

            if (TryDelimited(value, index, "~~", out var strikeContent, out var strikeEnd))
            {
                FlushPlain();
                ParseInline(strikeContent, marks with { Strikethrough = true }, spans);
                index = strikeEnd;
                continue;
            }

            if (TryDelimited(value, index, "`", out var codeContent, out var codeEnd))
            {
                FlushPlain();
                AddSpan(spans, codeContent, marks with { Code = true });
                index = codeEnd;
                continue;
            }

            var link = LinkAtPattern().Match(value, index);
            if (link.Success && link.Index == index)
            {
                FlushPlain();
                ParseInline(
                    link.Groups["label"].Value,
                    marks with { Link = WebUtility.HtmlDecode(link.Groups["url"].Value) },
                    spans);
                index += link.Length;
                continue;
            }

            if (TryDelimited(value, index, "*", out var italicContent, out var italicEnd)
                || TryDelimited(value, index, "_", out italicContent, out italicEnd))
            {
                FlushPlain();
                ParseInline(italicContent, marks with { Italic = true }, spans);
                index = italicEnd;
                continue;
            }

            plain.Append(value[index]);
            index++;
        }

        FlushPlain();
    }

    private static bool TryDelimited(
        string value,
        int start,
        string delimiter,
        out string content,
        out int end)
    {
        content = string.Empty;
        end = start;
        if (!value.AsSpan(start).StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        var closing = value.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        if (closing <= start + delimiter.Length)
        {
            return false;
        }

        content = value[(start + delimiter.Length)..closing];
        end = closing + delimiter.Length;
        return true;
    }

    private static void AddSpan(
        ICollection<WikiRichTextSpan> spans,
        string text,
        InlineMarks marks)
    {
        if (text.Length == 0)
        {
            return;
        }

        var decoded = WebUtility.HtmlDecode(text);
        if (spans.LastOrDefault() is { } previous
            && previous.Bold == marks.Bold
            && previous.Italic == marks.Italic
            && previous.Strikethrough == marks.Strikethrough
            && previous.Code == marks.Code
            && string.Equals(previous.Link, marks.Link, StringComparison.Ordinal))
        {
            spans.Remove(previous);
            spans.Add(previous with { Text = previous.Text + decoded });
            return;
        }

        spans.Add(new WikiRichTextSpan(
            decoded,
            marks.Bold,
            marks.Italic,
            marks.Strikethrough,
            marks.Code,
            marks.Link));
    }

    private static void AddCallout(ICollection<WikiBlock> blocks, string html, int indent)
    {
        var content = StripContainer(html, "aside");
        var markdown = HtmlToMarkdown(content).Trim();
        var icon = ExtractLeadingIcon(ref markdown) ?? "💡";
        blocks.Add(NewBlock(
            WikiBlockTypes.Callout,
            indent,
            ParseRichText(markdown),
            new Dictionary<string, string> { ["icon"] = icon }));
    }

    private static void AddToggle(ICollection<WikiBlock> blocks, string html, int indent)
    {
        var summaryMatch = SummaryPattern().Match(html);
        var summary = summaryMatch.Success
            ? HtmlToMarkdown(summaryMatch.Groups["content"].Value).Trim()
            : "Toggle";
        blocks.Add(NewBlock(
            WikiBlockTypes.Toggle,
            indent,
            ParseRichText(summary),
            new Dictionary<string, string> { ["open"] = "false" }));

        var body = summaryMatch.Success
            ? html.Remove(summaryMatch.Index, summaryMatch.Length)
            : html;
        body = StripContainer(body, "details");
        var markdownBody = HtmlToMarkdown(body).Trim();
        if (markdownBody.Length > 0)
        {
            ParseLines(markdownBody.Split('\n'), blocks, indent + 1);
        }
    }

    private static string CollectHtmlBlock(
        IReadOnlyList<string> lines,
        ref int index,
        string closingTag)
    {
        var html = new StringBuilder(lines[index]);
        while (!html.ToString().Contains(closingTag, StringComparison.OrdinalIgnoreCase)
               && index + 1 < lines.Count)
        {
            html.Append('\n').Append(lines[++index]);
        }
        return html.ToString();
    }

    private static string HtmlToMarkdown(string html)
    {
        var value = AnchorPattern().Replace(
            html,
            match => $"[{match.Groups["content"].Value}]({match.Groups["url"].Value})");
        value = Regex.Replace(value, @"<(strong|b)\b[^>]*>", "**", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</(strong|b)>", "**", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<(em|i)\b[^>]*>", "*", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</(em|i)>", "*", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<(s|strike|del)\b[^>]*>", "~~", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</(s|strike|del)>", "~~", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<code\b[^>]*>", "`", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</code>", "`", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</(p|div|li)>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(value)
            .Replace("\u00a0", " ", StringComparison.Ordinal);
    }

    private static string StripContainer(string html, string tag)
    {
        var value = Regex.Replace(html, $@"^\s*<{tag}\b[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        return Regex.Replace(value, $@"</{tag}>\s*$", string.Empty, RegexOptions.IgnoreCase);
    }

    private static string? ExtractLeadingIcon(ref string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var first = enumerator.GetTextElement();
        if (first.All(character => char.IsLetterOrDigit(character)))
        {
            return null;
        }

        value = value[first.Length..].TrimStart();
        return first;
    }

    private static bool LooksLikeTableRow(string value) =>
        value.Count(character => character == '|') >= 2;

    private static bool IsTableSeparator(string value)
    {
        if (!LooksLikeTableRow(value))
        {
            return false;
        }
        return SplitTableRow(value).All(cell => TableSeparatorCellPattern().IsMatch(cell));
    }

    private static IReadOnlyList<IReadOnlyList<WikiRichTextSpan>> ParseTableRow(string value)
    {
        var cells = new List<IReadOnlyList<WikiRichTextSpan>>();
        foreach (var cell in SplitTableRow(value))
        {
            var spans = ParseRichText(cell.Trim());
            cells.Add(spans);
        }
        return cells;
    }

    private static IReadOnlyList<string> SplitTableRow(string value) =>
        value.Trim().Trim('|').Split('|', StringSplitOptions.TrimEntries);

    private static int IndentWidth(string line)
    {
        var width = 0;
        foreach (var character in line)
        {
            if (character == ' ')
            {
                width++;
            }
            else if (character == '\t')
            {
                width += 4;
            }
            else
            {
                break;
            }
        }
        return width;
    }

    private static bool IsAttachmentUrl(string url)
    {
        if (url.StartsWith("/admin/sentinel/files/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var extension = Path.GetExtension(url.Split('?', '#')[0]);
        return extension.Length > 0
            && extension is not ".html" and not ".htm" and not ".md" and not ".markdown";
    }

    private static WikiBlock NewBlock(
        string type,
        int indent,
        IReadOnlyList<WikiRichTextSpan> richText,
        IReadOnlyDictionary<string, string>? props = null) =>
        new(
            Guid.NewGuid(),
            type,
            Math.Max(0, indent),
            richText,
            props ?? new Dictionary<string, string>());

    private readonly record struct InlineMarks(
        bool Bold = false,
        bool Italic = false,
        bool Strikethrough = false,
        bool Code = false,
        string? Link = null);

    [GeneratedRegex(@"^(?<marks>#{1,6})\s+(?<text>.+)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^[-*+]\s+\[(?<state>[ xX])\]\s*(?<text>.*)$")]
    private static partial Regex ToDoPattern();

    [GeneratedRegex(@"^[-*+]\s+(?<text>.+)$")]
    private static partial Regex BulletedPattern();

    [GeneratedRegex(@"^(?<number>\d+)[.)]\s+(?<text>.+)$")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<url>\S+?)(?:\s+""[^""]*"")?\)$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)$")]
    private static partial Regex StandaloneLinkPattern();

    [GeneratedRegex(@"\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex LinkAtPattern();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex TableSeparatorCellPattern();

    [GeneratedRegex(@"<summary\b[^>]*>(?<content>.*?)</summary>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SummaryPattern();

    [GeneratedRegex(@"<a\b[^>]*href\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>(?<content>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorPattern();
}
