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

    public static string DefaultFor(string key) => ValuesFor(key)[0];

    public static bool IsAllowed(string key, string value) => ValuesFor(key).Contains(value, StringComparer.Ordinal);

    private static string[] ValuesFor(string key) =>
        Values.TryGetValue(key, out var values) ? values : throw new ArgumentException($"Unknown preference '{key}'.");
}
