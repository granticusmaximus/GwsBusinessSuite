namespace GwsBusinessSuite.SentinelCli;

// User-authored, file-based instruction snippets - deliberately simpler than /agent's personas:
// no built-in set, no frontmatter, just "one markdown file is one skill, injected as extra
// instruction for a single turn." Anyone can add one without recompiling the CLI.
public sealed class SkillLibrary(string skillsDirectory)
{
    public string Directory => skillsDirectory;

    public IReadOnlyList<string> List() =>
        System.IO.Directory.Exists(skillsDirectory)
            ? System.IO.Directory.GetFiles(skillsDirectory, "*.md")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    public string? Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            return null;

        var path = Path.Combine(skillsDirectory, name + ".md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
