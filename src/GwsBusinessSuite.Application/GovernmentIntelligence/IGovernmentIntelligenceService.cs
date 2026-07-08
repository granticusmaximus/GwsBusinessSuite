namespace GwsBusinessSuite.Application.GovernmentIntelligence;

public interface IGovernmentIntelligenceService
{
    Task<GovernmentIntelligenceSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken ct = default);
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
    IReadOnlyList<CivicResourceSection> ResourceSections);

public sealed record StateGovernmentCoverage(
    string Summary,
    IReadOnlyList<CivicUpdate> PressReleases,
    IReadOnlyList<LawSummary> SignedLegislation,
    IReadOnlyList<CivicResourceSection> ResourceSections);

public sealed record FederalGovernmentCoverage(
    string Summary,
    string StatusNote,
    IReadOnlyList<ChamberVoteSummary> SenateVotes,
    IReadOnlyList<ChamberVoteSummary> HouseVotes,
    IReadOnlyList<CivicResourceSection> ResourceSections);

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
    string Source);

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
    IReadOnlyList<MemberVoteRecord> Votes);

public sealed record MemberVoteRecord(
    string Name,
    string Party,
    string State,
    string Vote);
