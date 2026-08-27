using System.Diagnostics;
using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

public sealed class DevToolsTestersTests
{
    [Fact]
    public void TestRegex_ShouldReportEachMatchWithItsGroups()
    {
        var result = DevToolsTesters.TestRegex(@"(\w+)@(\w+)\.com", "reach grant@example.com or sales@acme.com", false, false);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Match 1").And.Contain("Match 2").And.Contain("groups: grant, example");
    }

    [Fact]
    public void TestRegex_ShouldFailCleanlyForAnInvalidPattern()
    {
        DevToolsTesters.TestRegex("(unclosed", "x", false, false).Success.Should().BeFalse();
    }

    [Fact]
    public void TestRegex_ShouldStopACatastrophicallyBacktrackingPatternWithinItsTimeout()
    {
        // Classic ReDoS shape: no trailing match forces exponential backtracking over (a+)+.
        var input = new string('a', 35) + "!";

        var stopwatch = Stopwatch.StartNew();
        var result = DevToolsTesters.TestRegex(@"^(a+)+$", input, false, false);
        stopwatch.Stop();

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("too long");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void DiffText_ShouldMarkAddedAndRemovedLines()
    {
        var result = DevToolsTesters.DiffText("line one\nline two", "line one\nline three");

        result.Success.Should().BeTrue();
        result.Lines.Should().Contain(line => line.Kind == "unchanged" && line.Text == "line one");
        result.Lines.Should().Contain(line => line.Kind == "removed" && line.Text == "line two");
        result.Lines.Should().Contain(line => line.Kind == "added" && line.Text == "line three");
    }

    [Fact]
    public void DiffText_ShouldRejectInputBeyondTheLineCap()
    {
        var oversized = string.Join('\n', Enumerable.Range(0, 5_001).Select(i => i.ToString()));

        var result = DevToolsTesters.DiffText(oversized, "short");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("limited to");
    }

    [Fact]
    public void AnalyzeText_ShouldCountWordsLinesAndSentences()
    {
        var summary = DevToolsTesters.AnalyzeText("Hello world. How are you?\nSecond line.");

        summary.Should().Contain("Words: 7").And.Contain("Lines: 2").And.Contain("Sentences: 3");
    }
}
