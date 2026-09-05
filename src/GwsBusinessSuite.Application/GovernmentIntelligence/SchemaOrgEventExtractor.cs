using System.Globalization;
using System.Text.Json;

namespace GwsBusinessSuite.Application.GovernmentIntelligence;

// One event pulled out of a page's structured data, flattened into the shape a workflow node can
// hand straight to database.addRow without further mapping.
public sealed record ExtractedEvent(
    string Title,
    string Url,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    string Venue,
    string City,
    double? Latitude,
    double? Longitude,
    double? MilesFromHome,
    bool IsOnline,
    string? ImageUrl,
    string? Description);

// Pulls schema.org Event objects out of a fetched page.
//
// Sites publish this markup deliberately, for search engines - it is the one machine-readable
// contract a public events page tends to honour, and it survives redesigns that break any
// CSS-selector scraper. Two carriers are supported because real sites use both:
//
//   1. <script type="application/ld+json"> - the standard, used by most WordPress/venue sites.
//   2. window.__SERVER_DATA__ = {...}      - React apps (Eventbrite) that embed the same
//                                            schema.org objects in their server state blob.
//
// Everything here is pure string/JSON work: no browser, no network, no API key. That is what
// lets it run inside an automation node against any URL the user points it at.
public static class SchemaOrgEventExtractor
{
    private const string OnlineAttendanceMode = "OnlineEventAttendanceMode";

    private static readonly string[] StateBlobMarkers =
    [
        "window.__SERVER_DATA__",
        "window.__NEXT_DATA__",
        "window.__INITIAL_STATE__",
    ];

    public static IReadOnlyList<ExtractedEvent> Extract(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var events = new List<ExtractedEvent>();
        foreach (var blob in EnumerateJsonBlobs(html))
        {
            try
            {
                using var document = JsonDocument.Parse(blob);
                Collect(document.RootElement, events);
            }
            catch (JsonException)
            {
                // A page can carry several blobs and only some be well-formed; a bad one must not
                // lose the rest.
            }
        }

        // The same event routinely appears in both an ItemList and a standalone block on one page.
        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Url) ? e.Title : e.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.StartAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    private static IEnumerable<string> EnumerateJsonBlobs(string html)
    {
        foreach (var blob in EnumerateLdJson(html)) yield return blob;

        foreach (var marker in StateBlobMarkers)
        {
            var blob = ExtractBalancedObject(html, marker);
            if (blob is not null) yield return blob;
        }
    }

    private static IEnumerable<string> EnumerateLdJson(string html)
    {
        var cursor = 0;
        while (true)
        {
            var tagStart = html.IndexOf("application/ld+json", cursor, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) yield break;

            var contentStart = html.IndexOf('>', tagStart);
            if (contentStart < 0) yield break;

            var contentEnd = html.IndexOf("</script", contentStart, StringComparison.OrdinalIgnoreCase);
            if (contentEnd < 0) yield break;

            yield return html[(contentStart + 1)..contentEnd].Trim();
            cursor = contentEnd;
        }
    }

    // Scans a balanced JSON object after a marker. A lazy regex to the next "};" is wrong twice
    // over: other script follows in the same tag, and descriptions contain braces and escaped
    // quotes of their own - so string state has to be tracked.
    public static string? ExtractBalancedObject(string html, string marker)
    {
        var markerAt = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt < 0) return null;

        var start = html.IndexOf('{', markerAt);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return html[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    // Walks the whole document rather than following a known path: the Event objects sit at
    // different depths on different sites (and inside ItemList wrappers on some), and a path that
    // a layout change breaks would fail silently rather than loudly.
    private static void Collect(JsonElement node, List<ExtractedEvent> sink)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsEventType(node) && Map(node) is { } mapped) sink.Add(mapped);
                foreach (var property in node.EnumerateObject()) Collect(property.Value, sink);
                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) Collect(item, sink);
                break;
        }
    }

    // @type is a string on most sites but an array on a few ("@type": ["Event", "SocialEvent"]),
    // and subtypes like MusicEvent/Festival are still events worth listing.
    private static bool IsEventType(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type)) return false;

        return type.ValueKind switch
        {
            JsonValueKind.String => LooksLikeEvent(type.GetString()),
            JsonValueKind.Array => type.EnumerateArray()
                .Any(t => t.ValueKind == JsonValueKind.String && LooksLikeEvent(t.GetString())),
            _ => false
        };
    }

    private static bool LooksLikeEvent(string? type) =>
        !string.IsNullOrEmpty(type)
        && type.EndsWith("Event", StringComparison.Ordinal)
        && !type.Equals("EventSeries", StringComparison.Ordinal);

    private static ExtractedEvent? Map(JsonElement node)
    {
        var title = Str(node, "name");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var isOnline = Str(node, "eventAttendanceMode") is { } mode
                       && mode.Contains(OnlineAttendanceMode, StringComparison.Ordinal);

        string venue = string.Empty, city = string.Empty;
        double? latitude = null, longitude = null;

        if (node.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object)
        {
            venue = Str(location, "name") ?? string.Empty;

            if (location.TryGetProperty("address", out var address))
            {
                city = address.ValueKind switch
                {
                    JsonValueKind.Object => Str(address, "addressLocality") ?? string.Empty,
                    // Some sites publish the address as a single string.
                    JsonValueKind.String => address.GetString() ?? string.Empty,
                    _ => string.Empty
                };
            }

            if (location.TryGetProperty("geo", out var geo) && geo.ValueKind == JsonValueKind.Object
                && TryDouble(geo, "latitude", out var lat) && TryDouble(geo, "longitude", out var lon))
            {
                latitude = lat;
                longitude = lon;
            }
        }

        var miles = latitude is { } y && longitude is { } x
            ? Math.Round(CivicPlaces.MilesFromHomeTo(y, x), 1)
            : CivicPlaces.MilesFor(CivicPlaces.Resolve(string.Empty, $"{city} {venue}"));

        return new ExtractedEvent(
            title!.Trim(),
            Str(node, "url") ?? string.Empty,
            ParseDate(Str(node, "startDate")),
            ParseDate(Str(node, "endDate")),
            venue,
            city,
            latitude,
            longitude,
            miles,
            isOnline,
            ImageOf(node),
            Str(node, "description"));
    }

    // "image" is a bare URL on some sites and an ImageObject (or an array of them) on others.
    private static string? ImageOf(JsonElement node)
    {
        if (!node.TryGetProperty("image", out var image)) return null;
        return image.ValueKind switch
        {
            JsonValueKind.String => image.GetString(),
            JsonValueKind.Object => Str(image, "url"),
            JsonValueKind.Array => image.EnumerateArray()
                .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : Str(i, "url"))
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
            _ => null
        };
    }

    private static string? Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryDouble(JsonElement node, string name, out double result)
    {
        result = 0;
        if (!node.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            // Coordinates are published as strings by Eventbrite and as numbers by others.
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
            JsonValueKind.Number => value.TryGetDouble(out result),
            _ => false
        };
    }

    private static DateTimeOffset? ParseDate(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
}
