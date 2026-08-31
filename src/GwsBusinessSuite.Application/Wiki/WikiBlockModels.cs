using System.Text.Json;
using System.Text.Json.Serialization;

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
    // "Web bookmark" - a link-preview card (or provider iframe for the handful of hosts
    // WikiEmbedResolver recognizes). Phase 5.4 split the video/audio/file/pdf cases that used
    // to be crammed into this one type (distinguished only by a Props["mediaKind"] string) out
    // into their own real block types below, each with its own icon/menu entry/renderer.
    public const string Embed = "embed";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string File = "file";
    public const string Pdf = "pdf";
    // A visual shortcut to another Sentinel page. The target remains the source of truth;
    // this block stores its id plus title/icon snapshots so the link can render immediately.
    public const string PageLink = "page_link";
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
    public const string Tab = "tab";
    // Legacy content carried over from the old single-Markdown-string wiki by the one-time
    // backfill (WikiMarkdownBackfillService) - rendered through the existing Markdig
    // pipeline unchanged, so pre-existing pages keep their content verbatim rather than
    // losing it when BlocksJson supersedes the Markdown column.
    public const string Markdown = "markdown";

    public static readonly IReadOnlyList<string> All =
    [
        Paragraph, Heading1, Heading2, Heading3, BulletedListItem, NumberedListItem,
        ToDo, Toggle, Quote, Callout, Code, Divider, Image, Embed, Video, Audio, File, Pdf,
        PageLink, LinkedDatabase, InlineDatabase,
        Table, Equation, Breadcrumb, TableOfContents, Button, SyncedBlock, Columns, Tab, Markdown
    ];

    public static bool IsListItem(string type) => type is BulletedListItem or NumberedListItem;
}

public sealed record WikiRichTextSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Strikethrough = false,
    bool Underline = false,
    bool Code = false,
    string? Link = null,
    string? TextColor = null,
    string? BackgroundColor = null);

// A named pane inside a Tab block. Tab blocks keep these in Props["tabsJson"] so each
// label travels with its rich-text content when tabs are reordered in the browser editor.
public sealed record WikiTab(
    string Title,
    IReadOnlyList<WikiRichTextSpan> RichText);

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

    public static WikiBlock CreatePageLink(Guid pageId, string title, string? icon = null)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var normalizedIcon = string.IsNullOrWhiteSpace(icon) ? "📄" : icon.Trim();
        return new WikiBlock(
            Guid.NewGuid(),
            WikiBlockTypes.PageLink,
            0,
            [new WikiRichTextSpan(normalizedTitle, Link: $"wikilink:{pageId}")],
            new Dictionary<string, string>
            {
                ["pageId"] = pageId.ToString(),
                ["pageTitle"] = normalizedTitle,
                ["pageIcon"] = normalizedIcon
            });
    }

    public static WikiBlock CreateEmpty(string type) => new(
        Guid.NewGuid(), type, 0, [], new Dictionary<string, string>());

    public static IReadOnlyList<WikiBlock> FromLegacyMarkdown(string markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? []
            : [new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Markdown, 0, [],
                new Dictionary<string, string> { ["content"] = markdown })];

    // Notion's API recovery endpoint and workspace ZIP exports both use Markdown, with HTML
    // mixed in for blocks such as callouts and toggles. Keep the conversion in one mapper so
    // both import paths produce the same native, round-trip-editable block structure.
    public static IReadOnlyList<WikiBlock> FromMarkdown(string markdown, string? pageTitle = null) =>
        NotionMarkdownBlockParser.Parse(markdown, pageTitle);
}
