namespace GwsBusinessSuite.Application.GovernmentIntelligence;

// Everything Civic Watch keeps per install rather than per snapshot: the home location and the
// districts it falls in, keyword searches (Federal Register, Grants.gov), the bill watchlist,
// "what changed" sightings, county commission documents and upcoming elections. The fixed
// Georgia/Houston County briefing itself stays in IGovernmentIntelligenceService.
public interface ICivicWatchService
{
    // ---- Settings / location ----
    Task<CivicWatchSettingsView> GetSettingsAsync(CancellationToken ct = default);

    // Settings per person (20-11-26): their own when saved, else the shared default.
    Task<CivicWatchSettingsView> GetSettingsAsync(string? username, CancellationToken ct = default);
    Task<CivicWatchSettingsView> SaveSettingsAsync(CivicWatchSettingsView settings, string username, bool forEveryone, CancellationToken ct = default);

    // Geocodes HomeAddress when it changed (otherwise uses the given coordinates), then resolves
    // the congressional and state legislative districts for the point.
    Task<CivicWatchSettingsView> SaveSettingsAsync(CivicWatchSettingsView settings, string username, CancellationToken ct = default);

    // ---- Your representatives ----
    Task<CivicRepresentatives> GetRepresentativesAsync(CancellationToken ct = default);
    Task<CivicRepresentatives> GetRepresentativesAsync(string? username, CancellationToken ct = default);

    // ---- Keyword feeds ----
    Task<CivicKeywordFeed<FederalRegisterDocument>> GetFederalRegisterAsync(bool refresh = false, CancellationToken ct = default);
    Task<CivicKeywordFeed<GrantOpportunity>> GetGrantOpportunitiesAsync(bool refresh = false, CancellationToken ct = default);
    Task<CivicKeywordFeed<FederalRegisterDocument>> GetFederalRegisterAsync(string? username, bool refresh = false, CancellationToken ct = default);
    Task<CivicKeywordFeed<GrantOpportunity>> GetGrantOpportunitiesAsync(string? username, bool refresh = false, CancellationToken ct = default);

    // ---- Bill watchlist ----
    Task<IReadOnlyList<WatchedBillView>> ListWatchedBillsAsync(CancellationToken ct = default);

    // Accepts "HB 68", "SB 1", "HR 12", "SR 4" (Georgia) or "H.R. 1", "S. 25", "H.J.Res. 7"
    // (federal, current Congress). Looks the bill up immediately so a typo fails at once.
    Task<WatchedBillView> AddWatchedBillAsync(string jurisdiction, string billText, string username, CancellationToken ct = default);
    Task RemoveWatchedBillAsync(Guid id, CancellationToken ct = default);

    // Georgia bills about some words (20-11-26), current session first, to follow from the list.
    Task<IReadOnlyList<GeorgiaBillSearchHit>> SearchGeorgiaBillsAsync(string query, CancellationToken ct = default);

    // Background: re-checks every watched bill, emails changes when alerts are on, and fires the
    // civic.billStatusChangedTrigger automation trigger. Returns the changes found.
    Task<IReadOnlyList<WatchedBillChange>> CheckWatchedBillsAsync(CancellationToken ct = default);

    // ---- What changed ----
    // Background: records the first time each snapshot item was seen.
    Task RecordSightingsAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default);
    Task<CivicChanges> GetChangesAsync(TimeSpan window, CancellationToken ct = default);

    // What changed since your last visit (20-11-26): records this visit and returns when the one
    // before was (null on a first visit). Reloads within half an hour keep the same mark.
    Task<DateTimeOffset?> MarkVisitAsync(string username, CancellationToken ct = default);
    Task<CivicChanges> GetChangesSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    // Every word must appear; newest meetings first (20-11-26).
    Task<IReadOnlyList<CivicMeetingSearchHit>> SearchMeetingDocumentsAsync(string query, CancellationToken ct = default);

    // ---- County commission agendas and minutes ----
    Task<IReadOnlyList<CivicMeetingDocumentView>> ListMeetingDocumentsAsync(int take = 8, CancellationToken ct = default);

    // Background: finds new agenda/minutes PDFs and summarizes at most `maxSummaries` of them.
    Task RefreshMeetingDocumentsAsync(int maxSummaries = 2, CancellationToken ct = default);

    // ---- Elections ----
    Task<CivicElectionInfo> GetElectionsAsync(bool refresh = false, CancellationToken ct = default);

    // ---- Features (20-11-26) ----
    // 1. Calendar subscription: the person's secret link token (made on first use, or renewed),
    // and the feed for a token (null when no one has it).
    Task<string> GetCalendarTokenAsync(string username, bool renew = false, CancellationToken ct = default);
    Task<string?> CalendarForTokenAsync(string token, GovernmentIntelligenceSnapshot? snapshot, CancellationToken ct = default);
    Task<IReadOnlyList<CivicCalendarItem>> CalendarItemsAsync(string? username, GovernmentIntelligenceSnapshot? snapshot, CancellationToken ct = default);

    // 2. Agenda items followed across meetings.
    Task<IReadOnlyList<CivicFollowedItem>> GetFollowedItemsAsync(string username, CancellationToken ct = default);
    Task SetFollowedItemsAsync(string username, string phrases, CancellationToken ct = default);

    // 3. The school board's meetings (Houston County Board of Education).
    Task<SchoolBoardDesk> GetSchoolBoardAsync(bool refresh = false, CancellationToken ct = default);

    // 4. The weekly civic page in Sentinel (Mondays from 07:00, for readers of the last 30 days).
    Task<int> RunWeeklyPagesIfDueAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default);
    Task<Guid> BuildWeeklyPageAsync(string username, GovernmentIntelligenceSnapshot? snapshot, CancellationToken ct = default);

    // 5. Representative scorecards from the roll calls kept.
    Task RecordMemberVotesAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default);
    Task<IReadOnlyList<CivicScorecard>> GetScorecardsAsync(string? username, CancellationToken ct = default);

    // 6. A drafted public comment on a Federal Register document, from the person's position.
    Task<(string? Text, string? Problem)> DraftPublicCommentAsync(FederalRegisterDocument document, string position, CancellationToken ct = default);
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
    string? AlertRecipient,
    // True when these are the person's own settings rather than the shared default (20-11-26).
    bool Personal = false,
    // Streets and places to look for in the county's meeting papers, one per line.
    string MentionTerms = "")
{
    public IReadOnlyList<string> FederalRegisterKeywordList => CivicKeywords.Split(FederalRegisterKeywords);
    public IReadOnlyList<string> GrantKeywordList => CivicKeywords.Split(GrantKeywords);
    public IReadOnlyList<string> MentionTermList => CivicMentions.Split(MentionTerms);
}

// A Georgia bill found by words: its label to follow it by ("HB 68"), caption and status.
public sealed record GeorgiaBillSearchHit(string Label, string Caption, string Status, string? StatusDate, string Url, bool CurrentSession);

// A meeting paper that matched a search, with the passage around the first match.
public sealed record CivicMeetingSearchHit(Guid Id, DateOnly MeetingDate, string Kind, string Title, string Url, string Snippet);

// Places to look for in the county's meeting papers (20-11-26).
public static class CivicMentions
{
    public const int MaxTerms = 10;

    public static IReadOnlyList<string> Split(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTerms).ToList();

    // The terms a document's text names, whole words only, ignoring case.
    public static IReadOnlyList<string> Found(string? text, IEnumerable<string> terms) =>
        string.IsNullOrWhiteSpace(text) ? [] : terms.Where(term =>
            System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToList();

    // Every word must appear; the passage around the first one.
    public static string? Snippet(string? text, string query, int radius = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 0).ToList();
        if (words.Count == 0 || words.Any(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0)) return null;
        var at = text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at - radius);
        var end = Math.Min(text.Length, at + words[0].Length + radius);
        var passage = System.Text.RegularExpressions.Regex.Replace(text[start..end], @"\s+", " ").Trim();
        return (start > 0 ? "…" : string.Empty) + passage + (end < text.Length ? "…" : string.Empty);
    }
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
    string? LastError,
    // Who added it (their weekly page lists their own bills).
    string? AddedBy = null);

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
