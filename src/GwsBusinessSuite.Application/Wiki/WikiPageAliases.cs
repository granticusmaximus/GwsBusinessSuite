using System.Text.RegularExpressions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelAliasUpdate(IReadOnlyList<string> Aliases, IReadOnlyList<string> SharedWith);

public interface ISentinelPageAliasService
{
    Task<IReadOnlyList<string>> GetAsync(Guid pageId, CancellationToken cancellationToken = default);

    // Needs edit access. SharedWith names other pages already titled or aliased the same, where a
    // typed [[name]] stays ambiguous (it links to the page titled that, else to nothing).
    Task<SentinelAliasUpdate> SetAsync(Guid pageId, IEnumerable<string> aliases, string username, CancellationToken cancellationToken = default);
}

// Page aliases (Sentinel item 15): "Also known as" names stored one per line in WikiPage.Aliases.
public static partial class WikiPageAliases
{
    public const int MaxAliases = 10;
    public const int MaxLength = 60;

    public static IReadOnlyList<string> Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? [] : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Trims and tidies each name, drops brackets/pipes (they'd break [[link]] syntax), the title
    // itself and repeats (ignoring case), and keeps at most MaxAliases.
    public static IReadOnlyList<string> Normalize(IEnumerable<string?> input, string title)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { (title ?? string.Empty).Trim() };
        var result = new List<string>();
        foreach (var raw in input ?? [])
        {
            var alias = Whitespace().Replace(Unsafe().Replace(raw ?? string.Empty, string.Empty), " ").Trim();
            if (alias.Length == 0) continue;
            if (alias.Length > MaxLength) alias = alias[..MaxLength].TrimEnd();
            if (!seen.Add(alias)) continue;
            result.Add(alias);
            if (result.Count == MaxAliases) break;
        }
        return result;
    }

    // "GW, Grant; G. Watson" -> three names, as typed in the "Also known as" field.
    public static IReadOnlyList<string> SplitInput(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Serialize(IReadOnlyList<string> aliases) => string.Join('\n', aliases);

    public static bool HasAlias(WikiPage page, string name) =>
        Parse(page.Aliases).Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase));

    // The page a typed [[name]] means: one titled that, otherwise the only page with that alias.
    public static WikiPage? Resolve(string name, IReadOnlyList<WikiPage> pages)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        var byTitle = pages.FirstOrDefault(p => string.Equals(p.Title?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (byTitle is not null) return byTitle;
        var byAlias = pages.Where(p => HasAlias(p, name)).Take(2).ToList();
        return byAlias.Count == 1 ? byAlias[0] : null;
    }

    [GeneratedRegex(@"[\[\]|]")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
