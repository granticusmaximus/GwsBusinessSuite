using FluentAssertions;
using GwsBusinessSuite.Application.ContentStudio;

namespace GwsBusinessSuite.Tests;

// The point of compiling generated code is that a compiler cannot be persuaded an API exists.
// These cover both halves of that being useful: it must catch invented APIs, and it must not
// flag legitimately abbreviated snippets - a verifier that cries wolf gets switched off.
public sealed class GeneratedCodeVerifierTests
{
    private static string Article(string code, string lang = "csharp") => $"""
        # A guide

        Some prose.

        ```{lang}
        {code}
        ```

        More prose.
        """;

    [Fact]
    public void Verify_ShouldCatchAnInventedMethod()
    {
        // The classic hallucination: a plausible-sounding method that does not exist.
        var report = GeneratedCodeVerifier.Verify(Article("""
            var client = new HttpClient();
            var data = await client.GetJsonAsync("https://example.com/api");
            """));

        report.BlocksChecked.Should().Be(1);
        report.Problems.Should().ContainSingle();
        report.Problems[0].Diagnostic.Should().Contain("GetJsonAsync");
    }

    [Fact]
    public void Verify_ShouldCatchAnInventedType()
    {
        var report = GeneratedCodeVerifier.Verify(Article("""
            var cache = new MemoryCacheBuilder().WithSlidingExpiration(TimeSpan.FromMinutes(5));
            """));

        report.IsClean.Should().BeFalse();
        report.Problems[0].Diagnostic.Should().Contain("MemoryCacheBuilder");
    }

    [Fact]
    public void Verify_ShouldCatchAWrongSignature()
    {
        // Real method, wrong arguments - the failure a reader hits only when they try to build.
        var report = GeneratedCodeVerifier.Verify(Article("""
            var text = "hello";
            var trimmed = text.Substring(1, 2, 3);
            """));

        report.IsClean.Should().BeFalse();
    }

    [Fact]
    public void Verify_ShouldAcceptCodeThatOmitsUsings()
    {
        // Snippets legitimately omit usings; the prompt even allows it. Flagging that would bury
        // real errors under false positives and the whole check would get ignored.
        var report = GeneratedCodeVerifier.Verify(Article("""
            var items = new List<string> { "a", "b" };
            var joined = string.Join(", ", items.Where(x => x.Length > 0));
            Console.WriteLine(joined);
            """));

        report.IsClean.Should().BeTrue(
            "a snippet without using directives is normal, not a hallucination");
    }

    [Fact]
    public void Verify_ShouldAcceptALooseMethodBody()
    {
        // Articles routinely show a method rather than a whole program.
        var report = GeneratedCodeVerifier.Verify(Article("""
            public async Task<string> FetchAsync(HttpClient client, string url)
            {
                using var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            """));

        report.IsClean.Should().BeTrue();
    }

    [Fact]
    public void Verify_ShouldAcceptATypeDefinedInAnEarlierBlock()
    {
        // Cross-block references are correct writing, not missing types.
        const string markdown = """
            First, the model:

            ```csharp
            public record Invoice(string Number, decimal Total);
            ```

            Then use it:

            ```csharp
            var invoice = new Invoice("INV-1", 42.00m);
            Console.WriteLine(invoice.Total);
            ```
            """;

        GeneratedCodeVerifier.Verify(markdown).IsClean.Should().BeTrue();
    }

    [Fact]
    public void Verify_ShouldIgnoreNonCSharpBlocks()
    {
        var report = GeneratedCodeVerifier.Verify("""
            ```bash
            dotnet add package Definitely.Not.Real
            ```

            ```json
            { "not": "csharp" }
            ```
            """);

        report.BlocksChecked.Should().Be(0);
        report.IsClean.Should().BeTrue();
    }

    [Fact]
    public void Verify_ShouldReportWhichBlockFailed()
    {
        // A reviewer needs to know where to look, so the block number is part of the report.
        const string markdown = """
            ```csharp
            Console.WriteLine("fine");
            ```

            ```csharp
            NotARealType.DoSomething();
            ```
            """;

        var report = GeneratedCodeVerifier.Verify(markdown);

        report.BlocksChecked.Should().Be(2);
        report.Problems.Should().ContainSingle();
        report.Problems[0].BlockNumber.Should().Be(2);
    }

    [Fact]
    public void Verify_ShouldHandleAnArticleWithNoCodeAtAll()
    {
        GeneratedCodeVerifier.Verify("# Just prose\n\nNo code here.").IsClean.Should().BeTrue();
    }
}
