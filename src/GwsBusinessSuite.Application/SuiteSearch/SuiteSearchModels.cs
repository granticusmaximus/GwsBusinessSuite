namespace GwsBusinessSuite.Application.SuiteSearch;

// Backs the suite-wide Cmd/Ctrl+K command palette (MainLayout.razor's static gws-command-*
// markup, wired up in app.js) - unlike that palette's existing client-side-only nav-entry
// search, this reaches into live data across modules so "search everything" actually means
// records, not just page names.
public sealed record SuiteSearchResult(string Title, string Subtitle, string Category, string Url, string IconClass);

public interface ISuiteSearchService
{
    Task<IReadOnlyList<SuiteSearchResult>> SearchAsync(string query, string performedBy, int take = 12, CancellationToken cancellationToken = default);
}
