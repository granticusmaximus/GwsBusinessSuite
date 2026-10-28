using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Guards two CSS mistakes that fail silently (found 28-10-26): a utility class used in markup
// that nothing defines (.min-w-0, in 16 components), and a class styled only in another
// component's scoped CSS without ::deep, which never reaches this component's elements (.btn-xs
// on ~14 pages; Growth's panel style inside its child components).
public sealed partial class CssClassReferenceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static readonly string Web = Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web");

    [Fact]
    public void UtilityClassesUsedInMarkup_ShouldBeDefinedSomewhere()
    {
        var defined = GlobalClasses().Union(ScopedStyles().Values.SelectMany(style => style.All)).ToHashSet();

        var missing = MarkupClasses()
            .Where(use => UtilityLike().IsMatch(use.Class) && !defined.Contains(use.Class))
            .Select(use => $"{use.Class} ({use.File})")
            .Distinct()
            .ToList();

        missing.Should().BeEmpty("Bootstrap has no such utility and no stylesheet defines it, so it does nothing");
    }

    [Fact]
    public void ClassesStyledInAnotherComponentsScopedCss_ShouldReachThroughDeep()
    {
        var global = GlobalClasses();
        var scoped = ScopedStyles();
        var deep = scoped.Values.SelectMany(style => style.Deep).ToHashSet();

        var unreached = new List<string>();
        foreach (var (file, cls) in MarkupClasses())
        {
            // Bootstrap Icons come from a CDN stylesheet this test doesn't read.
            if (cls.StartsWith("bi-", StringComparison.Ordinal) || global.Contains(cls) || deep.Contains(cls)) continue;
            var own = file + ".css";
            if (scoped.TryGetValue(own, out var style) && style.All.Contains(cls)) continue;
            var elsewhere = scoped.Where(pair => pair.Key != own && pair.Value.All.Contains(cls)).Select(pair => Path.GetFileName(pair.Key)).ToList();
            if (elsewhere.Count > 0) unreached.Add($"{cls} in {Path.GetFileName(file)} (styled only in {string.Join(", ", elsewhere)})");
        }

        unreached.Distinct().Should().BeEmpty(
            "a component's scoped CSS only styles its own markup; move the rule to app.css, or prefix it with ::deep in the parent");
    }

    private static HashSet<string> GlobalClasses()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Web, "wwwroot"), "*.css", SearchOption.AllDirectories)
            .Where(path => !path.Replace('\\', '/').Contains("/lib/", StringComparison.Ordinal))
            .Append(Path.Combine(Web, "wwwroot/lib/bootstrap/dist/css/bootstrap.min.css"));
        return files.SelectMany(path => CssClasses(File.ReadAllText(path))).ToHashSet();
    }

    private static Dictionary<string, (HashSet<string> All, HashSet<string> Deep)> ScopedStyles() =>
        Directory.EnumerateFiles(Path.Combine(Web, "Components"), "*.razor.css", SearchOption.AllDirectories)
            .ToDictionary(path => path, path =>
            {
                var css = File.ReadAllText(path);
                var deep = DeepSelector().Matches(css).SelectMany(match => CssClasses(match.Groups[1].Value)).ToHashSet();
                return (CssClasses(css).ToHashSet(), deep);
            });

    // Static class names in each component's class="..." attributes; Razor expressions are dropped.
    private static IEnumerable<(string File, string Class)> MarkupClasses() =>
        Directory.EnumerateFiles(Path.Combine(Web, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(path => ClassAttribute().Matches(File.ReadAllText(path))
                .SelectMany(match => RazorExpression().Replace(match.Groups[1].Value, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(token => ClassName().IsMatch(token))
                .Select(token => (path, token)));

    private static IEnumerable<string> CssClasses(string css) =>
        CssClass().Matches(CssComment().Replace(css, string.Empty)).Select(match => match.Groups[1].Value);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"\.(-?[A-Za-z_][\w-]*)")]
    private static partial Regex CssClass();

    [GeneratedRegex(@"::deep([^{,]*)")]
    private static partial Regex DeepSelector();

    [GeneratedRegex("class=\"([^\"]*)\"")]
    private static partial Regex ClassAttribute();

    [GeneratedRegex(@"@\([^)]*\)|@\w[\w.]*(\([^)]*\))?")]
    private static partial Regex RazorExpression();

    [GeneratedRegex(@"^[A-Za-z][\w-]*$")]
    private static partial Regex ClassName();

    // Names shaped like Bootstrap utilities and components - the ones easy to assume exist.
    [GeneratedRegex(@"^((m|p)[trblxyse]?-(\d|auto|n\d)|g[xy]?-\d|gap-|row-gap|column-gap|(min|max)-(w|h|vw|vh)-|[wh]-\d|[wh]-auto|vw-|vh-|text-|bg-|fw-|fs-|fst-|lh-|d-|flex-|align-|justify-|order-|overflow-|position-|top-|bottom-|start-|end-|translate-|z-|border|rounded|shadow|opacity-|cursor-|user-select|pe-|visible|invisible|font-|small$|lead$|link-|ratio|object-fit|vstack|hstack|stretched|clearfix|float-|col-|row-cols|btn-|badge|alert-|list-group|form-|input-group|table-|spinner|placeholder|truncate|whitespace|break|nowrap)")]
    private static partial Regex UtilityLike();
}
