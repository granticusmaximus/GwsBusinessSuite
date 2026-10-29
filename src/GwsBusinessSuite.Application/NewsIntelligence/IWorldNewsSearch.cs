namespace GwsBusinessSuite.Application.NewsIntelligence;

// World News for SentinelGPT's answers on the web: the last week's events that the question is
// clearly about, as reading for the model plus the sources to cite. Implemented over
// WorldNewsService in Infrastructure, so this layer needn't reference WorldNewsKit.
public interface IWorldNewsSearch
{
    // Null when no event matches strongly enough to be what the question is about.
    Task<WorldNewsContext?> ContextAsync(string question, CancellationToken cancellationToken = default);
}

public sealed record WorldNewsContext(string Text, IReadOnlyList<WorldNewsContextSource> Sources);

public sealed record WorldNewsContextSource(string Title, string Url);
