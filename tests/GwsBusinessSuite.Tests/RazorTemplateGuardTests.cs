using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// 05-11-26: KpiCard wrote a template as @<>...</> (a React-style fragment). Razor has no fragment
// tag. It compiled <> to an element with an empty name, the browser refused document.createElement(""),
// and the circuit died - "An unhandled error has occurred. Reload" on the Home dashboard straight
// after login, 01-11..05-11-26. A template without a wrapper element is @<text>...</text>.
public sealed partial class RazorTemplateGuardTests
{
    private static string ComponentsRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/Components"));

    [Fact]
    public void NoRazorFile_ShouldUseAnEmptyFragmentTag()
    {
        var offenders = Directory.EnumerateFiles(ComponentsRoot, "*.razor", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => EmptyFragment().IsMatch(entry.line))
            .Select(entry => $"{Path.GetFileName(entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty("<> and </> compile to an element with an empty name, which crashes the page; use @<text>...</text>");
    }

    // "@<>" opening a template, or a "</>" closing one (not "<>" inside C# generics like List<>).
    [GeneratedRegex(@"@<>|^\s*</>|</>\s*;")]
    private static partial Regex EmptyFragment();
}
