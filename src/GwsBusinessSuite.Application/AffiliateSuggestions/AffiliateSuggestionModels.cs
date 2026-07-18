namespace GwsBusinessSuite.Application.AffiliateSuggestions;

public sealed class AffiliateSuggestionView
{
    public Guid Id { get; init; }
    public string AdvertiserId { get; init; } = string.Empty;
    public string AdvertiserName { get; init; } = string.Empty;
    public string LinkName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string TrackingUrl { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public int Rank { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class ArticleSuggestionGroupView
{
    public Guid ArticleId { get; init; }
    public string ArticleTitle { get; init; } = string.Empty;
    public string ArticleSlug { get; init; } = string.Empty;
    public string ArticleStatus { get; init; } = string.Empty;
    public IReadOnlyList<AffiliateSuggestionView> Suggestions { get; init; } = Array.Empty<AffiliateSuggestionView>();
}

public sealed class GenerateSuggestionsResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public int ArticlesProcessed { get; init; }
    public int ArticlesFailed { get; init; }
    public int SuggestionsCreated { get; init; }
    public IReadOnlyList<string> FailureMessages { get; init; } = Array.Empty<string>();
}
