using System.Text.Json;
using System.Text.Json.Nodes;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Same shape/convention as Wiki's WikiPropertyValues - a Dictionary<propertyId,value> JSON blob,
// narrowed to only the accessors CmsPagePropertyTypes actually needs (every type here is either
// text-shaped, a number, or a date). Kept as its own small type rather than reusing
// WikiPropertyValues directly so CmsBuilder stays self-contained and doesn't pull in a
// Sentinel/Wiki-module dependency for an unrelated feature.
public static class CmsPropertyValues
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static JsonObject ParseObject(string propertyValuesJson)
    {
        if (string.IsNullOrWhiteSpace(propertyValuesJson)) return new JsonObject();
        try { return JsonNode.Parse(propertyValuesJson)?.AsObject() ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static string Serialize(JsonObject values) => values.ToJsonString(Options);

    public static string? GetText(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) ? node?.GetValue<string>() : null;

    public static void SetText(JsonObject values, Guid propertyId, string? value) => values[propertyId.ToString()] = value;

    public static decimal? GetNumber(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is not null
            && decimal.TryParse(node.GetValue<string>(), out var number) ? number : null;

    public static void SetNumber(JsonObject values, Guid propertyId, decimal? value) =>
        values[propertyId.ToString()] = value?.ToString();

    public static DateTimeOffset? GetDate(JsonObject values, Guid propertyId) =>
        values.TryGetPropertyValue(propertyId.ToString(), out var node) && node is not null
            && DateTimeOffset.TryParse(node.GetValue<string>(), out var date) ? date : null;

    public static void SetDate(JsonObject values, Guid propertyId, DateTimeOffset? value) =>
        values[propertyId.ToString()] = value?.ToString("O");
}
