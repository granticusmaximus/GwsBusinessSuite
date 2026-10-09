namespace GwsBusinessSuite.Application.Wiki;

// A Person property's value is a list of strings (same storage as MultiSelect). New values are
// GWS usernames chosen from a picker; older rows may hold free text typed before the picker
// existed. "My work" matches assignees to the signed-in username case-insensitively, so a
// free-text "Grant" already counts as the user "grant" - these helpers use the same rule.
public sealed record WikiPersonAssignee(string Value, string? Username)
{
    public bool IsUser => Username is not null;
}

public static class WikiPersonValues
{
    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    // Each stored value, paired with the user it names (or null when it names nobody).
    public static IReadOnlyList<WikiPersonAssignee> Resolve(IEnumerable<string> stored, IReadOnlyCollection<string> usernames)
    {
        var byKey = usernames
            .GroupBy(Normalize)
            .ToDictionary(group => group.Key, group => group.First());
        return stored
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new WikiPersonAssignee(value, byKey.GetValueOrDefault(Normalize(value))))
            .ToList();
    }

    public static bool IsAssigned(IEnumerable<string> stored, string username) =>
        stored.Any(value => Normalize(value) == Normalize(username));

    // Adding stores the exact username; removing drops every value naming that user, including
    // an old free-text spelling of it, so unticking someone always clears them.
    public static IReadOnlyList<string> Toggle(IEnumerable<string> stored, string username, bool assign)
    {
        var current = stored.Where(value => Normalize(value) != Normalize(username)).ToList();
        if (assign) current.Add(username);
        return current;
    }

    public static IReadOnlyList<string> Remove(IEnumerable<string> stored, string value) =>
        stored.Where(existing => existing != value).ToList();
}
