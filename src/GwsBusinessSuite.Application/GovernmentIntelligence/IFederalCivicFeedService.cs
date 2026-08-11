namespace GwsBusinessSuite.Application.GovernmentIntelligence;

// Mirrors ILocalEventsScraperService's split: RefreshAsync does real I/O (HTTP + a DB
// upsert for the transcript archive) and is only ever called by
// FederalCivicRefreshBackgroundService on its own hourly cadence; the GetCached...OrEmpty
// reads are pure cache lookups, safe to call inline from
// GovernmentIntelligenceService.BuildFederalCoverageAsync on the 15-minute snapshot path.
public interface IFederalCivicFeedService
{
    Task RefreshAsync(CancellationToken ct = default);

    IReadOnlyList<FederalNewsItem> GetCachedSenateNewsOrEmpty();
    IReadOnlyList<FederalNewsItem> GetCachedHouseNewsOrEmpty();
    FloorStatus GetCachedSenateFloorOrEmpty();
    FloorStatus GetCachedHouseFloorOrEmpty();

    // The archive/detail view CongressionalTranscriptSummary's own doc comment refers to -
    // FloorStatus.LatestTranscript only ever surfaces the single newest transcript per
    // chamber, so this is the only way to see everything RefreshAsync has archived over time.
    // Not cache-backed like the other reads here since it's a real DB query hit only when an
    // admin opens the archive view, not on every 15-minute snapshot cycle.
    Task<IReadOnlyList<CongressionalTranscriptSummary>> ListTranscriptArchiveAsync(
        string? chamber = null, int take = 50, CancellationToken ct = default);
}
