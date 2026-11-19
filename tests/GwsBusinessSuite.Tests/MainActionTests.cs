using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// 19-11-26 (Apple-style item 3): a page has one main action, and the rest sits under More.
public sealed class MainActionTests
{
    private static readonly string Pages = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite"));

    // World News's R shortcut clicks the first .world-news-refresh: it was Regroup (a full regroup of
    // every story) until 19-11-26. Pull latest is the only one, and the page's primary button.
    [Fact]
    public void WorldNews_RShortcut_ShouldPullTheNews_NotRegroup()
    {
        var page = File.ReadAllText(Path.Combine(Pages, "WorldNews.razor"));
        var refresh = Regex.Matches(page, @"class=""[^""]*\bworld-news-refresh\b[^""]*""[^>]*@onclick=""(?<action>\w+)""");
        refresh.Should().ContainSingle().Which.Groups["action"].Value.Should().Be("PullNow");
        page.Should().Contain(@"class=""btn btn-primary world-news-refresh""");
    }

    // Every page header: at most one primary button, and no more than four buttons in a row; the rest
    // goes under More (a dropdown).
    [Fact]
    public void PageHeaders_ShouldHaveOneMainAction_AndKeepTheRestUnderMore()
    {
        var root = Path.GetFullPath(Path.Combine(Pages, ".."));
        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            var actions = Regex.Match(File.ReadAllText(file), @"<Actions>(?<body>.*?)</Actions>", RegexOptions.Singleline);
            if (!actions.Success) continue;
            // Not counted: the More menu, a segmented control (only its selected segment is filled), and
            // an else branch (an if/else shows one of its buttons, as CRM's New deal / New contact).
            var body = Regex.Replace(actions.Groups["body"].Value, @"<ul class=""dropdown-menu.*?</ul>", "", RegexOptions.Singleline);
            body = Regex.Replace(body, @"<div class=""btn-group"".*?</div>", "", RegexOptions.Singleline);
            body = Regex.Replace(body, @"\belse\s*\{.*?\n\s*\}", "", RegexOptions.Singleline);
            var name = Path.GetFileName(file);
            Regex.Matches(body, @"\bbtn-primary\b").Count.Should().BeLessThanOrEqualTo(1, $"{name} has one main action");
            Regex.Matches(body, @"<(a|button)\b[^>]*class=""[^""]*\bbtn\b").Count.Should().BeLessThanOrEqualTo(4, $"{name} keeps the rest under More");
        }
    }
}
