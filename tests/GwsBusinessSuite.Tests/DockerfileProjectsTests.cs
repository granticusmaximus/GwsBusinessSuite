using System.Xml.Linq;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// The Dockerfile copies projects one by one (a restore layer from the .csproj files, then the
// sources), so a project the web app starts referencing has to be added there by hand. When
// WorldNewsKit wasn't (20-10-26), restore only warned, the build failed on the droplet, and the
// deploy stopped - the local release check builds with dotnet, not Docker, so it never saw it.
public sealed class DockerfileProjectsTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Dockerfile_ShouldCopyEveryProjectTheWebAppBuildsFrom()
    {
        var dockerfile = File.ReadAllLines(Path.Combine(RepoRoot, "Dockerfile")).Select(line => line.Trim()).ToHashSet();
        var projects = ProjectClosure(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj"));

        projects.Should().Contain(p => p.EndsWith("GwsBusinessSuite.WorldNewsKit.csproj", StringComparison.Ordinal));
        foreach (var project in projects)
        {
            var directory = Path.GetRelativePath(RepoRoot, Path.GetDirectoryName(project)!).Replace('\\', '/');
            var file = Path.GetFileName(project);
            dockerfile.Should().Contain($"COPY {directory}/{file} {directory}/", $"the restore layer needs {file}");
            dockerfile.Should().Contain($"COPY {directory}/ {directory}/", $"the build needs {directory}'s sources");
        }
    }

    private static IReadOnlyCollection<string> ProjectClosure(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.GetFullPath(root)]);
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!seen.Add(project)) continue;
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value.Replace('\\', '/');
                if (include is not null) pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include)));
            }
        }
        return seen;
    }
}
