using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.ContentStudio;

namespace GwsBusinessSuite.Application.DevTools;

public static class DevToolsTesters
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Arbitrary user-supplied patterns run against arbitrary input server-side - the explicit
    // timeout is a real ReDoS guard, not optional polish, since a pattern like (a+)+b against a
    // non-matching long input can otherwise hang the request indefinitely.
    public static DevToolsResult TestRegex(string pattern, string input, bool ignoreCase, bool multiline)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return DevToolsResult.Fail("Enter a pattern to test.");
        }

        var options = RegexOptions.None;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        if (multiline) options |= RegexOptions.Multiline;

        try
        {
            var regex = new Regex(pattern, options, RegexTimeout);
            var matches = regex.Matches(input);
            if (matches.Count == 0)
            {
                return DevToolsResult.Ok("No matches.");
            }

            var lines = matches.Select((match, index) =>
            {
                var groups = match.Groups.Count > 1
                    ? " | groups: " + string.Join(", ", match.Groups.Cast<Group>().Skip(1).Select(g => g.Success ? g.Value : "(no match)"))
                    : string.Empty;
                return $"Match {index + 1} at {match.Index}: \"{match.Value}\"{groups}";
            });
            return DevToolsResult.Ok(string.Join("\n", lines));
        }
        catch (RegexParseException ex)
        {
            return DevToolsResult.Fail($"Invalid pattern: {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return DevToolsResult.Fail("That pattern took too long to evaluate (possible catastrophic backtracking) and was stopped after 2 seconds.");
        }
    }

    // BuildLineDiff is O(beforeLines x afterLines) in both time and memory - this cap is what
    // actually bounds that (character count alone wouldn't: a 200KB paste with no newlines is one
    // "line" and trivially fast, while 200K one-character lines would try to allocate ~150GB).
    private const int MaxDiffLines = 5_000;

    public static DevToolsDiffResult DiffText(string before, string after)
    {
        var beforeLineCount = CountLines(before);
        var afterLineCount = CountLines(after);
        if (beforeLineCount > MaxDiffLines || afterLineCount > MaxDiffLines)
        {
            return DevToolsDiffResult.Fail($"Each side is limited to {MaxDiffLines:N0} lines for the diff tool.");
        }

        return DevToolsDiffResult.Ok(ContentStudioService.BuildLineDiff(before, after));
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;

    public static string AnalyzeText(string input)
    {
        var characters = input.Length;
        var charactersNoSpaces = input.Count(c => !char.IsWhiteSpace(c));
        var words = input.Split((char[]?)null!, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = CountLines(input);
        var sentences = Regex.Matches(input, @"[.!?]+(?=\s|$)", RegexOptions.None, RegexTimeout).Count;

        return $"Characters: {characters}\nCharacters (no spaces): {charactersNoSpaces}\nWords: {words}\nLines: {lines}\nSentences: {sentences}";
    }
}
