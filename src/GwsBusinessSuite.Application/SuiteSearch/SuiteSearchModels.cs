namespace GwsBusinessSuite.Application.SuiteSearch;

// Backs the suite-wide Cmd/Ctrl+K command palette (MainLayout.razor's static gws-command-*
// markup, wired up in app.js) - unlike that palette's existing client-side-only nav-entry
// search, this reaches into live data across modules so "search everything" actually means
// records, not just page names.
public sealed record SuiteSearchResult(string Title, string Subtitle, string Category, string Url, string IconClass);

public interface ISuiteSearchService
{
    Task<IReadOnlyList<SuiteSearchResult>> SearchAsync(string query, string performedBy, int take = 12, CancellationToken cancellationToken = default);

    // One structured search (SentinelGPT's find_records call, tech-forward T1b).
    Task<IReadOnlyList<SuiteSearchResult>> FindAsync(SuiteSearchRequest request, CancellationToken cancellationToken = default);
}

public static class SuiteSearchCategories
{
    public const string SentinelPage = "Sentinel page";
    public const string SentinelDatabase = "Sentinel database";
    public const string CrmContact = "CRM contact";
    public const string CrmDeal = "CRM deal";
    public const string Automation = "Automation";
    public const string CmsPage = "CMS page";
    public const string Article = "Article";
    public const string AffiliateOffer = "Affiliate offer";
    public const string SupportTicket = "Support ticket";
    public const string BillingInvoice = "Billing invoice";
}

// The palette's type filters (tech-forward T1): "t:ticket acme" (or "type:ticket") searches only
// tickets for "acme", and a filter with no text lists that kind's latest records. A word that
// isn't a known filter stays part of the text, so "t:foo" is just searched for.
public sealed record SuiteSearchQuery(string Text, IReadOnlySet<string> Categories)
{
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ticket"] = [SuiteSearchCategories.SupportTicket], ["tickets"] = [SuiteSearchCategories.SupportTicket], ["support"] = [SuiteSearchCategories.SupportTicket],
        ["invoice"] = [SuiteSearchCategories.BillingInvoice], ["invoices"] = [SuiteSearchCategories.BillingInvoice], ["billing"] = [SuiteSearchCategories.BillingInvoice],
        ["contact"] = [SuiteSearchCategories.CrmContact], ["contacts"] = [SuiteSearchCategories.CrmContact], ["crm"] = [SuiteSearchCategories.CrmContact],
        ["deal"] = [SuiteSearchCategories.CrmDeal], ["deals"] = [SuiteSearchCategories.CrmDeal],
        ["page"] = [SuiteSearchCategories.CmsPage], ["pages"] = [SuiteSearchCategories.CmsPage], ["cms"] = [SuiteSearchCategories.CmsPage],
        ["post"] = [SuiteSearchCategories.Article], ["posts"] = [SuiteSearchCategories.Article], ["article"] = [SuiteSearchCategories.Article], ["articles"] = [SuiteSearchCategories.Article],
        ["workflow"] = [SuiteSearchCategories.Automation], ["workflows"] = [SuiteSearchCategories.Automation], ["automation"] = [SuiteSearchCategories.Automation],
        ["sentinel"] = [SuiteSearchCategories.SentinelPage, SuiteSearchCategories.SentinelDatabase],
        ["note"] = [SuiteSearchCategories.SentinelPage, SuiteSearchCategories.SentinelDatabase], ["notes"] = [SuiteSearchCategories.SentinelPage, SuiteSearchCategories.SentinelDatabase],
        ["offer"] = [SuiteSearchCategories.AffiliateOffer], ["offers"] = [SuiteSearchCategories.AffiliateOffer], ["affiliate"] = [SuiteSearchCategories.AffiliateOffer],
    };

    public bool IsFiltered => Categories.Count > 0;

    public bool Wants(string category) => !IsFiltered || Categories.Contains(category);

    public static SuiteSearchQuery Parse(string? raw)
    {
        var categories = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>();
        foreach (var word in (raw ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = word.IndexOf(':');
            var prefix = colon > 0 ? word[..colon] : string.Empty;
            if ((prefix.Equals("t", StringComparison.OrdinalIgnoreCase) || prefix.Equals("type", StringComparison.OrdinalIgnoreCase))
                && Aliases.TryGetValue(word[(colon + 1)..], out var kinds))
            {
                categories.UnionWith(kinds);
                continue;
            }
            words.Add(word);
        }
        return new SuiteSearchQuery(string.Join(' ', words), categories);
    }
}

// Who sees which results: CRM, billing, support, automation and affiliate records live on
// admin-only pages, so other roles get only what they can open - their Sentinel pages (the
// workspace search already checks access), site pages and posts.
public static class SuiteSearchVisibility
{
    private static readonly HashSet<string> OpenToEveryone =
    [
        SuiteSearchCategories.SentinelPage, SuiteSearchCategories.SentinelDatabase,
        SuiteSearchCategories.CmsPage, SuiteSearchCategories.Article
    ];

    public static IReadOnlyList<SuiteSearchResult> For(IReadOnlyList<SuiteSearchResult> results, bool isAdmin) =>
        isAdmin ? results : results.Where(result => OpenToEveryone.Contains(result.Category)).ToList();
}
