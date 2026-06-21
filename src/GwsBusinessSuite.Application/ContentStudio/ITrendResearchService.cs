namespace GwsBusinessSuite.Application.ContentStudio;

public interface ITrendResearchService
{
    Task<TrendResearchResult> ResearchTrendsAsync(TrendResearchRequest request, CancellationToken cancellationToken = default);
}