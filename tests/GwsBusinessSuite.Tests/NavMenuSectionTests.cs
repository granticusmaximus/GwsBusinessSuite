using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// 19-11-26 (Apple-style item 2): a workspace with more than five links reads as short labelled
// groups, as Mail's and Finder's sidebars do, not one long list.
public sealed class NavMenuSectionTests
{
    private static readonly string NavMenu = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Layout/NavMenu.razor"));

    [Fact]
    public void LongWorkspaces_ShouldOpenWithAHeading_SoNoLinkSitsUngrouped()
    {
        var razor = File.ReadAllText(NavMenu);
        var workspaces = Regex.Matches(razor, @"data-workspace=""(?<key>[a-z]+)""(?<body>.*?)</section>", RegexOptions.Singleline);
        workspaces.Should().NotBeEmpty();
        foreach (Match workspace in workspaces)
        {
            var body = workspace.Groups["body"].Value;
            var links = Regex.Matches(body, @"class=""gws-nav-link""|AppLinkClass\(").Count;
            if (links <= 5) continue;
            var firstHeading = body.IndexOf("gws-subnav-section", StringComparison.Ordinal);
            var firstLink = Regex.Match(body, @"class=""gws-nav-link""|AppLinkClass\(").Index;
            firstHeading.Should().BeGreaterThan(-1, $"{workspace.Groups["key"].Value} has {links} links");
            firstHeading.Should().BeLessThan(firstLink, $"{workspace.Groups["key"].Value}'s first links need a heading too");
        }
    }
}
