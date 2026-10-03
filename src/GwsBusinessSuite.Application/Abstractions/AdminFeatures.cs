namespace GwsBusinessSuite.Application.Abstractions;

public sealed record AdminFeature(string Key, string DisplayName, string Group, string Reason);

// The admin areas that can be hidden from navigation, and why each is a candidate.
//
// Each was a candidate because production had no data in it when this was added - an observation
// about adoption, not worth. Nothing is hidden unless chosen in Settings (see DefaultHiddenKeys).
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

    // Nothing is hidden by default (changed 2026-10-03 at Grant's request - every area should be
    // in the menu). Hiding is purely opt-in from Settings > General > Navigation.
    public static IReadOnlyList<string> DefaultHiddenKeys => [];

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
