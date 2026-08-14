namespace GwsBusinessSuite.Application.Scoring;

public static class DealScoreBands
{
    public const string Hot = "Hot";
    public const string Warm = "Warm";
    public const string Cool = "Cool";
    public const string Cold = "Cold";
}

// One line item in a score's explanation - Label describes the signal, Points is what it
// contributed (positive or negative), Detail is the human-readable "why" behind the number.
// A score is only useful to a salesperson if they can see what drove it - this is never
// hidden behind a single opaque number.
public sealed record DealScoreFactor(string Label, int Points, string Detail);

public sealed record DealScoreView(
    Guid DealId,
    string DealTitle,
    string ContactName,
    string Stage,
    decimal ValueUsd,
    int Score,
    string Band,
    IReadOnlyList<DealScoreFactor> Factors);

// The tenant-wide historical stats every individual deal's score is measured against -
// exposed alongside the per-deal scores so "why is the baseline 62%?" is always answerable
// from the same page, not a black box.
public sealed record DealScoringBaseline(
    int ClosedDealCount,
    int WonDealCount,
    double HistoricalWinRatePercent,
    double? AverageWonDealAgeDays);

public sealed record DealScoringResult(DealScoringBaseline Baseline, IReadOnlyList<DealScoreView> Deals);

public interface IDealScoringService
{
    // Scores every currently-open deal (DealStages.Open) against the tenant's own historical
    // Won/Lost outcomes - a heuristic, explainable model over this tenant's real data, not a
    // trained classifier (there's no training pipeline or model file behind this).
    Task<DealScoringResult> ScoreOpenDealsAsync(CancellationToken cancellationToken = default);
    Task<DealScoreView?> ScoreDealAsync(Guid dealId, CancellationToken cancellationToken = default);
}
