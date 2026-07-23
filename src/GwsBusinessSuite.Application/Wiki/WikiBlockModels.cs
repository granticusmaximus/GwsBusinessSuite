using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

public static class WikiBlockTypes
{
    public const string Paragraph = "paragraph";
    public const string Heading1 = "heading_1";
    public const string Heading2 = "heading_2";
    public const string Heading3 = "heading_3";
    public const string BulletedListItem = "bulleted_list_item";
    public const string NumberedListItem = "numbered_list_item";
    public const string ToDo = "to_do";
    public const string Toggle = "toggle";
    public const string Quote = "quote";
    public const string Callout = "callout";
    public const string Code = "code";
    public const string Divider = "divider";
    public const string Image = "image";
    public const string Embed = "embed";
    // A reference to an existing Sentinel database. The database remains the single source
    // of truth; the block stores only its id and a display-title snapshot so pages can link
    // to the same database without copying schema or rows.
    public const string LinkedDatabase = "linked_database";
    public const string InlineDatabase = "inline_database";
    public const string Table = "table";
    public const string Equation = "equation";
    public const string Breadcrumb = "breadcrumb";
    public const string TableOfContents = "table_of_contents";
    public const string Button = "button";
    public const string SyncedBlock = "synced_block";
    public const string Columns = "columns";
    // Legacy content carried over from the old single-Markdown-string wiki by the one-time
    // backfill (WikiMarkdownBackfillService) - rendered through the existing Markdig
    // pipeline unchanged, so pre-existing pages keep their content verbatim rather than
    // losing it when BlocksJson supersedes the Markdown column.
    public const string Markdown = "markdown";

    public static readonly IReadOnlyList<string> All =
    [
        Paragraph, Heading1, Heading2, Heading3, BulletedListItem, NumberedListItem,
        ToDo, Toggle, Quote, Callout, Code, Divider, Image, Embed, LinkedDatabase, InlineDatabase,
        Table, Equation, Breadcrumb, TableOfContents, Button, SyncedBlock, Columns, Markdown
    ];

    public static bool IsListItem(string type) => type is BulletedListItem or NumberedListItem;
}

public sealed record WikiRichTextSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Strikethrough = false,
    bool Code = false,
    string? Link = null);

public sealed record WikiBlock(
    Guid Id,
    string Type,
    int IndentLevel,
    IReadOnlyList<WikiRichTextSpan> RichText,
    IReadOnlyDictionary<string, string> Props)
{
    [JsonIgnore]
    public string PlainText => string.Concat(RichText.Select(span => span.Text));
}

public static class WikiBlockJson
{
    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ToDoPattern = new(@"^[-*+]\s+\[([ xX])\]\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletedPattern = new(@"^[-*+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex NumberedPattern = new(@"^\d+[\.\)]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(@"^!\[([^\]]*)\]\((\S+?)(?:\s+""[^""]*"")?\)$", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex UnknownTagPattern = new(
        @"<unknown\b[^>]*\balt=""([^""]*)""[^>]*/>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IReadOnlyList<WikiBlock> ParseBlocks(string blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
        {
            return [];
        }

        try { return JsonSerializer.Deserialize<List<WikiBlock>>(blocksJson.Trim(), Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IReadOnlyList<WikiBlock> blocks) => JsonSerializer.Serialize(blocks, Options);

    public static WikiBlock CreateEmpty(string type) => new(
        Guid.NewGuid(), type, 0, [], new Dictionary<string, string>());

    public static IReadOnlyList<WikiBlock> FromLegacyMarkdown(string markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? []
            : [new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Markdown, 0, [],
                new Dictionary<string, string> { ["content"] = markdown })];

    // Notion's full-page Markdown endpoint is a recovery path when its structured block
    // endpoint returns an empty list. Convert the common Markdown shapes into the same native
    // editable blocks the Sentinel editor already understands instead of storing an opaque
    // legacy Markdown blob that the inline editor cannot edit.
    public static IReadOnlyList<WikiBlock> FromMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var blocks = new List<WikiBlock>();
        var paragraph = new List<string>();
        var code = new List<string>();
        string? fence = null;
        var codeLanguage = string.Empty;

        void FlushParagraph()
        {
            var text = CleanMarkdownText(string.Join("\n", paragraph));
            paragraph.Clear();
            if (text.Length > 0)
            {
                blocks.Add(TextBlock(WikiBlockTypes.Paragraph, text));
            }
        }

        void FlushCode()
        {
            blocks.Add(new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.Code,
                0,
                [new WikiRichTextSpan(string.Join("\n", code))],
                new Dictionary<string, string> { ["language"] = codeLanguage }));
            code.Clear();
            fence = null;
            codeLanguage = string.Empty;
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    FlushCode();
                }
                else
                {
                    code.Add(rawLine);
                }
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                fence = trimmed[..3];
                codeLanguage = trimmed[3..].Trim();
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var heading = HeadingPattern.Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                var level = Math.Min(heading.Groups[1].Value.Length, 3);
                var type = level switch
                {
                    1 => WikiBlockTypes.Heading1,
                    2 => WikiBlockTypes.Heading2,
                    _ => WikiBlockTypes.Heading3
                };
                blocks.Add(TextBlock(type, CleanMarkdownText(heading.Groups[2].Value)));
                continue;
            }

            var image = ImagePattern.Match(trimmed);
            if (image.Success)
            {
                FlushParagraph();
                blocks.Add(new WikiBlock(
                    Guid.NewGuid(),
                    WikiBlockTypes.Image,
                    0,
                    [new WikiRichTextSpan(CleanMarkdownText(image.Groups[1].Value))],
                    new Dictionary<string, string> { ["url"] = image.Groups[2].Value }));
                continue;
            }

            var toDo = ToDoPattern.Match(trimmed);
            if (toDo.Success)
            {
                FlushParagraph();
                blocks.Add(new WikiBlock(
                    Guid.NewGuid(),
                    WikiBlockTypes.ToDo,
                    0,
                    [new WikiRichTextSpan(CleanMarkdownText(toDo.Groups[2].Value))],
                    new Dictionary<string, string>
                    {
                        ["checked"] = toDo.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase)
                            ? "true"
                            : "false"
                    }));
                continue;
            }

            var bulleted = BulletedPattern.Match(trimmed);
            if (bulleted.Success)
            {
                FlushParagraph();
                blocks.Add(TextBlock(
                    WikiBlockTypes.BulletedListItem,
                    CleanMarkdownText(bulleted.Groups[1].Value)));
                continue;
            }

            var numbered = NumberedPattern.Match(trimmed);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(TextBlock(
                    WikiBlockTypes.NumberedListItem,
                    CleanMarkdownText(numbered.Groups[1].Value)));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(TextBlock(
                    WikiBlockTypes.Quote,
                    CleanMarkdownText(trimmed[1..].TrimStart())));
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(new WikiBlock(
                    Guid.NewGuid(),
                    WikiBlockTypes.Divider,
                    0,
                    [],
                    new Dictionary<string, string>()));
                continue;
            }

            paragraph.Add(rawLine);
        }

        if (fence is not null)
        {
            FlushCode();
        }
        FlushParagraph();
        return blocks;
    }

    private static WikiBlock TextBlock(string type, string text) =>
        new(
            Guid.NewGuid(),
            type,
            0,
            text.Length == 0 ? [] : [new WikiRichTextSpan(text)],
            new Dictionary<string, string>());

    private static string CleanMarkdownText(string value)
    {
        var unknownLabeled = UnknownTagPattern.Replace(value, match =>
            string.IsNullOrWhiteSpace(match.Groups[1].Value)
                ? "[Unsupported Notion content]"
                : $"[{match.Groups[1].Value}]");
        return WebUtility.HtmlDecode(HtmlTagPattern.Replace(unknownLabeled, string.Empty)).Trim();
    }
}
