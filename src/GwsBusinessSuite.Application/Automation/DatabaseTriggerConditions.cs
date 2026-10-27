using System.Text.Json;
using System.Text.Json.Nodes;

namespace GwsBusinessSuite.Application.Automation;

// Optional filtering for database.rowChangedTrigger: without any conditions, the trigger fires
// on any property change to the watched database (existing, unchanged behavior). With
// conditions, it only fires when every condition matches the row's new values - "multi-condition
// triggers" per the plan, evaluated against final values rather than a before/after diff (no
// dedicated "did property X specifically change" operator in v1 - see the operator list below).
// "changedTo" (Boards K6) is the one transition operator: the new value equals the expected one
// and the row's previous value didn't, read from the payload's previousValues. A new row counts
// as changing from empty; a payload without previousValues never matches it, since there's no
// evidence the value moved.
public static class DatabaseTriggerConditions
{
    private static readonly HashSet<string> SupportedOperators =
        new(StringComparer.OrdinalIgnoreCase) { "equals", "notEquals", "contains", "changedTo" };

    // Empty/missing conditions = always matches, preserving today's behavior for every existing
    // workflow that predates this feature.
    public static bool Matches(string triggerNodeParametersJson, string triggerInputJson)
    {
        var parameters = ParseObject(triggerNodeParametersJson);
        if (parameters?["conditions"] is not JsonArray conditions || conditions.Count == 0) return true;

        var input = ParseObject(triggerInputJson);
        var values = input?["values"] as JsonObject;
        var isNew = input?["isNew"] is JsonValue newFlag && newFlag.TryGetValue<bool>(out var flag) && flag;
        var previousValues = isNew ? new JsonObject() : input?["previousValues"] as JsonObject;

        foreach (var conditionNode in conditions)
        {
            if (conditionNode is not JsonObject condition) continue;
            var propertyId = condition["propertyId"]?.GetValue<string>();
            var op = condition["operator"]?.GetValue<string>()?.Trim() ?? "equals";
            var expected = condition["value"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(propertyId)) continue;

            var actual = ValueText(values, propertyId);

            var matched = op.ToLowerInvariant() switch
            {
                "notequals" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                "changedto" => previousValues is not null
                    && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ValueText(previousValues, propertyId), expected, StringComparison.OrdinalIgnoreCase),
                "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            };
            if (!matched) return false;
        }
        return true;
    }

    public static void ValidateConditions(string parametersJson, string nodeName)
    {
        var parameters = ParseObject(parametersJson);
        if (parameters?["conditions"] is not JsonArray conditions) return;
        foreach (var conditionNode in conditions)
        {
            if (conditionNode is not JsonObject condition)
                throw new InvalidOperationException($"{nodeName}: every condition must be an object.");
            if (!Guid.TryParse(condition["propertyId"]?.GetValue<string>(), out _))
                throw new InvalidOperationException($"{nodeName}: every condition needs a valid propertyId.");
            var op = condition["operator"]?.GetValue<string>()?.Trim() ?? "equals";
            if (!SupportedOperators.Contains(op))
                throw new InvalidOperationException($"{nodeName}: condition operator '{op}' must be one of equals, notEquals, contains, changedTo.");
        }
    }

    private static string ValueText(JsonObject? values, string propertyId) =>
        values?[propertyId] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : values?[propertyId]?.ToJsonString() ?? string.Empty;

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
