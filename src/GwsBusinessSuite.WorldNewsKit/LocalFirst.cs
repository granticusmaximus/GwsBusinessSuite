namespace GwsBusinessSuite.WorldNewsKit;

// B8 (13-11-26): local first. Outlets based in the country where a story happened usually know it
// best and report it first, so the story page can list them first and say how many there are.
// The story's country comes from its headline (WorldNewsPlaces); outlets' from their feed.
public static class LocalFirst
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "United States", ["US"] = "United States", ["United States of America"] = "United States",
        ["UK"] = "United Kingdom", ["Great Britain"] = "United Kingdom", ["Britain"] = "United Kingdom",
        ["Russian Federation"] = "Russia", ["Republic of Korea"] = "South Korea", ["Korea, South"] = "South Korea",
        ["Türkiye"] = "Turkey", ["Czechia"] = "Czech Republic", ["UAE"] = "United Arab Emirates",
    };

    public static string Canonical(string country) =>
        Aliases.TryGetValue(country.Trim(), out var name) ? name : country.Trim();

    public static bool IsLocal(string? outletCountry, string? storyCountry) =>
        !string.IsNullOrWhiteSpace(outletCountry) && !string.IsNullOrWhiteSpace(storyCountry)
        && string.Equals(Canonical(outletCountry), Canonical(storyCountry), StringComparison.OrdinalIgnoreCase);

    // Local outlets first (earliest first among them), then the rest in the order given.
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> items, Func<T, string> country, Func<T, DateTimeOffset> firstSeen, string? storyCountry)
    {
        var list = items.ToList();
        if (string.IsNullOrWhiteSpace(storyCountry)) return list;
        return list.Where(item => IsLocal(country(item), storyCountry)).OrderBy(firstSeen)
            .Concat(list.Where(item => !IsLocal(country(item), storyCountry)))
            .ToList();
    }
}
