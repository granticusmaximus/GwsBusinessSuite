namespace GwsBusinessSuite.Infrastructure.Services;

// Extracted from NewsIntelligenceService as its own public static class so this mapping
// (the one piece of genuinely trip-worthy logic in the dev.to fetch pipeline) can be unit
// tested directly, rather than only reachable through a live HTTP call.
public static class DevToTagNormalizer
{
    // Blind alphanumeric-stripping turns "C#" into "c" and ".NET" into "net" - neither is
    // dev.to's real tag ("csharp"/"dotnet") - so common language/framework keywords are
    // aliased to their actual dev.to tag first, falling back to the stripped form for
    // anything not in the list (which is already a real tag-shaped word, e.g. "python",
    // "blazor").
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c#"] = "csharp",
        ["csharp"] = "csharp",
        [".net"] = "dotnet",
        ["dotnet"] = "dotnet",
        ["f#"] = "fsharp",
        ["node.js"] = "node",
        ["nodejs"] = "node",
        ["c++"] = "cpp",
    };

    public static string Normalize(string keyword)
    {
        var trimmed = keyword.Trim();
        if (Aliases.TryGetValue(trimmed, out var alias))
        {
            return alias;
        }

        return new string(trimmed.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
