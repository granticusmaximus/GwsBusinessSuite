using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// App-wide UX pass (12-11-26): the rules the review found drifting, kept from drifting again.
public sealed class DesignSystemGuardTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web"));

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Web, "Components"), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Name(string path) => Path.GetRelativePath(Web, path);

    [Fact]
    public void PageStyles_ShouldUseTheAppsOneTypeface()
    {
        var serif = Files("*.razor.css")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"Georgia|Playfair|--gws-font-serif"))
            .Select(Name);
        serif.Should().BeEmpty("page titles, numbers and headings use --gws-font-sans; serif is for user content only (equations, article previews)");

        var appCss = File.ReadAllText(Path.Combine(Web, "wwwroot/app.css"));
        Regex.Match(appCss, @"\.page-header-rich h1 \{[^}]*\}").Value.Should().Contain("var(--gws-font-sans)");
    }

    [Fact]
    public void PageStyles_ShouldSizeToTheContentArea_NotTheWindow()
    {
        // Shell parts outside .gws-content (the gws-main container) still need the viewport.
        string[] shell = ["AssistantPanel.razor.css", "NotificationBell.razor.css", "ReconnectModal.razor.css", "RecordInspector.razor.css"];
        var viewport = Files("*.razor.css")
            .Where(path => !shell.Contains(Path.GetFileName(path)))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"@media\s*\([^)]*(min|max)-width"))
            .Select(Name);
        viewport.Should().BeEmpty("admin pages use @container gws-main, so they fit beside the sidebar and in the Mac app");
    }

    [Fact]
    public void PanelsAndPages_ShouldLoadWithTheBootScreen_NotABareSpinner()
    {
        // Spinners that sit inside a button or beside live text are allowed (AGENTS.md); these
        // are the ones reviewed on 12-11-26. A new one belongs in a button, or is a LoadingIndicator.
        string[] reviewed =
        [
            "CreatePageModal.razor", "AssistantPanel.razor", "LegislationBriefPanel.razor", "ArticleEditorDetail.razor",
            "Automation.razor", "ContentStudio.razor", "DockerHealth.razor", "SentinelAiPanel.razor",
            "SentinelDatabaseRowPage.razor", "Settings.razor", "Wiki.razor", "WikiDatabaseEditor.razor",
            "WorldNewsStoryView.razor", "SentinelPublicShare.razor"
        ];
        var unreviewed = Files("*.razor")
            .Where(path => File.ReadAllText(path).Contains("spinner-border", StringComparison.Ordinal))
            .Where(path => !reviewed.Contains(Path.GetFileName(path)))
            .Select(Name);
        unreviewed.Should().BeEmpty();
    }

    [Fact]
    public void GrowthAndIntelligencePages_ShouldFollowTheAppTheme()
    {
        var pages = Path.Combine(Web, "Components/Pages/BusinessSuite");
        var growth = File.ReadAllText(Path.Combine(pages, "Growth.razor.css"));
        growth.Should().Contain("--growth-ink: var(--gws-text-strong);").And.Contain("--growth-panel: var(--gws-panel-bg);");
        growth.Should().NotContain("--gws-text-strong: #", "Growth no longer pins the app's tokens to dark");
        foreach (var page in new[] { "ThreatIntelligence", "NewsIntelligence", "GovernmentIntelligence" })
        {
            var css = File.ReadAllText(Path.Combine(pages, page + ".razor.css"));
            Regex.Matches(css, @"--civic-accent(-strong)?:\s*([^;]+);").Select(match => match.Groups[2].Value.Trim())
                .Should().OnlyContain(value => value == "var(--gws-sidebar-accent)", $"{page} shares the app's one accent");
        }
    }
}
