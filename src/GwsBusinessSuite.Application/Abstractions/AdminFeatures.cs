namespace GwsBusinessSuite.Application.Abstractions;

public sealed record AdminFeature(string Key, string DisplayName, string Group, string Reason);

// The admin areas that can be hidden from navigation, and why each is a candidate.
//
// "Hidden by default" here means: production has never held a single row for that feature. That
// is an observation about adoption, not a judgement about worth - each one is complete, tested
// code that works the moment it is switched back on. The cost being addressed is not disk or
// compute, it is that 57 admin destinations make the twelve in daily use harder to find.
//
// Deliberately excluded from this list: anything whose emptiness is healthy (security incidents,
// privacy requests), and every sub-feature of an area that IS used - Wiki revisions, CMS
// categories, automation credentials and the Sentinel collaboration tables are all empty because
// they are the next step inside a live feature, or because this is a single-user deployment.
public static class AdminFeatures
{
    public static readonly IReadOnlyList<AdminFeature> All =
    [
        new("localization", "Localization", "Content & Publishing", "No translations have ever been created."),
        new("comments", "Comments", "Content & Publishing", "No visitor comment has ever been received."),
        new("podcasts", "Podcasts", "Content & Publishing", "No episodes stored; a separate podcast app already runs on the host."),
        new("app-generation", "SentinelGPT Builder", "Content & Publishing", "No generation request has ever been made."),
        new("crm", "CRM", "Relationships", "No contacts or deals exist."),
        new("billing", "Billing", "Relationships", "No invoices have been raised."),
        new("support", "Support", "Relationships", "No ticket has ever been opened."),
        new("scheduling", "Scheduling", "Relationships", "No bookings or booking types exist."),
        new("email-campaigns", "Email Campaigns", "Relationships", "No campaign has ever been created."),
    ];

    // Everything above starts hidden: each is empty in production today, and the whole point is
    // that the navigation reflects what is actually used. Anything hidden in error is one
    // checkbox away in Settings, which is why this is safe to default on.
    public static IReadOnlyList<string> DefaultHiddenKeys =>
        [.. All.Select(feature => feature.Key)];

    public static bool IsHidden(string? hiddenNavKeys, string key) =>
        Parse(hiddenNavKeys).Contains(key, StringComparer.OrdinalIgnoreCase);

    // A null column means "never configured", which takes the defaults. An empty string is a
    // deliberate "show me everything" and must not be re-defaulted, or the setting could never
    // be cleared.
    public static IReadOnlyList<string> Parse(string? hiddenNavKeys) =>
        hiddenNavKeys is null
            ? DefaultHiddenKeys
            : [.. hiddenNavKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static string Serialize(IEnumerable<string> keys) =>
        string.Join(',', keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.Ordinal));
}
