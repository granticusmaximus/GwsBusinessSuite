using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GwsBusinessSuite.Application.ContentStudio;

public sealed record CodeBlockProblem(int BlockNumber, string Language, string Code, string Diagnostic);

public sealed record CodeVerificationReport(
    int BlocksChecked,
    IReadOnlyList<CodeBlockProblem> Problems)
{
    public bool IsClean => Problems.Count == 0;
}

// Compiles the C# in a generated article.
//
// The system prompt already forbids inventing APIs and asks the model to self-check. It still
// hallucinates, because a self-check runs on the same weights that produced the error: a model
// confident enough to invent HttpClient.GetJsonAsync is confident enough to confirm it. A
// compiler has no such problem. It is the only step here that produces ground truth rather than
// another opinion, which is why its diagnostics - not a critique prompt - are what gets fed back
// to the model for a fix.
//
// Deliberately narrow: this proves the code is real. It says nothing about prose claims or
// benchmark figures, which need a different mechanism.
public static partial class GeneratedCodeVerifier
{
    // Errors that mean "this API does not exist" or "you called it wrong" - the hallucination
    // signature. Everything else (style, unreachable code, unused variables) is noise here, and
    // an article snippet is allowed to be a fragment.
    private static readonly HashSet<string> HallucinationCodes =
    [
        "CS0103", // name does not exist in the current context
        "CS0117", // type does not contain a definition
        "CS1061", // no accessible extension method / member
        "CS0246", // type or namespace not found
        "CS0234", // namespace member not found
        "CS1501", // no overload takes N arguments
        "CS7036", // required parameter has no argument
        "CS1503", // argument cannot convert
        "CS0029", // cannot implicitly convert
        "CS1002", // syntax: ; expected
        "CS1022", // syntax: type or namespace definition expected
        "CS1513", // syntax: } expected
    ];

    // What a normal project gives you via ImplicitUsings. Article snippets legitimately omit
    // usings, and flagging that as a hallucination would bury the real errors in false positives.
    private const string UsingPreamble = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Globalization;
        using System.IO;
        using System.Linq;
        using System.Net.Http;
        using System.Net.Http.Json;
        using System.Text;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;

        """;

    public static CodeVerificationReport Verify(string markdown)
    {
        var blocks = ExtractCSharpBlocks(markdown);
        if (blocks.Count == 0) return new CodeVerificationReport(0, []);

        var references = ReferenceAssemblies.Value;
        // Types the article defines itself. A later snippet referencing a class introduced in an
        // earlier one is correct, not a hallucination, so those names are not reported missing.
        var declared = blocks
            .SelectMany(block => DeclaredTypeNames(block.Code))
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<CodeBlockProblem>();
        foreach (var block in blocks)
        {
            foreach (var diagnostic in CompileBlock(block.Code, references))
            {
                if (!HallucinationCodes.Contains(diagnostic.Id)) continue;
                var message = diagnostic.GetMessage();
                // Skip anything whose missing name the article defines elsewhere.
                if (declared.Any(name => message.Contains($"'{name}'", StringComparison.Ordinal))) continue;

                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                problems.Add(new CodeBlockProblem(
                    block.Number, block.Language, block.Code,
                    $"line {line}: {diagnostic.Id}: {message}"));
            }
        }

        return new CodeVerificationReport(blocks.Count, problems);
    }

    private static IEnumerable<Diagnostic> CompileBlock(string code, ImmutableArray<MetadataReference> references)
    {
        // Snippets come in three shapes: a full type, loose members, or bare statements. Each is
        // tried in turn and the first that parses cleanly is the one whose semantic errors are
        // reported - otherwise a perfectly good method body is flagged for not being a program.
        foreach (var candidate in Candidates(code))
        {
            var tree = CSharpSyntaxTree.ParseText(candidate,
                new CSharpParseOptions(LanguageVersion.Latest));
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) continue;

            var compilation = CSharpCompilation.Create(
                "ArticleSnippet",
                [tree],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
        }

        // Nothing parsed: report the syntax errors from the least-wrapped form, which is the one
        // whose line numbers still line up with what the reader sees.
        return CSharpSyntaxTree
            .ParseText(UsingPreamble + code, new CSharpParseOptions(LanguageVersion.Latest))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static IEnumerable<string> Candidates(string code)
    {
        yield return UsingPreamble + code;                                              // full file
        yield return $"{UsingPreamble}class __Snippet {{\n{code}\n}}";                  // loose members
        yield return $"{UsingPreamble}class __Snippet {{ async Task __Run() {{\n{code}\n}} }}"; // statements
    }

    // The running runtime's own assemblies. Using these rather than a pinned reference pack means
    // the article is checked against the framework this app actually targets.
    private static readonly Lazy<ImmutableArray<MetadataReference>> ReferenceAssemblies = new(() =>
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in trusted)
        {
            try { references.Add(MetadataReference.CreateFromFile(path)); }
            catch (Exception) { /* Native or unreadable entries are simply not references. */ }
        }
        return references.ToImmutable();
    });

    private static IEnumerable<string> DeclaredTypeNames(string code) =>
        DeclarationPattern().Matches(code).Select(match => match.Groups["name"].Value);

    internal static IReadOnlyList<(int Number, string Language, string Code)> ExtractCSharpBlocks(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        var blocks = new List<(int, string, string)>();
        var number = 0;
        foreach (Match match in FencePattern().Matches(markdown))
        {
            var language = match.Groups["lang"].Value.Trim().ToLowerInvariant();
            number++;
            if (language is not ("csharp" or "cs" or "c#")) continue;

            var code = match.Groups["code"].Value;
            if (!string.IsNullOrWhiteSpace(code)) blocks.Add((number, language, code));
        }
        return blocks;
    }

    [GeneratedRegex(@"^```(?<lang>[A-Za-z#+]*)\s*\n(?<code>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"\b(?:class|record|struct|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DeclarationPattern();
}
