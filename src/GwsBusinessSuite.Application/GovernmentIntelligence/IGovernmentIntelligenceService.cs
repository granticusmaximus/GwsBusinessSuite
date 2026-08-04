namespace GwsBusinessSuite.Application.GovernmentIntelligence;

public interface IGovernmentIntelligenceService
{
    Task<GovernmentIntelligenceSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken ct = default);

    // Background-only: generates SentinelGPT overviews for Georgia legislation that doesn't
    // have one cached yet. Must never be called from a page-load path - see the comment on
    // the implementation in GovernmentIntelligenceService for why.
    Task PopulateAiOverviewsAsync(CancellationToken ct = default);
}

public sealed record GovernmentIntelligenceSnapshot(
    string AreaLabel,
    DateTimeOffset RetrievedAt,
    CommunityCoverage Community,
    StateGovernmentCoverage State,
    FederalGovernmentCoverage Federal);

public sealed record CommunityCoverage(
    string Summary,
    IReadOnlyList<CivicUpdate> Announcements,
    IReadOnlyList<CivicMeeting> Meetings,
    IReadOnlyList<CivicResourceSection> ResourceSections,
    IReadOnlyList<LegislationDetailBrief> LegislationBriefs,
    IReadOnlyList<CivicEvent> LocalEvents);

public sealed record StateGovernmentCoverage(
    string Summary,
    IReadOnlyList<CivicUpdate> PressReleases,
    IReadOnlyList<LawSummary> SignedLegislation,
    IReadOnlyList<StateLegislativeVoteSummary> HouseVotes,
    IReadOnlyList<StateLegislativeVoteSummary> SenateVotes,
    IReadOnlyList<CivicResourceSection> ResourceSections);

public sealed record FederalGovernmentCoverage(
    string Summary,
    string StatusNote,
    IReadOnlyList<ChamberVoteSummary> SenateVotes,
    IReadOnlyList<ChamberVoteSummary> HouseVotes,
    IReadOnlyList<CivicResourceSection> ResourceSections,
    IReadOnlyList<FederalNewsItem> SenateNews,
    IReadOnlyList<FederalNewsItem> HouseNews,
    FloorStatus SenateFloor,
    FloorStatus HouseFloor);

public sealed record FederalNewsItem(
    string Title,
    string Url,
    string? Description,
    DateTimeOffset? PublishedAt,
    string Source);

// LiveEmbedUrl is only populated when InSession - there's nothing live to show otherwise,
// and the UI renders only the video (no page chrome, no separate "watch" link) when present.
public sealed record FloorStatus(
    bool InSession,
    string StatusNote,
    string? LiveEmbedUrl,
    CongressionalTranscriptSummary? LatestTranscript);

// Congressional Record floor-proceedings text - official but same-day-delayed, not
// live-synced captioning. Excerpt is a short preview; full text lives in
// CongressionalFloorTranscript, queried separately for the archive/detail view.
public sealed record CongressionalTranscriptSummary(
    Guid Id,
    string Chamber,
    DateOnly SessionDate,
    string SourceUrl,
    string Excerpt,
    DateTimeOffset FetchedAt);

public sealed record CivicUpdate(
    string Title,
    string Url,
    string Summary,
    DateTimeOffset? PublishedAt,
    string Source);

public sealed record CivicMeeting(
    string Title,
    string Url,
    DateOnly? MeetingDate,
    string Location,
    string Source);

public sealed record CivicEvent(
    string Title,
    string Url,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    string Location,
    string Source,
    string? ImageUrl);

public sealed record CivicResourceSection(
    string Title,
    IReadOnlyList<CivicResourceLink> Links);

public sealed record CivicResourceLink(
    string Title,
    string Url,
    string Description);

public sealed record LawSummary(
    string DocumentNumber,
    string Title,
    string Url,
    string Source,
    LegislationDetailBrief? Legislation);

public sealed record ChamberVoteSummary(
    string Chamber,
    string RollCallNumber,
    string Measure,
    string Question,
    string Result,
    string Title,
    DateTimeOffset? VotedAt,
    string DetailUrl,
    int YeaCount,
    int NayCount,
    int PresentCount,
    int NotVotingCount,
    IReadOnlyList<MemberVoteRecord> Votes,
    LegislationDetailBrief? Legislation);

public sealed record StateLegislativeVoteSummary(
    string Chamber,
    string RollCallNumber,
    string Caption,
    string Measure,
    string Title,
    string Status,
    DateTimeOffset? VotedAt,
    string DetailUrl,
    int YeaCount,
    int NayCount,
    int NotVotingCount,
    int ExcusedCount,
    IReadOnlyList<StateMemberVoteRecord> Votes,
    LegislationDetailBrief? Legislation);

public sealed record StateMemberVoteRecord(
    string Name,
    string Vote);

public sealed record MemberVoteRecord(
    string Name,
    string Party,
    string State,
    string Vote);

public sealed record LegislationDetailBrief(
    string Kind,
    string Jurisdiction,
    string GoverningBody,
    string Measure,
    string Title,
    string Status,
    string Summary,
    string OfficialUrl,
    IReadOnlyList<LegislationFact> Facts,
    IReadOnlyList<LegislationLink> Links,
    IReadOnlyList<LegislationTimelineEntry> Timeline,
    // Null when Ollama is unavailable or hasn't summarized this bill yet - the UI simply
    // omits the SentinelGPT panel rather than showing an error.
    string? AiOverview = null);

public sealed record LegislationFact(
    string Label,
    string Value);

public sealed record LegislationLink(
    string Title,
    string Url,
    string Description);

public sealed record LegislationTimelineEntry(
    string Label,
    string Detail,
    string When);
