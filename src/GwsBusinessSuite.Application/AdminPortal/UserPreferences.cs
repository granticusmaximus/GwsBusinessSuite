namespace GwsBusinessSuite.Application.AdminPortal;

// Per-person admin settings (shell redesign R4): today only the layout density. Each key has a
// fixed set of values, and an unset or unknown stored value reads as the key's default.
public interface IUserPreferenceService
{
    Task<string> GetAsync(string username, string key, CancellationToken ct = default);
    Task SetAsync(string username, string key, string value, CancellationToken ct = default);
}

public static class UserPreferenceKeys
{
    public const string Density = "density";
}

public static class Densities
{
    public const string Comfortable = "comfortable";
    public const string Compact = "compact";
}

public static class UserPreferenceRules
{
    // The first value of each key is its default.
    private static readonly Dictionary<string, string[]> Values = new(StringComparer.Ordinal)
    {
        [UserPreferenceKeys.Density] = [Densities.Comfortable, Densities.Compact]
    };

    // A family of keys whose values can't be listed: each board's filters (Boards K3), a small JSON
    // object per board, defaulting to "{}".
    private const string BoardFiltersPrefix = "boards:filters:";
    private const int MaxBoardFiltersLength = 2000;

    public static string BoardFilters(Guid databaseId) => BoardFiltersPrefix + databaseId.ToString("N");

    public static string DefaultFor(string key) => key.StartsWith(BoardFiltersPrefix, StringComparison.Ordinal) ? "{}" : ValuesFor(key)[0];

    public static bool IsAllowed(string key, string value) => key.StartsWith(BoardFiltersPrefix, StringComparison.Ordinal)
        ? value.Length <= MaxBoardFiltersLength && IsJsonObject(value)
        : ValuesFor(key).Contains(value, StringComparer.Ordinal);

    private static bool IsJsonObject(string value)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(value) is System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static string[] ValuesFor(string key) =>
        Values.TryGetValue(key, out var values) ? values : throw new ArgumentException($"Unknown preference '{key}'.");
}
