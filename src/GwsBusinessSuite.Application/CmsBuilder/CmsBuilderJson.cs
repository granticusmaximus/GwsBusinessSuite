using System.Text.Json;

namespace GwsBusinessSuite.Application.CmsBuilder;

public static class CmsBuilderJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static PageLayout? ParseLayout(string blocksJson) => Parse<PageLayout>(blocksJson);

    public static PageLayout ParseLayoutOrEmpty(string blocksJson) =>
        ParseLayout(blocksJson) ?? new PageLayout();

    // ParseLayoutOrEmpty deliberately can't distinguish "genuinely empty BlocksJson" from
    // "non-empty but malformed/wrong-shaped BlocksJson" - both return an empty PageLayout, which
    // is right for most read paths (never crash rendering a page) but has repeatedly caused
    // callers to silently treat a parse FAILURE as proof the source content was actually empty
    // (site import overwriting a page with blank content, an SEO audit scoring real content as
    // if it had none, a revision diff fabricating a full add/remove). Callers that need to tell
    // the two apart - anywhere a parse failure should be surfaced rather than silently
    // swallowed - should use this instead.
    public static bool TryParseLayout(string blocksJson, out PageLayout layout)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
        {
            layout = new PageLayout();
            return true;
        }

        var parsed = ParseLayout(blocksJson);
        layout = parsed ?? new PageLayout();
        return parsed is not null;
    }

    public static T? Parse<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json.Trim(), Options);
        }
        catch
        {
            return default;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Clone<T>(T value) where T : notnull =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)
        ?? throw new InvalidOperationException($"Unable to clone {typeof(T).Name}.");
}
