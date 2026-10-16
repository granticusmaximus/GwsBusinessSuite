using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Whiteboard blocks (Sentinel item 10). A `canvas` block keeps its board in props["doc"]:
// { "v": 1, "items": [...], "view": { "x", "y", "zoom" } }, drawn by wwwroot/js/sentinel-canvas.js.
// A page whose only block is a canvas is shown as a full-page whiteboard. Every save goes through
// NormalizeBlocks: the board is re-serialized from known fields only (anything else is dropped),
// checked against the limits, and the block's rich text is rebuilt from the board's text and page
// cards - so search, tags, backlinks ("wikilink:" spans), Ask and export read a whiteboard like
// any other block.
public static partial class SentinelCanvas
{
    public const int MaxDocLength = 1_500_000;
    public const int MaxItems = 2000;
    public const int MaxTextLength = 5000;
    public const int MaxPenPoints = 5000;
    private const double MaxCoordinate = 1_000_000;

    public static readonly IReadOnlySet<string> ItemTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "sticky", "text", "rect", "ellipse", "diamond", "frame", "card", "pen", "connector"
    };

    public static bool IsCanvasPage(IReadOnlyList<WikiBlock> blocks) =>
        blocks.Count == 1 && blocks[0].Type == WikiBlockTypes.Canvas;

    public static WikiBlock NewBlock() =>
        new(Guid.NewGuid(), WikiBlockTypes.Canvas, 0, [], new Dictionary<string, string> { ["doc"] = EmptyDoc });

    public const string EmptyDoc = """{"v":1,"items":[],"view":{"x":0,"y":0,"zoom":1}}""";

    // Fast path: blocks JSON without a canvas block is returned as it is.
    public static string NormalizeBlocksJson(string blocksJson)
    {
        if (string.IsNullOrEmpty(blocksJson) || !blocksJson.Contains("\"canvas\"", StringComparison.Ordinal)) return blocksJson;
        var blocks = WikiBlockJson.ParseBlocks(blocksJson);
        if (!blocks.Any(block => block.Type == WikiBlockTypes.Canvas)) return blocksJson;
        return WikiBlockJson.Serialize(blocks.Select(Normalize).ToList());
    }

    public static WikiBlock Normalize(WikiBlock block)
    {
        if (block.Type != WikiBlockTypes.Canvas) return block;
        var doc = NormalizeDoc(block.Props.GetValueOrDefault("doc"));
        var props = new Dictionary<string, string>(block.Props) { ["doc"] = doc };
        if (props.TryGetValue("height", out var height)
            && (!int.TryParse(height, NumberStyles.Integer, CultureInfo.InvariantCulture, out var px) || px is < 200 or > 4000))
            props.Remove("height");
        return block with { Props = props, RichText = RichText(doc) };
    }

    // Throws InvalidOperationException for a board over the limits; malformed JSON becomes an
    // empty board only when it was empty to begin with (a corrupt non-empty board is refused, so
    // a bad client can't silently wipe someone's whiteboard).
    public static string NormalizeDoc(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyDoc;
        if (json.Length > MaxDocLength) throw new InvalidOperationException("This whiteboard is too large to save (over 1.5 MB) - split it into several boards.");
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException("The whiteboard couldn't be read, so it wasn't saved."); }
        if (root is not JsonObject docObject) throw new InvalidOperationException("The whiteboard couldn't be read, so it wasn't saved.");

        var items = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (docObject["items"] is JsonArray rawItems)
        {
            if (rawItems.Count > MaxItems) throw new InvalidOperationException($"A whiteboard can hold up to {MaxItems} items.");
            foreach (var raw in rawItems.OfType<JsonObject>())
            {
                if (CleanItem(raw) is { } item && ids.Add(item["id"]!.GetValue<string>())) items.Add(item);
            }
        }
        // A connector pointing at an item that's gone keeps its last free position instead.
        foreach (var connector in items.OfType<JsonObject>().Where(item => Str(item, "type") == "connector"))
        {
            foreach (var end in new[] { "from", "to" })
            {
                if (Str(connector, end) is { } target && !ids.Contains(target)) connector.Remove(end);
            }
        }

        var view = docObject["view"] as JsonObject;
        var clean = new JsonObject
        {
            ["v"] = 1,
            ["items"] = items,
            ["view"] = new JsonObject
            {
                ["x"] = Coordinate(view, "x"),
                ["y"] = Coordinate(view, "y"),
                ["zoom"] = Math.Clamp(Number(view, "zoom") ?? 1, 0.1, 4)
            }
        };
        return clean.ToJsonString();
    }

    // The board's words in reading order (frames first, then top-to-bottom, left-to-right), one
    // line per item; page cards become wikilink spans.
    public static IReadOnlyList<WikiRichTextSpan> RichText(string doc)
    {
        var spans = new List<WikiRichTextSpan>();
        using var parsed = JsonDocument.Parse(doc);
        var items = parsed.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                Type: item.GetProperty("type").GetString()!,
                Y: item.GetProperty("y").GetDouble(),
                X: item.GetProperty("x").GetDouble(),
                Item: item))
            .OrderBy(entry => entry.Type == "frame" ? 0 : 1).ThenBy(entry => entry.Y).ThenBy(entry => entry.X);
        foreach (var (type, _, _, item) in items)
        {
            WikiRichTextSpan? span = type switch
            {
                "card" when item.TryGetProperty("pageId", out var pageId) =>
                    new WikiRichTextSpan(item.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t ? t : "Untitled", Link: $"wikilink:{pageId.GetString()}"),
                _ when item.TryGetProperty("text", out var text) && text.GetString() is { } value && value.Trim().Length > 0 =>
                    new WikiRichTextSpan(value.Trim()),
                _ => null
            };
            if (span is null) continue;
            if (spans.Count > 0) spans.Add(new WikiRichTextSpan("\n"));
            spans.Add(span);
        }
        return spans;
    }

    // Page ids the board's cards point at.
    public static IReadOnlyList<Guid> PageReferences(string doc)
    {
        using var parsed = JsonDocument.Parse(doc);
        return parsed.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "card" && item.TryGetProperty("pageId", out _))
            .Select(item => Guid.Parse(item.GetProperty("pageId").GetString()!))
            .Distinct().ToList();
    }

    // For read-only renders shown to people who may not see the linked pages (public shares):
    // cards keep their place but lose their stored titles.
    public static string WithoutCardTitles(string doc)
    {
        var root = JsonNode.Parse(doc)!.AsObject();
        foreach (var card in root["items"]!.AsArray().OfType<JsonObject>().Where(item => Str(item, "type") == "card"))
        {
            card.Remove("title");
            card.Remove("icon");
        }
        return root.ToJsonString();
    }

    private static JsonObject? CleanItem(JsonObject raw)
    {
        var type = Str(raw, "type");
        var id = Str(raw, "id");
        if (type is null || !ItemTypes.Contains(type) || id is null || !ItemId().IsMatch(id)) return null;
        var item = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["x"] = Coordinate(raw, "x"),
            ["y"] = Coordinate(raw, "y"),
            ["w"] = Math.Clamp(Number(raw, "w") ?? 0, 0, MaxCoordinate),
            ["h"] = Math.Clamp(Number(raw, "h") ?? 0, 0, MaxCoordinate),
            ["z"] = Math.Clamp(Number(raw, "z") ?? 0, -1_000_000, 1_000_000)
        };
        if (Str(raw, "color") is { } color && Color().IsMatch(color)) item["color"] = color;
        if (Str(raw, "text") is { } text) item["text"] = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        switch (type)
        {
            case "card":
                if (!Guid.TryParse(Str(raw, "pageId"), out var pageId)) return null;
                item["pageId"] = pageId.ToString();
                if (Str(raw, "title") is { } title) item["title"] = title.Length > 200 ? title[..200] : title;
                if (Str(raw, "icon") is { } icon && icon.Length <= 16) item["icon"] = icon;
                break;
            case "pen":
                if (raw["points"] is not JsonArray points || points.Count is < 1 or > MaxPenPoints) return null;
                var cleanPoints = new JsonArray();
                foreach (var point in points.OfType<JsonArray>().Where(point => point.Count == 2))
                {
                    var px = AsNumber(point[0]);
                    var py = AsNumber(point[1]);
                    if (px is null || py is null) continue;
                    cleanPoints.Add(new JsonArray(Math.Round(Math.Clamp(px.Value, -MaxCoordinate, MaxCoordinate), 1), Math.Round(Math.Clamp(py.Value, -MaxCoordinate, MaxCoordinate), 1)));
                }
                if (cleanPoints.Count == 0) return null;
                item["points"] = cleanPoints;
                item["stroke"] = Math.Clamp(Number(raw, "stroke") ?? 2, 1, 24);
                break;
            case "connector":
                if (Str(raw, "from") is { } from && ItemId().IsMatch(from)) item["from"] = from;
                if (Str(raw, "to") is { } to && ItemId().IsMatch(to)) item["to"] = to;
                item["x2"] = Coordinate(raw, "x2");
                item["y2"] = Coordinate(raw, "y2");
                item["arrow"] = raw["arrow"] is JsonValue arrow && arrow.TryGetValue<bool>(out var hasArrow) ? hasArrow : true;
                break;
        }
        return item;
    }

    private static string? Str(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonObject? node, string name) => AsNumber(node?[name]);

    private static double? AsNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;

    private static double Coordinate(JsonObject? node, string name) =>
        Math.Round(Math.Clamp(Number(node, name) ?? 0, -MaxCoordinate, MaxCoordinate), 2);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ItemId();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Color();
}
