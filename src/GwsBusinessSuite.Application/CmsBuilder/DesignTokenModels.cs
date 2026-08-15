using System.Text.Json;

namespace GwsBusinessSuite.Application.CmsBuilder;

// A named color, e.g. ("Primary", "#1c3d5a"). Widget styles reference these by Name (see
// WidgetStyle.TextColorToken/BackgroundColorToken) instead of duplicating the hex value, so
// changing a token here cascades everywhere it's referenced without touching stored BlocksJson.
public sealed record DesignToken(string Name, string Hex);

// A named font-size step, e.g. ("Body", "1rem"). Referenced by WidgetStyle.FontSizeToken as an
// alternative to the existing fixed sm/md/lg/xl scale.
public sealed record TypeScaleStep(string Name, string RemValue);

// A named spacing step. Not yet wired into WidgetStyle.Padding (see Phase 1 scope note) - kept
// alongside Colors/TypeScale so the whole token set round-trips as one shape even though only
// colors and type scale are consumed by rendering today.
public sealed record SpacingScaleStep(string Name, string RemValue);

public sealed record DesignTokenSet(
    IReadOnlyList<DesignToken> Colors,
    IReadOnlyList<TypeScaleStep> TypeScale,
    IReadOnlyList<SpacingScaleStep> SpacingScale)
{
    public static DesignTokenSet Empty { get; } = new([], [], []);
}

public static class DesignTokenJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(DesignTokenSet tokens) => JsonSerializer.Serialize(tokens, Options);

    // Mirrors CmsBuilderJson.ParseLayoutOrEmpty's "empty input is fine, malformed input degrades
    // to empty rather than throwing" behavior - a corrupted DesignTokensJson value should never
    // break rendering a page, it should just fall back to no tokens (raw widget-style values
    // still work unchanged).
    public static DesignTokenSet ParseOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DesignTokenSet.Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize<DesignTokenSet>(json, Options);
            if (parsed is null) return DesignTokenSet.Empty;

            // A stored value missing one or more of the three lists (e.g. "{}", or JSON written
            // before a field existed) deserializes those properties as null, not an empty list -
            // System.Text.Json doesn't require record constructor params to be present in the
            // JSON. Normalize here so callers never have to null-check .Colors/.TypeScale/
            // .SpacingScale themselves.
            return parsed with
            {
                Colors = parsed.Colors ?? [],
                TypeScale = parsed.TypeScale ?? [],
                SpacingScale = parsed.SpacingScale ?? []
            };
        }
        catch (JsonException)
        {
            return DesignTokenSet.Empty;
        }
    }
}
