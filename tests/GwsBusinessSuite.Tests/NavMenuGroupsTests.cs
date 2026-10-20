using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

// The workspace rail (shell redesign R1): which workspace each admin route shows on load.
public class NavMenuGroupsTests
{
    private static string NavMenuSource => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Layout/NavMenu.razor")));

    public static IEnumerable<object[]> KnownRoutesByWorkspace =>
    [
        // Content
        ["admin/article-editor", "content"],
        ["admin/content-studio", "content"],
        ["admin/content-studio/draft", "content"],
        ["admin/content-calendar", "content"],
        ["admin/seo-audit", "content"],
        ["admin/localization", "content"],
        ["admin/app-generation", "content"],
        ["admin/app-generation-queue", "content"],
        ["admin/media", "content"],
        ["admin/pages", "content"],
        ["admin/pages/all", "content"],
        ["admin/appearance/menus", "content"],
        ["admin/appearance/header-footer", "content"],
        ["admin/appearance/customize", "content"],
        ["admin/podcasts", "content"],
        ["admin/live-show", "content"],
        ["admin/live-show-recordings", "content"],
        ["admin/comments", "content"],
        ["admin/cms-knowledge", "content"],
        ["admin/form-submissions", "content"],

        // Relationships
        ["admin/crm", "relationships"],
        ["admin/crm/contacts/7a1b", "relationships"],
        ["admin/deal-scoring", "relationships"],
        ["admin/billing", "relationships"],
        ["admin/time", "relationships"],
        ["admin/support", "relationships"],
        ["admin/scheduling", "relationships"],
        ["admin/email-campaigns", "relationships"],
        ["admin/email-campaigns/alerts", "relationships"],
        ["admin/users", "relationships"],

        // Home: the dashboard, Community, and routes no workspace claims (Settings has its own rail button).
        ["admin", "home"],
        ["admin/community", "home"],
        ["admin/community/activity", "home"],
        ["admin/community/messages", "home"],
        ["admin/community/departments", "home"],
        ["admin/community/profile/jdoe", "home"],
        ["admin/settings", "home"],
        ["admin/account/touch-id", "home"],
        ["admin/mission-control", "home"],

        // Knowledge - admin/wiki has no sidebar link of its own (Wiki.razor also routes at admin/sentinel).
        ["admin/sentinel", "knowledge"],
        ["admin/sentinel/present", "knowledge"],
        ["admin/wiki", "knowledge"],
        ["admin/mind-maps", "knowledge"],

        // Intelligence
        ["admin/news-intelligence", "intelligence"],
        ["admin/government-intelligence", "intelligence"],
        ["admin/business-intelligence", "intelligence"],
        ["admin/osint", "intelligence"],
        ["admin/osint/directions", "intelligence"],
        ["admin/threat-intel", "intelligence"],
        ["admin/world-news", "intelligence"],

        // Growth
        ["admin/growth", "growth"],
        ["admin/cj-ads", "growth"],
        ["admin/affiliate-suggestions", "growth"],
        ["admin/affiliate-analytics", "growth"],

        // Platform
        ["admin/automation", "platform"],
        ["admin/automation/credentials", "platform"],
        ["admin/automation/help", "platform"],
        ["admin/docker-health", "platform"],
        ["admin/dev-tools", "platform"],
        ["admin/security-audit", "platform"],
        ["admin/privacy-operations", "platform"],
    ];

    [Theory]
    [MemberData(nameof(KnownRoutesByWorkspace))]
    public void WorkspaceFor_ShouldPickItsWorkspace_ForEveryKnownAdminRoute(string route, string expected) =>
        NavMenuGroups.WorkspaceFor(route).Key.Should().Be(expected);

    [Theory]
    [InlineData("ADMIN/CRM", "relationships")]
    [InlineData("/admin/osint?camera=12", "intelligence")]
    [InlineData("admin/not-a-page", "home")]
    [InlineData("", "home")]
    public void WorkspaceFor_ShouldIgnoreCaseAndLeadingSlash_AndFallBackToHome(string route, string expected) =>
        NavMenuGroups.WorkspaceFor(route).Key.Should().Be(expected);

    [Fact]
    public void Workspaces_ShouldHaveUniqueKeys_AndOnlyHomeAndContentOpenToEveryRole()
    {
        NavMenuGroups.Workspaces.Select(workspace => workspace.Key).Should().OnlyHaveUniqueItems();
        NavMenuGroups.Workspaces.Where(workspace => !workspace.AdminOnly).Select(workspace => workspace.Key)
            .Should().Equal("home", "content");
        NavMenuGroups.Workspaces[0].Should().BeSameAs(NavMenuGroups.Home);
    }

    // Every link in a workspace's sub-nav section must belong to that workspace, or opening the
    // page would show a different workspace than the one the link was clicked in.
    [Fact]
    public void NavMenu_ShouldListEachLink_InTheWorkspaceItsRouteOpens()
    {
        var source = NavMenuSource;
        var sections = Regex.Matches(source, @"<section class=""gws-workspace"" id=""gws-ws-(?<key>[a-z]+)"".*?</section>", RegexOptions.Singleline);
        sections.Select(match => match.Groups["key"].Value).Should().Equal(NavMenuGroups.Workspaces.Select(workspace => workspace.Key));

        var links = 0;
        foreach (Match section in sections)
        {
            foreach (Match href in Regex.Matches(section.Value, @"href=""(?<href>[^""]+)"""))
            {
                var route = Resolve(href.Groups["href"].Value);
                NavMenuGroups.WorkspaceFor(route).Key.Should().Be(section.Groups["key"].Value, $"'{route}' is listed there");
                links++;
            }
        }

        links.Should().BeGreaterThan(30);
    }

    // "@AdminApps.Crm.Href" -> "admin/crm"
    private static string Resolve(string href)
    {
        var app = Regex.Match(href, @"^@AdminApps\.(?<name>\w+)\.Href$");
        if (!app.Success) return href;
        var field = typeof(AdminApps).GetField(app.Groups["name"].Value);
        field.Should().NotBeNull(href);
        return ((AdminApp)field!.GetValue(null)!).Href;
    }
}

// The merged admin apps: one sidebar entry each, a tab per page.
public class AdminAppsTests
{
    [Theory]
    [InlineData("admin/app-generation-queue", "SentinelGPT Builder")]
    [InlineData("admin/deal-scoring?x=1", "CRM")]
    [InlineData("admin/community/profile/jdoe", "Community")]
    [InlineData("Admin/Appearance/Menus/", "Site design")]
    [InlineData("admin/affiliate-analytics#top", "Affiliate")]
    public void Contains_ShouldClaimEveryTabsPage_ForExactlyOneApp(string path, string expected) =>
        AdminApps.All.Where(app => app.Contains(path)).Select(app => app.Label).Should().Equal(expected);

    [Theory]
    [InlineData("admin/crm-import")]
    [InlineData("admin/app-generation-queue-old")]
    [InlineData("admin")]
    public void Contains_ShouldNotClaimLookalikePaths(string path) =>
        AdminApps.All.Should().NotContain(app => app.Contains(path));

    [Fact]
    public void Tabs_ShouldHideAdminOnlyPages_FromOtherRoles()
    {
        AdminApps.Community.TabsFor(AppRoles.Author).Select(tab => tab.Label).Should().Equal("Directory", "Activity", "Messages");
        AdminApps.Community.TabsFor(AppRoles.Admin).Should().HaveCount(4);
        AdminApps.PageBuilder.TabsFor(AppRoles.Contributor).Should().ContainSingle().Which.Href.Should().Be(AdminApps.PageBuilder.Href);
        AdminApps.All.SelectMany(app => app.Tabs).Select(tab => tab.Href).Should().OnlyHaveUniqueItems();
    }
}
