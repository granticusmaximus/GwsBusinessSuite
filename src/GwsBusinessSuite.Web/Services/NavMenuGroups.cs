namespace GwsBusinessSuite.Web.Services;

// One admin workspace: a rail button (icon + short label) that shows its pages in the sub-nav.
public sealed record NavWorkspace(string Key, string Label, string ShortLabel, string Icon, bool AdminOnly, IReadOnlyList<string> Prefixes);

// The sidebar's route-prefix-to-workspace mapping, kept out of NavMenu.razor so it is
// unit-testable without a component test harness. The workspaces are the 2026-09 design-plan
// groups (Content & Publishing / Relationships / Knowledge / Intelligence / Growth & Monetization /
// Platform) plus Home; since the 19-10-26 shell redesign (R1) each is a rail button, and the
// current page's workspace is the one shown on load. Routes no workspace claims (the dashboard,
// Settings, Touch ID) show Home.
public static class NavMenuGroups
{
    public static readonly IReadOnlyList<string> HomePrefixes = ["admin/community"];

    public static readonly IReadOnlyList<string> ContentPublishingPrefixes =
        ["admin/article", "admin/form-submissions", "admin/content-studio", "admin/content-calendar", "admin/seo-audit", "admin/localization", "admin/comments", "admin/app-generation", "admin/media", "admin/pages", "admin/cms-knowledge", "admin/appearance", "admin/podcasts", "admin/live-show"];

    public static readonly IReadOnlyList<string> RelationshipPrefixes =
        ["admin/crm", "admin/deal-scoring", "admin/billing", "admin/time", "admin/support", "admin/scheduling", "admin/email-campaigns", "admin/users"];

    public static readonly IReadOnlyList<string> KnowledgePrefixes =
        ["admin/sentinel", "admin/wiki", "admin/boards", "admin/my-day", "admin/mind-maps"];

    public static readonly IReadOnlyList<string> IntelligencePrefixes =
        ["admin/news-intelligence", "admin/world-news", "admin/government-intelligence", "admin/business-intelligence", "admin/osint", "admin/threat-intel"];

    public static readonly IReadOnlyList<string> GrowthMonetizationPrefixes =
        ["admin/growth", "admin/cj-ads", "admin/affiliate"];

    public static readonly IReadOnlyList<string> PlatformPrefixes =
        ["admin/automation", "admin/docker", "admin/dev-tools", "admin/security-audit", "admin/privacy-operations"];

    public static readonly NavWorkspace Home =
        new("home", "Home", "Home", "bi-house-door-fill", false, HomePrefixes);

    public static readonly IReadOnlyList<NavWorkspace> Workspaces =
    [
        Home,
        new("content", "Content & Publishing", "Content", "bi-pencil-square", false, ContentPublishingPrefixes),
        new("relationships", "Relationships", "Relations", "bi-briefcase-fill", true, RelationshipPrefixes),
        new("knowledge", "Knowledge", "Knowledge", "bi-journal-bookmark-fill", true, KnowledgePrefixes),
        new("intelligence", "Intelligence", "Intel", "bi-radar", true, IntelligencePrefixes),
        new("growth", "Growth & Monetization", "Growth", "bi-graph-up-arrow", true, GrowthMonetizationPrefixes),
        new("platform", "Platform", "Platform", "bi-cpu-fill", true, PlatformPrefixes)
    ];

    // The workspace a base-relative path (e.g. "admin/crm/contacts/1?tab=x") belongs to.
    public static NavWorkspace WorkspaceFor(string relativePath)
    {
        var path = relativePath.TrimStart('/');
        return Workspaces.FirstOrDefault(workspace =>
            workspace.Prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) ?? Home;
    }
}
