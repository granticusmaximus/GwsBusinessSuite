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
    public const string Embed = "embed";
    // Legacy content carried over from the old single-Markdown-string wiki by the one-time
    // backfill (WikiMarkdownBackfillService) - rendered through the existing Markdig
    // pipeline unchanged, so pre-existing pages keep their content verbatim rather than
    // losing it when BlocksJson supersedes the Markdown column.
    public const string Markdown = "markdown";

    public static readonly IReadOnlyList<string> All =
    [
        Paragraph, Heading1, Heading2, Heading3, BulletedListItem, NumberedListItem,
        ToDo, Toggle, Quote, Callout, Code, Divider, Image, Embed, Markdown
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
}
