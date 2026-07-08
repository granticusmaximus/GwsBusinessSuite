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
