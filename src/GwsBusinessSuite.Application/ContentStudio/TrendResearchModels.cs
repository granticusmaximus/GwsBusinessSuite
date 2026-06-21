namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class TrendResearchRequest
{
    public string FocusArea { get; init; } = string.Empty;
    public bool ForceRefresh { get; init; }
}

public sealed class TrendSignal
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int Score { get; init; }
    public int CommentCount { get; init; }
}

public sealed class TrendTopicSuggestion
{
    public string Topic { get; init; } = string.Empty;
    public string PrimaryKeyword { get; init; } = string.Empty;
    public string SecondaryKeywords { get; init; } = string.Empty;
    public string Rationale { get; init; } = string.Empty;
    public string PositiveTake { get; init; } = string.Empty;
    public string NegativeTake { get; init; } = string.Empty;
}

public sealed record TrendResearchResult
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string FocusArea { get; init; } = string.Empty;
    public string OverallSummary { get; init; } = string.Empty;
    public IReadOnlyList<TrendSignal> Signals { get; init; } = Array.Empty<TrendSignal>();
    public IReadOnlyList<TrendTopicSuggestion> Suggestions { get; init; } = Array.Empty<TrendTopicSuggestion>();
    public bool FromCache { get; init; }
}