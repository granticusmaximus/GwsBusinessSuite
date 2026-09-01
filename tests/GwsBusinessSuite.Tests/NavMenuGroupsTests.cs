using FluentAssertions;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

public class NavMenuGroupsTests
{
    public static IEnumerable<object[]> KnownRoutesByGroup =>
    [
        // Content & Publishing
        ["admin/article-editor", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/content-studio", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/seo-audit", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/localization", nameof(NavMenuGroups.ContentPublishingPrefixes)],
        ["admin/growth", nameof(NavMenuGroups.ContentPublishingPrefixes)],
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

        // Relationships
        ["admin/crm", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/deal-scoring", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/billing", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/support", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/scheduling", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/email-campaigns", nameof(NavMenuGroups.RelationshipPrefixes)],
        ["admin/users", nameof(NavMenuGroups.RelationshipPrefixes)],

        // Knowledge - admin/wiki has no NavLink of its own (Wiki.razor also routes at
        // admin/sentinel, which does) but must still keep the Knowledge group expanded.
        ["admin/sentinel", nameof(NavMenuGroups.KnowledgePrefixes)],
        ["admin/wiki", nameof(NavMenuGroups.KnowledgePrefixes)],
        ["admin/mind-maps", nameof(NavMenuGroups.KnowledgePrefixes)],
        ["admin/cms-knowledge", nameof(NavMenuGroups.KnowledgePrefixes)],

        // Intelligence
        ["admin/news-intelligence", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/government-intelligence", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/business-intelligence", nameof(NavMenuGroups.IntelligencePrefixes)],
        ["admin/osint", nameof(NavMenuGroups.IntelligencePrefixes)],

        // Growth & Monetization
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
