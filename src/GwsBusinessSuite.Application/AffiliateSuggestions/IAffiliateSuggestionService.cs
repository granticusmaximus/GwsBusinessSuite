namespace GwsBusinessSuite.Application.AffiliateSuggestions;

public interface IAffiliateSuggestionService
{
    // Clears any existing Pending suggestions for the article and asks Ollama to pick up to
    // 3 fresh ones from the current AffiliateOffer catalog. Applied/Dismissed suggestions for
    // the article are left alone (they're history, not something to silently regenerate over).
    Task<GenerateSuggestionsResult> GenerateForArticleAsync(Guid articleId, CancellationToken cancellationToken = default);

    Task<GenerateSuggestionsResult> GenerateForAllArticlesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArticleSuggestionGroupView>> ListPendingSuggestionsAsync(CancellationToken cancellationToken = default);

    // Turns a suggestion into a real ArticleAffiliatePlacement (and inserts its slot token
    // into the article's markdown, spread across the body rather than clustered) - see
    // ArticleMarkdownRenderer for how the token later resolves into an ad card at render time.
    Task ApplySuggestionAsync(Guid suggestionId, CancellationToken cancellationToken = default);

    Task ApplyAllForArticleAsync(Guid articleId, CancellationToken cancellationToken = default);

    Task DismissSuggestionAsync(Guid suggestionId, CancellationToken cancellationToken = default);
}
