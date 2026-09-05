namespace GwsBusinessSuite.SentinelAgentKit;

// User-authored, file-based instruction snippets - deliberately simpler than /agent's personas:
// no built-in set, no frontmatter, just "one markdown file is one skill, injected as extra
// instruction for a single turn." Anyone can add one without recompiling anything.
//
// Takes several directories because its two hosts can't share one. SentinelCLI keeps skills in
// ~/.config/sentinelcli/skills, which the sandboxed Mac app cannot read at all - App Sandbox
// grants it its own container plus whatever folder the user explicitly picked, and nothing else.
// So the app reads its container *and* the attached project folder, which lets a repository
// carry its own skills alongside its code. Earlier directories win a name collision, so a
// host-level skill can be overridden by a repo-local one of the same name.
public sealed class SkillLibrary
{
    private readonly string[] _directories;

    // Nullable entries are accepted and dropped so a host can pass a conditional source
    // ("the attached folder's skills, if a folder is attached") without branching at the call site.
    public SkillLibrary(params string?[] skillsDirectories) =>
        _directories = skillsDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .ToArray();

    // The primary (first) directory - what a host shows when telling a user where to add one.
    public string Directory => _directories.Length > 0 ? _directories[0] : string.Empty;

    public IReadOnlyList<string> List() =>
        _directories
            .Where(System.IO.Directory.Exists)
            .SelectMany(directory => System.IO.Directory.GetFiles(directory, "*.md"))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string? Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            return null;

        foreach (var directory in _directories)
        {
            var path = Path.Combine(directory, name + ".md");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    // The shape a host wraps a skill in for one turn, kept here so the CLI and the app phrase it
    // identically rather than each inventing their own framing.
    public static string BuildTurnPrompt(string skillName, string instructions, string request) =>
        $"Follow this skill's instructions for this request.\n\nSkill '{skillName}':\n{instructions}\n\nRequest: {request}";
}
