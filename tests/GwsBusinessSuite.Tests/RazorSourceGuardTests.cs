using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Three Razor mistakes that compile, render, and silently do the wrong thing - each has shipped
// before: an inline <script> the CSP never runs (canvas 2026-09-05, widget runtimes 2026-10-03),
// @(expr).Method() printing ".Method()" as text (the WYSIWYG "can't type" bug), and a string
// parameter given Foo="_field", which passes the text "_field" (Developer Tools' blank panel).
public sealed partial class RazorSourceGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static readonly string Components = Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/Components");

    [Fact]
    public void Markup_ShouldHaveNoInlineScripts()
    {
        var inline = Sources()
            .SelectMany(source => InlineScript().Matches(source.Text).Select(match => $"{source.File}:{Line(source.Text, match.Index)}"))
            .ToList();

        inline.Should().BeEmpty("script-src has no 'unsafe-inline', so the browser never runs it - put it in wwwroot/js");
    }

    [Fact]
    public void ExplicitExpressions_ShouldNotBeFollowedByAMethodCall()
    {
        var chained = Sources()
            .SelectMany(source => ChainedExpression().Matches(source.Text).Select(match => $"{source.File}:{Line(source.Text, match.Index)} {match.Value}"))
            .ToList();

        chained.Should().BeEmpty("@(...) ends at its own ')', so the call after it renders as text - move it inside the parentheses");
    }

    [Fact]
    public void StringParameters_ShouldNotBeGivenAFieldNameWithoutAt()
    {
        var stringParameters = Sources()
            .Select(source => (Name: Path.GetFileNameWithoutExtension(source.File), Text: source.Text + CodeBehind(source.File)))
            .ToDictionary(
                component => component.Name,
                component => StringParameter().Matches(component.Text).Select(match => match.Groups[1].Value).ToHashSet());

        var literal = new List<string>();
        foreach (var source in Sources())
        {
            foreach (Match tag in ComponentTag().Matches(source.Text))
            {
                if (!stringParameters.TryGetValue(tag.Groups[1].Value, out var names)) continue;
                literal.AddRange(FieldAttribute().Matches(tag.Groups[2].Value)
                    .Where(attribute => names.Contains(attribute.Groups[1].Value))
                    .Select(attribute => $"{source.File}:{Line(source.Text, tag.Index)} {tag.Groups[1].Value} {attribute.Value.Trim()}"));
            }
        }

        literal.Should().BeEmpty("a string parameter without @ receives the literal text, not the field - write Foo=\"@_field\"");
    }

    private static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Components, "*.razor", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(Components, path), RazorComment().Replace(File.ReadAllText(path), CommentBlank)));

    private static string CodeBehind(string relativePath)
    {
        var path = Path.Combine(Components, relativePath + ".cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    // Comments are blanked line for line, so reported line numbers stay right.
    private static string CommentBlank(Match comment) => Regex.Replace(comment.Value, "[^\n]", " ");

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    // A <script> element without a src attribute.
    [GeneratedRegex(@"<script(?![^>]*\bsrc\s*=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    // @(...) - allowing one level of nested parentheses - followed directly by .Name(
    [GeneratedRegex(@"@\((?:[^()]|\([^()]*\))*\)\.[A-Za-z]\w*\(")]
    private static partial Regex ChainedExpression();

    [GeneratedRegex(@"\[Parameter[^\]]*\]\s*public\s+string\??\s+(\w+)\s*\{")]
    private static partial Regex StringParameter();

    [GeneratedRegex(@"<([A-Z]\w*)\b([^>]*)>", RegexOptions.Singleline)]
    private static partial Regex ComponentTag();

    // Name="_field" - a value that is a field name, never meant as literal text.
    [GeneratedRegex(@"\s([A-Z]\w*)=""_[A-Za-z]\w*""")]
    private static partial Regex FieldAttribute();
}
