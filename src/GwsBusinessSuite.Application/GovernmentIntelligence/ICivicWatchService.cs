namespace GwsBusinessSuite.Application.GovernmentIntelligence;

// Everything Civic Watch keeps per install rather than per snapshot: the home location and the
// districts it falls in, keyword searches (Federal Register, Grants.gov), the bill watchlist,
// "what changed" sightings, county commission documents and upcoming elections. The fixed
// Georgia/Houston County briefing itself stays in IGovernmentIntelligenceService.
public interface ICivicWatchService
{
    // ---- Settings / location ----
    Task<CivicWatchSettingsView> GetSettingsAsync(CancellationToken ct = default);

    // Geocodes HomeAddress when it changed (otherwise uses the given coordinates), then resolves
    // the congressional and state legislative districts for the point.
    Task<CivicWatchSettingsView> SaveSettingsAsync(CivicWatchSettingsView settings, string username, CancellationToken ct = default);

    // ---- Your representatives ----
    Task<CivicRepresentatives> GetRepresentativesAsync(CancellationToken ct = default);

    // ---- Keyword feeds ----
    Task<CivicKeywordFeed<FederalRegisterDocument>> GetFederalRegisterAsync(bool refresh = false, CancellationToken ct = default);
    Task<CivicKeywordFeed<GrantOpportunity>> GetGrantOpportunitiesAsync(bool refresh = false, CancellationToken ct = default);

    // ---- Bill watchlist ----
    Task<IReadOnlyList<WatchedBillView>> ListWatchedBillsAsync(CancellationToken ct = default);

    // Accepts "HB 68", "SB 1", "HR 12", "SR 4" (Georgia) or "H.R. 1", "S. 25", "H.J.Res. 7"
    // (federal, current Congress). Looks the bill up immediately so a typo fails at once.
    Task<WatchedBillView> AddWatchedBillAsync(string jurisdiction, string billText, string username, CancellationToken ct = default);
    Task RemoveWatchedBillAsync(Guid id, CancellationToken ct = default);

    // Background: re-checks every watched bill, emails changes when alerts are on, and fires the
    // civic.billStatusChangedTrigger automation trigger. Returns the changes found.
    Task<IReadOnlyList<WatchedBillChange>> CheckWatchedBillsAsync(CancellationToken ct = default);

    // ---- What changed ----
    // Background: records the first time each snapshot item was seen.
    Task RecordSightingsAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default);
    Task<CivicChanges> GetChangesAsync(TimeSpan window, CancellationToken ct = default);

    // ---- County commission agendas and minutes ----
    Task<IReadOnlyList<CivicMeetingDocumentView>> ListMeetingDocumentsAsync(int take = 8, CancellationToken ct = default);

    // Background: finds new agenda/minutes PDFs and summarizes at most `maxSummaries` of them.
    Task RefreshMeetingDocumentsAsync(int maxSummaries = 2, CancellationToken ct = default);

    // ---- Elections ----
    Task<CivicElectionInfo> GetElectionsAsync(bool refresh = false, CancellationToken ct = default);
}

public sealed record CivicWatchSettingsView(
    string HomeLabel,
    double HomeLatitude,
    double HomeLongitude,
    string? HomeAddress,
    string? StateCode,
    string? CountyName,
    int? CongressionalDistrict,
    int? StateSenateDistrict,
    int? StateHouseDistrict,
    DateTimeOffset? DistrictsResolvedAt,
    string FederalRegisterKeywords,
    string GrantKeywords,
    bool AlertsEnabled,
    string? AlertRecipient)
{
    public IReadOnlyList<string> FederalRegisterKeywordList => CivicKeywords.Split(FederalRegisterKeywords);
    public IReadOnlyList<string> GrantKeywordList => CivicKeywords.Split(GrantKeywords);
}

public static class CivicKeywords
{
    public const int MaxKeywords = 6;

    public static IReadOnlyList<string> Split(string? text) =>
        (text ?? string.Empty)
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeywords)
            .ToList();
}

// Level: "federal" or "state". Chamber: "Senate" or "House". MemberId matches
// MemberVoteRecord.MemberId (federal) or StateMemberVoteRecord.MemberId (state, as a string).
public sealed record CivicRepresentative(
    string Level,
    string Chamber,
    string Name,
    string Party,
    string District,
    string MemberId,
    string? WebsiteUrl,
    string? Phone);

public sealed record CivicRepresentatives(
    IReadOnlyList<CivicRepresentative> Members,
    // Why the list is empty or partial (no districts yet, a source failed). Null when complete.
    string? Note);

public sealed record CivicKeywordFeed<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<string> Keywords,
    DateTimeOffset? RetrievedAt,
    string? Error);

public sealed record FederalRegisterDocument(
    string DocumentNumber,
    string Title,
    string Type,
    string Url,
    DateOnly? PublishedOn,
    DateOnly? CommentsCloseOn,
    string Agencies,
    string? Abstract,
    string MatchedKeyword)
{
    public bool IsOpenForComment(DateOnly today) => CommentsCloseOn is { } close && close >= today;
}

public sealed record GrantOpportunity(
    string Id,
    string Number,
    string Title,
    string Agency,
    string Status,
    DateOnly? OpenDate,
    DateOnly? CloseDate,
    string Url,
    string MatchedKeyword);

public sealed record WatchedBillView(
    Guid Id,
    string Jurisdiction,
    string Label,
    string Title,
    string LatestStatus,
    string? LatestStatusDate,
    string OfficialUrl,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastChangedAt,
    string? LastError);

public sealed record WatchedBillChange(
    Guid Id,
    string Jurisdiction,
    string Label,
    string Title,
    string PreviousStatus,
    string Status,
    string? StatusDate,
    string Url);

public sealed record CivicChangedItem(
    string Desk,
    string Kind,
    string Title,
    string Url,
    DateTimeOffset FirstSeenAt);

public sealed record CivicChanges(
    IReadOnlyList<CivicChangedItem> Items,
    // Keys (CivicItemKeys.For) of everything new in the window, for "New" badges.
    IReadOnlySet<string> NewKeys);

public static class CivicItemKeys
{
    public static string For(string desk, string url) => $"{desk}|{url}".ToLowerInvariant();
}

public sealed record CivicMeetingDocumentView(
    Guid Id,
    DateOnly MeetingDate,
    string Kind,
    string Title,
    string Url,
    string? AiSummary,
    string? LastError);

public sealed record CivicElection(
    string Name,
    DateOnly ElectionDate,
    string RegistrationDeadline);

public sealed record CivicElectionInfo(
    IReadOnlyList<CivicElection> Upcoming,
    IReadOnlyList<CivicResourceLink> Documents,
    DateTimeOffset? RetrievedAt,
    string? Error);
