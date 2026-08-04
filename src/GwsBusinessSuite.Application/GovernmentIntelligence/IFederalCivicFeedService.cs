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
}
