using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

public class NavMenuGroupsTests
{
    private const string NoGroup = "(none)";

    public static IEnumerable<object[]> KnownRoutesByGroup =>
    [
        // Content & Publishing
        ["admin/article-editor", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/content-studio", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/content-calendar", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/seo-audit", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/localization", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/app-generation", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/app-generation-queue", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/media", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/pages", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/appearance/menus", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/appearance/customize", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/podcasts", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/live-show", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/live-show-recordings", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/comments", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/cms-knowledge", nameof(NavMenuGroups.ContentPublishingPrefixes)],

        // Relationships
        ["admin/crm", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/deal-scoring", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/billing", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/time", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/support", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/scheduling", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/email-campaigns", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/users", nameof(NavMenuGroups.RelationshipPrefixes)],

        // Community is a top-level entry, so its pages open no group.
        ["admin/community", NoGroup],
        ["admin/community/activity", NoGroup],
        ["admin/community/messages", NoGroup],
        ["admin/community/departments", NoGroup],
        ["admin/community/profile/jdoe", NoGroup],

        // Knowledge - admin/wiki has no NavLink of its own (Wiki.razor also routes at
        // admin/sentinel, which does) but must still keep the Knowledge group expanded.
        ["admin/sentinel", nameof(NavMenuGroups.KnowledgePrefixes)],
        ["admin/wiki", nameof(NavMenuGroups.KnowledgePrefixes)],
        ["admin/mind-maps", nameof(NavMenuGroups.KnowledgePrefixes)],

        // Intelligence
        ["admin/news-intelligence", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/government-intelligence", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/business-intelligence", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/osint", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/threat-intel", nameof(NavMenuGroups.IntelligencePrefixes)],

        // Growth & Monetization
        ["admin/growth", nameof(NavMenuGroups.GrowthMonetizationPrefixes)],
        ["admin/cj-ads", nameof(NavMenuGroups.GrowthMonetizationPrefixes)],
        ["admin/affiliate-suggestions", nameof(NavMenuGroups.GrowthMonetizationPrefixes)],
        ["admin/affiliate-analytics", nameof(NavMenuGroups.GrowthMonetizationPrefixes)],

        // Platform
        ["admin/automation", nameof(NavMenuGroups.PlatformPrefixes)],
        ["admin/automation/credentials", nameof(NavMenuGroups.PlatformPrefixes)],
        ["admin/automation/help", nameof(NavMenuGroups.PlatformPrefixes)],
        ["admin/docker-health", nameof(NavMenuGroups.PlatformPrefixes)],
        ["admin/dev-tools", nameof(NavMenuGroups.PlatformPrefixes)],
        ["admin/security-audit", nameof(NavMenuGroups.PlatformPrefixes)],
        ["admin/privacy-operations", nameof(NavMenuGroups.PlatformPrefixes)],
    ];

    private static readonly Dictionary<string, IReadOnlyList<string>> AllGroups = new()
    {
        [nameof(NavMenuGroups.ContentPublishingPrefixes)] = NavMenuGroups.ContentPublishingPrefixes,
        [nameof(NavMenuGroups.RelationshipPrefixes)] = NavMenuGroups.RelationshipPrefixes,
        [nameof(NavMenuGroups.KnowledgePrefixes)] = NavMenuGroups.KnowledgePrefixes,
        [nameof(NavMenuGroups.IntelligencePrefixes)] = NavMenuGroups.IntelligencePrefixes,
        [nameof(NavMenuGroups.GrowthMonetizationPrefixes)] = NavMenuGroups.GrowthMonetizationPrefixes,
        [nameof(NavMenuGroups.PlatformPrefixes)] = NavMenuGroups.PlatformPrefixes,
    };

    [Theory]
    [MemberData(nameof(KnownRoutesByGroup))]
    public void IsGroupOpen_ShouldMatchExactlyItsOwnGroup_ForEveryKnownAdminRoute(string route, string expectedGroupName)
    {
        foreach (var (groupName, prefixes) in AllGroups)
        {
            var isOpen = NavMenuGroups.IsGroupOpen(route, prefixes);
            isOpen.Should().Be(groupName == expectedGroupName,
                $"route '{route}' should only open '{expectedGroupName}', not '{groupName}'");
        }
    }

    [Fact]
    public void IsGroupOpen_ShouldBeCaseInsensitive()
    {
        NavMenuGroups.IsGroupOpen("ADMIN/CRM", NavMenuGroups.RelationshipPrefixes).Should().BeTrue();
    }

    [Fact]
    public void IsGroupOpen_ShouldReturnFalse_ForUnrelatedRoute()
    {
        NavMenuGroups.IsGroupOpen("admin", NavMenuGroups.ContentPublishingPrefixes).Should().BeFalse();
        NavMenuGroups.IsGroupOpen("admin/settings", NavMenuGroups.PlatformPrefixes).Should().BeFalse();
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
