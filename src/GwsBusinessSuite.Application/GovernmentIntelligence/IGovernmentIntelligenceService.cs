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
    string? ImageUrl,
    // Which of the surrounding communities this event belongs to, so the Local events panel can
    // be filtered down to the ones worth driving to. Derived from the source and the free-text
    // Location by CivicPlaces.Resolve - the upstream calendars do not publish a city field.
    string City = CivicPlaces.Unknown,
    // Rough straight-line miles from Kathleen, for "within 15 minutes of me" style filtering.
    // Null when the city could not be resolved.
    double? MilesFromHome = null);

// The communities this watch covers, centred on Kathleen in unincorporated Houston County.
// Distances are straight-line miles from Kathleen and are for sorting/filtering only - they are
// not driving distances.
public static class CivicPlaces
{
    public const string Unknown = "Unknown";
    public const string Kathleen = "Kathleen";
    public const string Bonaire = "Bonaire";
    public const string WarnerRobins = "Warner Robins";
    public const string Perry = "Perry";
    public const string Centerville = "Centerville";
    public const string Byron = "Byron";
    public const string Macon = "Macon";
    public const string HoustonCounty = "Houston County";

    // Ordered nearest-first from Kathleen so a "closest to me" sort needs no extra data.
    public static readonly IReadOnlyDictionary<string, double> MilesFromKathleen =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [Kathleen] = 0,
            [Bonaire] = 3.5,
            [WarnerRobins] = 7,
            [HoustonCounty] = 7,
            [Perry] = 9,
            [Centerville] = 10,
            [Byron] = 12,
            [Macon] = 22,
        };

    public static double? MilesFor(string city) =>
        MilesFromKathleen.TryGetValue(city ?? string.Empty, out var miles) ? miles : null;

    // Kathleen, GA. Sources that publish venue coordinates (Eventbrite's schema.org markup does)
    // get a real per-venue distance instead of the coarse per-town figure above - the difference
    // is real, e.g. two Warner Robins venues 7.5 and 11.1 miles out.
    public const double HomeLatitude = 32.4610;
    public const double HomeLongitude = -83.6152;

    public static double MilesFromHomeTo(double latitude, double longitude)
    {
        const double earthRadiusMiles = 3958.8;
        var dLat = DegreesToRadians(latitude - HomeLatitude);
        var dLon = DegreesToRadians(longitude - HomeLongitude);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(DegreesToRadians(HomeLatitude)) * Math.Cos(DegreesToRadians(latitude))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusMiles * Math.Asin(Math.Sqrt(a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    // Source name and free-text location are all the upstream calendars give us, so the city is
    // recovered by matching known place names in either. Order matters: "Warner Robins" has to be
    // tested before "Robins" so the Robins Region Chamber's own name does not swallow it.
    public static string Resolve(string source, string location)
    {
        var haystack = $"{location} {source}";
        foreach (var candidate in new[]
                 { WarnerRobins, Kathleen, Bonaire, Centerville, Byron, Perry, Macon })
        {
            if (haystack.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        return haystack.Contains("Houston County", StringComparison.OrdinalIgnoreCase)
            ? HoustonCounty
            : Unknown;
    }
}

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
