using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Pure JSON->model mapping for Notion's public API shapes (blocks, rich text, database
// properties/values) onto this app's own WikiBlock/WikiDatabaseProperty vocabulary. No HTTP,
// no DB - NotionService fetches the raw JsonElements, NotionSyncService drives the walk and
// does the reconciliation; this class only ever answers "given this one Notion object, what's
// the GWS equivalent". Deliberately lossy on Notion's long tail - see
// docs/WIKI_NOTION_CLONE.md's Phase 3 section for what's dropped and why.
public static class NotionMapping
{
    // Notion block types that are pure layout wrappers with no GWS equivalent - their own
    // node produces no WikiBlock, but the walk still recurses into their children (imported
    // in place, one after another, not side-by-side).
    private static readonly HashSet<string> FlattenedWrapperTypes = ["column_list", "column", "synced_block", "tab", "template"];

    // Consumed by the page-tree walk (become their own WikiPage/WikiDatabase) - never
    // inlined as content when walking a page's blocks.
    private static readonly HashSet<string> PageTreeBlockTypes = ["child_page", "child_database"];

    public static bool IsFlattenedWrapper(string notionBlockType) => FlattenedWrapperTypes.Contains(notionBlockType);

    public static bool IsPageTreeBlock(string notionBlockType) => PageTreeBlockTypes.Contains(notionBlockType);

    public static IReadOnlyList<WikiRichTextSpan> MapRichText(JsonElement richTextArray)
    {
        if (richTextArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var spans = new List<WikiRichTextSpan>();
        foreach (var span in richTextArray.EnumerateArray())
        {
            // Every rich-text span - regardless of its own "type" (text/mention/equation) -
            // always carries a plain_text fallback, so we read that uniformly instead of
            // branching on span type.
            var text = span.TryGetProperty("plain_text", out var plainTextElement) ? plainTextElement.GetString() ?? string.Empty : string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            var bold = false;
            var italic = false;
            var strikethrough = false;
            var code = false;
            if (span.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
            {
                bold = annotations.TryGetProperty("bold", out var b) && b.ValueKind == JsonValueKind.True;
                italic = annotations.TryGetProperty("italic", out var i) && i.ValueKind == JsonValueKind.True;
                strikethrough = annotations.TryGetProperty("strikethrough", out var s) && s.ValueKind == JsonValueKind.True;
                code = annotations.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.True;
                // annotations.underline/color have no GWS equivalent and are dropped.
            }

            var link = span.TryGetProperty("href", out var hrefElement) && hrefElement.ValueKind == JsonValueKind.String
                ? hrefElement.GetString()
                : null;

            spans.Add(new WikiRichTextSpan(text, bold, italic, strikethrough, code, link));
        }

        return spans;
    }

    // Maps a single non-table, non-wrapper, non-page-tree Notion block to a WikiBlock. Returns
    // null for wrapper/page-tree/unsupported types - callers should check IsFlattenedWrapper /
    // IsPageTreeBlock before calling this, and treat a null return as "skip, nothing to add".
    public static WikiBlock? MapBlock(JsonElement block, int indentLevel, Action<string>? onUnsupported = null)
    {
        var type = block.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        if (type.Length == 0 || IsFlattenedWrapper(type) || IsPageTreeBlock(type))
        {
            return null;
        }

        if (!block.TryGetProperty(type, out var body))
        {
            body = default;
        }

        var richText = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("rich_text", out var rt)
            ? MapRichText(rt)
            : [];
        if (richText.Count == 0 && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("caption", out var caption))
        {
            richText = MapRichText(caption);
        }

        switch (type)
        {
            case "paragraph":
                return NewBlock(WikiBlockTypes.Paragraph, indentLevel, richText);
            case "heading_1":
                return NewBlock(WikiBlockTypes.Heading1, indentLevel, richText);
            case "heading_2":
                return NewBlock(WikiBlockTypes.Heading2, indentLevel, richText);
            case "heading_3":
                return NewBlock(WikiBlockTypes.Heading3, indentLevel, richText);
            case "bulleted_list_item":
                return NewBlock(WikiBlockTypes.BulletedListItem, indentLevel, richText);
            case "numbered_list_item":
                return NewBlock(WikiBlockTypes.NumberedListItem, indentLevel, richText);
            case "to_do":
                var isChecked = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("checked", out var checkedElement) && checkedElement.ValueKind == JsonValueKind.True;
                return NewBlock(WikiBlockTypes.ToDo, indentLevel, richText, new Dictionary<string, string> { ["checked"] = isChecked ? "true" : "false" });
            case "toggle":
                return NewBlock(WikiBlockTypes.Toggle, indentLevel, richText);
            case "quote":
                return NewBlock(WikiBlockTypes.Quote, indentLevel, richText);
            case "callout":
                var icon = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("icon", out var iconElement)
                    && iconElement.ValueKind == JsonValueKind.Object
                    && iconElement.TryGetProperty("emoji", out var emojiElement)
                    ? emojiElement.GetString() ?? "💡"
                    : "💡";
                return NewBlock(WikiBlockTypes.Callout, indentLevel, richText, new Dictionary<string, string> { ["icon"] = icon });
            case "code":
                var language = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("language", out var languageElement)
                    ? languageElement.GetString() ?? string.Empty
                    : string.Empty;
                return NewBlock(WikiBlockTypes.Code, indentLevel, richText, new Dictionary<string, string> { ["language"] = language });
            case "divider":
                return NewBlock(WikiBlockTypes.Divider, indentLevel, []);
            case "image":
                return NewBlock(WikiBlockTypes.Image, indentLevel, richText, FileProps(block, body));
            case "video":
            case "audio":
            case "file":
            case "pdf":
                return NewBlock(WikiBlockTypes.Embed, indentLevel, richText, FileProps(block, body));
            case "embed":
            case "bookmark":
            case "link_preview":
                var url = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                return NewBlock(WikiBlockTypes.Embed, indentLevel, richText, new Dictionary<string, string> { ["url"] = url });
            case "equation":
                var expression = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("expression", out var expressionElement)
                    ? expressionElement.GetString() ?? string.Empty
                    : string.Empty;
                return NewBlock(WikiBlockTypes.Equation, indentLevel, [new WikiRichTextSpan(expression)]);
            case "breadcrumb":
                return NewBlock(WikiBlockTypes.Breadcrumb, indentLevel, []);
            case "table_of_contents":
                return NewBlock(WikiBlockTypes.TableOfContents, indentLevel, []);
            case "synced_block":
                return NewBlock(WikiBlockTypes.SyncedBlock, indentLevel, richText);
            default:
                if (type.StartsWith("heading_", StringComparison.Ordinal))
                {
                    return NewBlock(WikiBlockTypes.Heading3, indentLevel, richText);
                }
                onUnsupported?.Invoke(type);
                return null;
        }
    }

    // table + its table_row children collapse into a single native table block holding a
    // pipe-delimited grid - table itself carries no rich text, so it can't map
    // through MapBlock alone. Cell rich text is flattened to plain text (no bold/italic
    // preserved inside table cells) - a documented simplification, not a bug.
    public static WikiBlock MapTable(JsonElement tableBlock, IReadOnlyList<JsonElement> rowBlocks, int indentLevel)
    {
        var hasColumnHeader = tableBlock.TryGetProperty("table", out var tableBody)
            && tableBody.TryGetProperty("has_column_header", out var headerElement)
            && headerElement.ValueKind == JsonValueKind.True;

        var rows = rowBlocks
            .Select(rowBlock => rowBlock.TryGetProperty("table_row", out var rowBody) && rowBody.TryGetProperty("cells", out var cellsElement) && cellsElement.ValueKind == JsonValueKind.Array
                ? cellsElement.EnumerateArray().Select(cell => string.Concat(MapRichText(cell).Select(span => span.Text))).ToList()
                : [])
            .ToList();

        var markdown = new StringBuilder();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            markdown.Append("| ").Append(string.Join(" | ", rows[rowIndex].Select(EscapePipeCell))).Append(" |\n");
            if (rowIndex == 0 && hasColumnHeader)
            {
                markdown.Append("| ").Append(string.Join(" | ", rows[rowIndex].Select(_ => "---"))).Append(" |\n");
            }
        }

        return new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Table, indentLevel,
            [new WikiRichTextSpan(markdown.ToString())], new Dictionary<string, string>());
    }

    private static string EscapePipeCell(string cell) => cell.Replace("|", "\\|").Replace("\n", " ");

    private static string ExtractFileUrl(JsonElement fileHolder)
    {
        if (fileHolder.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        // "file" (Notion-hosted, presigned, expires ~1hr after this API call) and "external"
        // (a real permanent URL) both nest the url the same way - see the known limitation
        // documented in docs/WIKI_NOTION_CLONE.md.
        if (fileHolder.TryGetProperty("file", out var file) && file.TryGetProperty("url", out var fileUrl))
        {
            return fileUrl.GetString() ?? string.Empty;
        }
        if (fileHolder.TryGetProperty("external", out var external) && external.TryGetProperty("url", out var externalUrl))
        {
            return externalUrl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> FileProps(JsonElement block, JsonElement body)
    {
        var props = new Dictionary<string, string>
        {
            ["url"] = ExtractFileUrl(body)
        };
        if (block.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } blockId)
        {
            props["notionBlockId"] = blockId;
        }
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("type", out var sourceType)
            && sourceType.GetString() is { Length: > 0 } type)
        {
            props["notionSourceType"] = type;
        }
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("name", out var name)
            && name.GetString() is { Length: > 0 } fileName)
        {
            props["fileName"] = fileName;
        }
        return props;
    }

    private static WikiBlock NewBlock(string type, int indentLevel, IReadOnlyList<WikiRichTextSpan> richText, IReadOnlyDictionary<string, string>? props = null) =>
        new(Guid.NewGuid(), type, indentLevel, richText, props ?? new Dictionary<string, string>());

    public static IReadOnlyList<object> MapBlocksForWrite(IReadOnlyList<WikiBlock> blocks) =>
        blocks.Select(MapBlockForWrite).Where(block => block is not null).Cast<object>().ToList();

    private static object? MapBlockForWrite(WikiBlock block)
    {
        var richText = block.RichText.Select(span => (object)new
        {
            type = "text",
            text = new { content = span.Text, link = string.IsNullOrWhiteSpace(span.Link) ? null : new { url = span.Link } },
            annotations = new { bold = span.Bold, italic = span.Italic, strikethrough = span.Strikethrough, underline = false, code = span.Code, color = "default" }
        }).ToList();

        return block.Type switch
        {
            WikiBlockTypes.Paragraph => new { @object = "block", type = "paragraph", paragraph = new { rich_text = richText } },
            WikiBlockTypes.Heading1 => new { @object = "block", type = "heading_1", heading_1 = new { rich_text = richText } },
            WikiBlockTypes.Heading2 => new { @object = "block", type = "heading_2", heading_2 = new { rich_text = richText } },
            WikiBlockTypes.Heading3 => new { @object = "block", type = "heading_3", heading_3 = new { rich_text = richText } },
            WikiBlockTypes.BulletedListItem => new { @object = "block", type = "bulleted_list_item", bulleted_list_item = new { rich_text = richText } },
            WikiBlockTypes.NumberedListItem => new { @object = "block", type = "numbered_list_item", numbered_list_item = new { rich_text = richText } },
            WikiBlockTypes.ToDo => new { @object = "block", type = "to_do", to_do = new { rich_text = richText, @checked = block.Props.TryGetValue("checked", out var value) && value == "true" } },
            WikiBlockTypes.Toggle => new { @object = "block", type = "toggle", toggle = new { rich_text = richText } },
            WikiBlockTypes.Quote => new { @object = "block", type = "quote", quote = new { rich_text = richText } },
            WikiBlockTypes.Callout => new { @object = "block", type = "callout", callout = new { rich_text = richText, icon = new { type = "emoji", emoji = block.Props.GetValueOrDefault("icon", "💡") } } },
            WikiBlockTypes.Code => new { @object = "block", type = "code", code = new { rich_text = richText, language = block.Props.GetValueOrDefault("language", "plain text") } },
            WikiBlockTypes.Divider => new { @object = "block", type = "divider", divider = new { } },
            WikiBlockTypes.Image when block.Props.TryGetValue("url", out var imageUrl) => new { @object = "block", type = "image", image = new { type = "external", external = new { url = imageUrl } } },
            WikiBlockTypes.Embed when block.Props.TryGetValue("url", out var embedUrl) => new { @object = "block", type = "bookmark", bookmark = new { url = embedUrl } },
            WikiBlockTypes.Equation => new { @object = "block", type = "equation", equation = new { expression = block.PlainText } },
            WikiBlockTypes.TableOfContents => new { @object = "block", type = "table_of_contents", table_of_contents = new { } },
            WikiBlockTypes.Breadcrumb => new { @object = "block", type = "breadcrumb", breadcrumb = new { } },
            _ when block.PlainText.Length > 0 => new { @object = "block", type = "paragraph", paragraph = new { rich_text = richText } },
            _ => null
        };
    }

    // ---- Database property schema + values ----

    public static string MapPropertyType(string notionPropertyType) => notionPropertyType switch
    {
        "title" => WikiDatabasePropertyTypes.Title,
        "rich_text" => WikiDatabasePropertyTypes.Text,
        "number" => WikiDatabasePropertyTypes.Number,
        "select" => WikiDatabasePropertyTypes.Select,
        // "One choice from an option list" - same semantics as select.
        "status" => WikiDatabasePropertyTypes.Select,
        "multi_select" => WikiDatabasePropertyTypes.MultiSelect,
        "date" => WikiDatabasePropertyTypes.Date,
        "checkbox" => WikiDatabasePropertyTypes.Checkbox,
        "url" => WikiDatabasePropertyTypes.Url,
        // Holds Notion's own original timestamp - intentionally NOT this app's own
        // WikiDatabasePropertyTypes.CreatedTime, which is backed by the local row's own
        // CreatedAt and would show the wrong date if conflated with Notion's.
        "created_time" => WikiDatabasePropertyTypes.Date,
        // Best-effort string rendering - see NotionSyncServiceTests for the exact per-type
        // fallback text shape.
        _ => WikiDatabasePropertyTypes.Text
    };

    private static readonly Dictionary<string, string> NotionColorToHex = new()
    {
        ["default"] = "#e5e7eb",
        ["gray"] = "#9ca3af",
        ["brown"] = "#92400e",
        ["orange"] = "#f97316",
        ["yellow"] = "#eab308",
        ["green"] = "#22c55e",
        ["blue"] = "#3b82f6",
        ["purple"] = "#a855f7",
        ["pink"] = "#ec4899",
        ["red"] = "#ef4444"
    };

    // Reads a select/status/multi_select schema's "options" array - the property id and each
    // option's own id are preserved verbatim, so row values (which reference option ids) keep
    // resolving correctly against the locally-stored options after import.
    public static IReadOnlyList<WikiDatabasePropertyOption> MapPropertyOptions(JsonElement propertySchema, string notionPropertyType)
    {
        if (notionPropertyType is not ("select" or "status" or "multi_select"))
        {
            return [];
        }

        if (!propertySchema.TryGetProperty(notionPropertyType, out var typeBody) || !typeBody.TryGetProperty("options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return optionsElement.EnumerateArray()
            .Select(option => new WikiDatabasePropertyOption(
                option.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty,
                option.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                option.TryGetProperty("color", out var colorElement) && NotionColorToHex.TryGetValue(colorElement.GetString() ?? string.Empty, out var hex)
                    ? hex
                    : NotionColorToHex["default"]))
            .Where(option => option.Id.Length > 0)
            .ToList();
    }

    // Writes one Notion property value (a {"type":"...", "<type>":...} object, e.g. from a
    // page's "properties" or a database row's "properties") into a WikiPropertyValues object
    // for the given local property. localPropertyType is the already-mapped type (via
    // MapPropertyType) of the target GWS property, since that determines which typed setter
    // applies - independent from whatever Notion happens to label the source value.
    public static void ApplyPropertyValue(System.Text.Json.Nodes.JsonObject values, Guid localPropertyId, string localPropertyType, JsonElement notionPropertyValue)
    {
        var notionType = notionPropertyValue.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        if (!notionPropertyValue.TryGetProperty(notionType, out var body))
        {
            body = default;
        }

        switch (localPropertyType)
        {
            case WikiDatabasePropertyTypes.Number:
                WikiPropertyValues.SetNumber(values, localPropertyId, body.ValueKind == JsonValueKind.Number ? body.GetDecimal() : null);
                return;
            case WikiDatabasePropertyTypes.Checkbox:
                WikiPropertyValues.SetCheckbox(values, localPropertyId, body.ValueKind is JsonValueKind.True or JsonValueKind.False && body.GetBoolean());
                return;
            case WikiDatabasePropertyTypes.Date:
                WikiPropertyValues.SetDate(values, localPropertyId, ExtractDate(notionType, body));
                return;
            case WikiDatabasePropertyTypes.Select:
                WikiPropertyValues.SetText(values, localPropertyId, ExtractOptionId(body));
                return;
            case WikiDatabasePropertyTypes.MultiSelect:
                WikiPropertyValues.SetMultiSelect(values, localPropertyId, ExtractOptionIds(body));
                return;
            default:
                WikiPropertyValues.SetText(values, localPropertyId, ExtractTextValue(notionType, body));
                return;
        }
    }

    private static DateTimeOffset? ExtractDate(string notionType, JsonElement body)
    {
        if (notionType == "created_time" || notionType == "last_edited_time")
        {
            return body.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(body.GetString(), out var timestamp) ? timestamp : null;
        }

        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("start", out var startElement) && startElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(startElement.GetString(), out var start))
        {
            return start;
        }

        return null;
    }

    private static string? ExtractOptionId(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

    private static IReadOnlyList<string> ExtractOptionIds(JsonElement body) =>
        body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().Select(ExtractOptionId).Where(id => id is not null).Select(id => id!).ToList()
            : [];

    // Best-effort plain-text rendering for every property type that maps to WikiDatabasePropertyTypes.Text
    // (title/rich_text/url/email/phone_number/people/files/unique_id/created_by/last_edited_by/
    // last_edited_time/place/formula/rollup/relation).
    private static string ExtractTextValue(string notionType, JsonElement body) => notionType switch
    {
        "title" or "rich_text" => string.Concat(MapRichText(body).Select(span => span.Text)),
        "url" or "email" or "phone_number" => body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty,
        "last_edited_time" => body.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(body.GetString(), out var timestamp)
            ? timestamp.ToString("O")
            : string.Empty,
        "people" => body.ValueKind == JsonValueKind.Array
            ? string.Join(", ", body.EnumerateArray().Select(p => p.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty).Where(n => n.Length > 0))
            : string.Empty,
        "files" => body.ValueKind == JsonValueKind.Array
            ? string.Join(", ", body.EnumerateArray().Select(f => f.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty).Where(n => n.Length > 0))
            : string.Empty,
        "unique_id" => body.ValueKind == JsonValueKind.Object
            ? $"{(body.TryGetProperty("prefix", out var prefix) ? prefix.GetString() : null)}{(body.TryGetProperty("number", out var number) ? number.ToString() : string.Empty)}"
            : string.Empty,
        "created_by" or "last_edited_by" => body.ValueKind == JsonValueKind.Object
            ? (body.TryGetProperty("name", out var name) ? name.GetString() : null) ?? (body.TryGetProperty("id", out var id) ? id.GetString() : null) ?? string.Empty
            : string.Empty,
        "formula" => ExtractFormulaValue(body),
        "rollup" => ExtractRollupValue(body),
        "relation" => body.ValueKind == JsonValueKind.Array
            ? string.Join(", ", body.EnumerateArray().Select(r => r.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty))
            : string.Empty,
        _ => body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty
    };

    private static string ExtractFormulaValue(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("type", out var typeElement))
        {
            return string.Empty;
        }

        var formulaType = typeElement.GetString() ?? string.Empty;
        return formulaType switch
        {
            "string" => body.TryGetProperty("string", out var s) ? s.GetString() ?? string.Empty : string.Empty,
            "number" => body.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.ToString() : string.Empty,
            "boolean" => body.TryGetProperty("boolean", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean().ToString() : string.Empty,
            "date" => body.TryGetProperty("date", out var d) ? ExtractDate("date", d)?.ToString("O") ?? string.Empty : string.Empty,
            _ => string.Empty
        };
    }

    private static string ExtractRollupValue(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("type", out var typeElement))
        {
            return string.Empty;
        }

        var rollupType = typeElement.GetString() ?? string.Empty;
        return rollupType switch
        {
            "number" => body.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.ToString() : string.Empty,
            "date" => body.TryGetProperty("date", out var d) ? ExtractDate("date", d)?.ToString("O") ?? string.Empty : string.Empty,
            "array" => body.TryGetProperty("array", out var array) && array.ValueKind == JsonValueKind.Array
                ? string.Join(", ", array.EnumerateArray().Select(item => item.TryGetProperty("type", out var itemType) && itemType.GetString() == "formula"
                    ? ExtractFormulaValue(item.TryGetProperty("formula", out var formula) ? formula : item)
                    : string.Empty).Where(text => text.Length > 0))
                : string.Empty,
            _ => string.Empty
        };
    }
}
