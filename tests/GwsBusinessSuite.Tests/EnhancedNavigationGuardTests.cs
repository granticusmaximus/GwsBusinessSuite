using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Enhanced navigation's DOM merge (27-10-26): data-permanent keeps an element's children but
// re-syncs its own attributes, and the merge also re-syncs <html>, <body> and the layout's
// classes. Two shell bugs came from that: the ⌘K palette and shortcut sheet closed when a
// navigation finished (their `hidden` sat on the permanent root), and app.js's after-navigation
// hook listened for a DOM event Blazor never raises, so a collapsed sidebar and light theme
// reverted on every page change.
public sealed class EnhancedNavigationGuardTests
{
    private static string Web => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web"));

    [Fact]
    public void PermanentElements_ShouldNotCarryStateInTheirOwnAttributes()
    {
        var layout = File.ReadAllText(Path.Combine(Web, "Components/Layout/MainLayout.razor"));
        var permanentTags = Regex.Matches(layout, @"<[a-z]+\b[^>]*\bdata-permanent\b[^>]*>").Select(match => match.Value).ToList();

        permanentTags.Should().NotBeEmpty();
        permanentTags.Should().NotContain(tag => Regex.IsMatch(tag, @"\shidden(\s|>|=)"),
            "the merge puts a permanent root's hidden back - wrap it and toggle a child instead");
    }

    [Fact]
    public void AppJs_ShouldReRunTheShellSetupThroughBlazorsOwnEnhancedLoadEvent()
    {
        var app = File.ReadAllText(Path.Combine(Web, "wwwroot/app.js"));

        app.Should().Contain("Blazor.addEventListener('enhancedload', initializeAdminShell)");
        app.Should().NotContain("addEventListener('blazor:enhancedload'", "no such DOM event is raised");
    }
}
