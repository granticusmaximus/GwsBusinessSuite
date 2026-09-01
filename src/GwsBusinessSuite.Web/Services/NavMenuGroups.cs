namespace GwsBusinessSuite.Web.Services;

// The sidebar's route-prefix-to-group mapping, extracted out of NavMenu.razor so the grouping
// itself (which page lands in which collapsible section, and which prefixes auto-expand a
// section on load) is unit-testable without a component test harness - see the 2026-09
// design-plan nav regroup (Content & Publishing / Relationships / Knowledge / Intelligence /
// Growth & Monetization / Platform).
public static class NavMenuGroups
{
    public static readonly IReadOnlyList<string> ContentPublishingPrefixes =
        ["admin/article", "admin/content-studio", "admin/seo-audit", "admin/localization", "admin/comments", "admin/growth", "admin/app-generation", "admin/media", "admin/pages", "admin/appearance", "admin/podcasts", "admin/live-show"];

    public static readonly IReadOnlyList<string> RelationshipPrefixes =
        ["admin/crm", "admin/deal-scoring", "admin/billing", "admin/support", "admin/scheduling", "admin/email-campaigns", "admin/users"];

    public static readonly IReadOnlyList<string> KnowledgePrefixes =
        ["admin/sentinel", "admin/wiki", "admin/mind-maps", "admin/cms-knowledge"];

    public static readonly IReadOnlyList<string> IntelligencePrefixes =
        ["admin/news-intelligence", "admin/government-intelligence", "admin/business-intelligence", "admin/osint"];

    public static readonly IReadOnlyList<string> GrowthMonetizationPrefixes =
        ["admin/cj-ads", "admin/affiliate"];

    public static readonly IReadOnlyList<string> PlatformPrefixes =
        ["admin/automation", "admin/docker", "admin/dev-tools", "admin/security-audit", "admin/privacy-operations"];

    public static bool IsGroupOpen(string path, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
