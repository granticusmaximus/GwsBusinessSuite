using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class GovernmentIntelligenceService(
    HttpClient http,
    IMemoryCache cache,
    ILogger<GovernmentIntelligenceService> logger) : IGovernmentIntelligenceService
{
    private const string SnapshotCacheKey = "government-intelligence:snapshot";
    private const string CountyHomeUrl = "https://www.houstoncountyga.gov/";
    private const string CountyAnnouncementsUrl = "https://www.houstoncountyga.gov/residents/announcements.cms";
    private const string CountyCalendarUrl = "https://www.houstoncountyga.gov/commissioner/calendar.cms";
    private const string CountyResidentsUrl = "https://www.houstoncountyga.gov/residents/";
    private const string CountyAlertsUrl = "https://www.smart911.com/";
    private const string CountyElectionsUrl = "https://www.houstoncountyga.gov/residents/board-of-elections.cms";
    private const string CountyCodeUrl = "https://www.municode.com/library/ga/houston_county/codes/code_of_ordinances";
    private const string SchoolsHomeUrl = "https://www.hcbe.net/";
    private const string SchoolsCalendarUrl = "https://www.hcbe.net/calendar";
    private const string SchoolsBoardVideoUrl = "https://www.hcbe.net/boardmeetingvideo";
    private const string SchoolsSimbliUrl = "https://simbli.eboardsolutions.com/Index.aspx?S=4089";
    private const string SchoolsLegislativePrioritiesUrl = "https://content.myconnectsuite.com/api/documents/3e0a634afd894b21ae1b4ea6b6e3e113";
    private const string SchoolsNewsUrl = "https://www.hcbe.net/newsmedia";
    private const string GovernorPressReleasesUrl = "https://gov.georgia.gov/press-releases";
    private const string GovernorLegislationUrl = "https://gov.georgia.gov/executive-action/legislation";
    private const string GovernorSignedLegislationUrl = "https://gov.georgia.gov/executive-action/legislation/signed-legislation/2026";
    private const string GovernorVetoedLegislationUrl = "https://gov.georgia.gov/executive-action/legislation/vetoed-legislation/2026";
    private const string GeorgiaGeneralAssemblyUrl = "https://www.legis.ga.gov/";
    private const string GeorgiaApiBaseUrl = "https://www.legis.ga.gov/api/";
    private const string GeorgiaAuthenticationTokenUrl = "https://www.legis.ga.gov/api/authentication/token";
    private const string GeorgiaAllLegislationUrl = "https://www.legis.ga.gov/legislation/all";
    private const string GeorgiaSignedByGovernorUrl = "https://www.legis.ga.gov/legislation/signed-by-governor";
    private const string GeorgiaHouseVotesUrl = "https://www.legis.ga.gov/votes/house";
    private const string GeorgiaSenateVotesUrl = "https://www.legis.ga.gov/votes/senate";
    private const string GeorgiaLegislationPageUrl = "https://www.legis.ga.gov/legislation/";
    private const string GeorgiaApiObscureKey = "jVEXFFwSu36BwwcP83xYgxLAhLYmKk";
    private const string SenateSummaryUrl = "https://www.senate.gov/legislative/LIS/roll_call_lists/vote_menu_119_2.xml";
    private const string SenateVotesUrl = "https://www.senate.gov/legislative/LIS/roll_call_lists/vote_menu_119_2.htm";
    private const string HouseVotesListUrl = "https://clerk.house.gov/Votes/MemberVotes?CongressNum=119&Session=2nd";
    private const string HouseVotesIndexUrl = "https://clerk.house.gov/Votes";
    private const string CongressSearchUrl = "https://www.congress.gov/search?q=%7B%22source%22:%22legislation%22%7D";
    private const string CongressPublicLawsUrl = "https://www.congress.gov/public-laws/119th-congress";
    private const string AreaLabel = "Kathleen, Houston County, Georgia";
    private const string GeorgiaAccessTokenCacheKey = "government-intelligence:georgia:token";
    private const string GeorgiaCurrentSessionCacheKey = "government-intelligence:georgia:session";
    private const int GeorgiaHouseChamber = 1;
    private const int GeorgiaSenateChamber = 2;
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GeorgiaAccessTokenCacheDuration = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan GeorgiaSessionCacheDuration = TimeSpan.FromHours(6);
    private const int MaxCountyAnnouncements = 6;
    private const int MaxCountyMeetings = 6;
    private const int MaxStatePressReleases = 8;
    private const int MaxStateSignedLaws = 16;
    private const int MaxStateVotesPerChamber = 4;
    private const int MaxStateVoteCandidatesPerChamber = 12;
    private const int MaxFederalVotesPerChamber = 4;
    private static readonly JsonSerializerOptions GeorgiaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex CountyAnnouncementRegex = new(
        """<a\s+class="news"\s+href="(?<href>[^"]+)".*?<h4>(?<title>.*?)</h4>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CountyMeetingRegex = new(
        """<a\s+class="event"\s+href="(?<href>[^"]+)".*?<div\s+class="month">(?<month>.*?)</div>.*?<div\s+class="day-num">(?<day>.*?)</div>.*?<p>(?<title>.*?)</p>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GeorgiaPressReleaseRegex = new(
        """<a\s+href="(?<href>/press-releases/[^"]+)"\s+class="global-teaser.*?<h2\s+class="global-teaser__title">\s*(?<title>.*?)\s*</h2>.*?<div\s+class="global-teaser__description">(?<date>.*?)</div>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GeorgiaSignedLawRegex = new(
        """<td\s+headers="view-rendered-entity-table-column"[^>]*>.*?<a\s+href="(?<href>[^"]+)"[^>]*data-text="(?<document>[^"]+)".*?</a>.*?</td>\s*<td\s+headers="view-field-document-description-table-column"[^>]*>(?<title>.*?)</td>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HouseVoteIdentifierRegex = new(
        """href="/Votes/(?<id>20\d{2}\d+)(?:\?Page=\d+)?"[^>]*aria-label="Roll number,\s*\d+""" ,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public async Task<GovernmentIntelligenceSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (forceRefresh)
        {
            cache.Remove(SnapshotCacheKey);
        }

        var cached = await cache.GetOrCreateAsync(SnapshotCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SnapshotCacheDuration;

            var communityTask = BuildCommunityCoverageAsync(ct);
            var stateTask = BuildStateCoverageAsync(ct);
            var federalTask = BuildFederalCoverageAsync(ct);

            await Task.WhenAll(communityTask, stateTask, federalTask);

            return new GovernmentIntelligenceSnapshot(
                AreaLabel,
                DateTimeOffset.UtcNow,
                communityTask.Result,
                stateTask.Result,
                federalTask.Result);
        });

        return cached ?? new GovernmentIntelligenceSnapshot(
            AreaLabel,
            DateTimeOffset.UtcNow,
            EmptyCommunityCoverage(),
            EmptyStateCoverage(),
            EmptyFederalCoverage());
    }

    private async Task<CommunityCoverage> BuildCommunityCoverageAsync(CancellationToken ct)
    {
        var html = await GetStringOrNullAsync(CountyHomeUrl, ct);
        var announcements = html is null
            ? []
            : ParseCountyAnnouncements(html).Take(MaxCountyAnnouncements).ToList();
        var meetings = html is null
            ? []
            : ParseCountyMeetings(html).Take(MaxCountyMeetings).ToList();

        return new CommunityCoverage(
            "Kathleen is covered here through Houston County government, countywide boards, and the Houston County School District because the community is served at the county level rather than through a separate city hall.",
            announcements,
            meetings,
            [
                new CivicResourceSection("County Government",
                [
                    new CivicResourceLink("Latest Announcements", CountyAnnouncementsUrl, "Official Houston County news, notices, and service updates."),
                    new CivicResourceLink("Commission Calendar", CountyCalendarUrl, "Upcoming county meetings and calendar entries."),
                    new CivicResourceLink("Residents Portal", CountyResidentsUrl, "County resident services, utilities, and local information."),
                    new CivicResourceLink("Board of Elections", CountyElectionsUrl, "County election administration and notices."),
                    new CivicResourceLink("Code of Ordinances", CountyCodeUrl, "County ordinances and governing code.")
                ]),
                new CivicResourceSection("School District",
                [
                    new CivicResourceLink("District Calendar", SchoolsCalendarUrl, "Houston County School District calendar and closures."),
                    new CivicResourceLink("Board Meeting Video", SchoolsBoardVideoUrl, "Recorded school board meetings."),
                    new CivicResourceLink("Board Agendas and Minutes", SchoolsSimbliUrl, "Board packets, agendas, and formal actions."),
                    new CivicResourceLink("Legislative Priorities", SchoolsLegislativePrioritiesUrl, "District policy and legislative agenda."),
                    new CivicResourceLink("News Releases", SchoolsNewsUrl, "District announcements and media releases.")
                ]),
                new CivicResourceSection("Public Safety and Alerts",
                [
                    new CivicResourceLink("County Alerts", CountyAlertsUrl, "Smart911 county alerts and emergency notifications."),
                    new CivicResourceLink("County Home", CountyHomeUrl, "Houston County official homepage and featured updates."),
                    new CivicResourceLink("School District Home", SchoolsHomeUrl, "Houston County School District official homepage.")
                ])
            ],
            BuildCommunityLegislationBriefs());
    }

    private async Task<StateGovernmentCoverage> BuildStateCoverageAsync(CancellationToken ct)
    {
        var pressReleaseHtmlTask = GetStringOrNullAsync(GovernorPressReleasesUrl, ct);
        var signedLawHtmlTask = GetStringOrNullAsync(GovernorSignedLegislationUrl, ct);
        var houseVotesTask = LoadGeorgiaLegislativeVotesAsync(GeorgiaHouseChamber, "House", ct);
        var senateVotesTask = LoadGeorgiaLegislativeVotesAsync(GeorgiaSenateChamber, "Senate", ct);

        await Task.WhenAll(pressReleaseHtmlTask, signedLawHtmlTask, houseVotesTask, senateVotesTask);

        var pressReleaseHtml = pressReleaseHtmlTask.Result;
        var signedLawHtml = signedLawHtmlTask.Result;

        var pressReleases = pressReleaseHtml is null
            ? []
            : ParseGeorgiaPressReleases(pressReleaseHtml).Take(MaxStatePressReleases).ToList();
        var signedLaws = signedLawHtml is null
            ? []
            : ParseGeorgiaSignedLaws(signedLawHtml).Take(MaxStateSignedLaws).ToList();

        return new StateGovernmentCoverage(
            "This section tracks statewide executive action, signed laws, and official Georgia House and Senate floor votes tied back to the bills moving through the General Assembly.",
            pressReleases,
            signedLaws,
            houseVotesTask.Result,
            senateVotesTask.Result,
            [
                new CivicResourceSection("Governor and Executive Action",
                [
                    new CivicResourceLink("Governor Press Releases", GovernorPressReleasesUrl, "Official statewide announcements and executive updates."),
                    new CivicResourceLink("Legislation Overview", GovernorLegislationUrl, "Governor-level signed and vetoed legislation hub."),
                    new CivicResourceLink("2026 Signed Legislation", GovernorSignedLegislationUrl, "Current-year Georgia bills signed into law."),
                    new CivicResourceLink("2026 Vetoed Legislation", GovernorVetoedLegislationUrl, "Current-year Georgia vetoes and rejected measures.")
                ]),
                new CivicResourceSection("Georgia Legislature",
                [
                    new CivicResourceLink("Georgia General Assembly", GeorgiaGeneralAssemblyUrl, "Official state legislature site for bills, calendars, and member pages."),
                    new CivicResourceLink("All Legislation", GeorgiaAllLegislationUrl, "Current Georgia bills and resolutions across both chambers."),
                    new CivicResourceLink("Signed by Governor", GeorgiaSignedByGovernorUrl, "Georgia legislature view of measures signed by the Governor."),
                    new CivicResourceLink("House Votes", GeorgiaHouseVotesUrl, "Official Georgia House floor-vote archive."),
                    new CivicResourceLink("Senate Votes", GeorgiaSenateVotesUrl, "Official Georgia Senate floor-vote archive.")
                ])
            ]);
    }

    private async Task<FederalGovernmentCoverage> BuildFederalCoverageAsync(CancellationToken ct)
    {
        var senateVotesTask = LoadSenateVotesAsync(ct);
        var houseVotesTask = LoadHouseVotesAsync(ct);

        await Task.WhenAll(senateVotesTask, houseVotesTask);

        return new FederalGovernmentCoverage(
            "Federal coverage focuses on live House and Senate roll calls, with Georgia delegation votes easy to spot inside each measure.",
            "House and Senate roll-call tracking is live from official chamber sources. Federal signed-into-law status is not auto-fetched yet because Congress.gov public-laws pages are protected by a browser challenge for server-side clients; use the Public Laws link below as the official enacted-law reference.",
            senateVotesTask.Result,
            houseVotesTask.Result,
            [
                new CivicResourceSection("Congress",
                [
                    new CivicResourceLink("Congress Bill Search", CongressSearchUrl, "Official Congress.gov legislation search."),
                    new CivicResourceLink("Public Laws", CongressPublicLawsUrl, "Official Congress.gov public laws index."),
                    new CivicResourceLink("Senate Roll Call Votes", SenateVotesUrl, "Official Senate roll-call list for the current session."),
                    new CivicResourceLink("House Roll Call Votes", HouseVotesIndexUrl, "Official House votes search and roll-call index.")
                ])
            ]);
    }

    private async Task<IReadOnlyList<ChamberVoteSummary>> LoadSenateVotesAsync(CancellationToken ct)
    {
        var summaryXml = await GetStringOrNullAsync(SenateSummaryUrl, ct);
        if (string.IsNullOrWhiteSpace(summaryXml))
        {
            return [];
        }

        IReadOnlyList<int> voteNumbers;
        try
        {
            voteNumbers = ParseSenateVoteNumbers(summaryXml)
                .Take(MaxFederalVotesPerChamber)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Senate vote summary");
            return [];
        }

        var tasks = voteNumbers.Select(voteNumber => LoadSenateVoteAsync(voteNumber, ct)).ToList();
        var votes = await Task.WhenAll(tasks);
        return votes.Where(vote => vote is not null).Cast<ChamberVoteSummary>().ToList();
    }

    private async Task<IReadOnlyList<ChamberVoteSummary>> LoadHouseVotesAsync(CancellationToken ct)
    {
        var listHtml = await GetStringOrNullAsync(HouseVotesListUrl, ct);
        if (string.IsNullOrWhiteSpace(listHtml))
        {
            return [];
        }

        IReadOnlyList<string> identifiers;
        try
        {
            identifiers = ParseHouseVoteIdentifiers(listHtml)
                .Take(MaxFederalVotesPerChamber)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse House vote list");
            return [];
        }

        var tasks = identifiers.Select(identifier => LoadHouseVoteAsync(identifier, ct)).ToList();
        var votes = await Task.WhenAll(tasks);
        return votes.Where(vote => vote is not null).Cast<ChamberVoteSummary>().ToList();
    }

    private async Task<ChamberVoteSummary?> LoadSenateVoteAsync(int voteNumber, CancellationToken ct)
    {
        var xmlUrl = $"https://www.senate.gov/legislative/LIS/roll_call_votes/vote1192/vote_119_2_{voteNumber:00000}.xml";
        var xml = await GetStringOrNullAsync(xmlUrl, ct);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null)
            {
                return null;
            }

            var title = BuildVoteTitle(
                CleanText(root.Element("vote_title")?.Value),
                CleanText(root.Element("vote_document_text")?.Value));

            var measure = CleanText(root.Element("document")?.Element("document_name")?.Value);
            if (string.IsNullOrWhiteSpace(measure))
            {
                measure = CleanText(root.Element("vote_title")?.Value);
            }

            var votes = root.Element("members")?.Elements("member")
                .Select(member => new MemberVoteRecord(
                    CleanText(member.Element("member_full")?.Value),
                    CleanText(member.Element("party")?.Value),
                    CleanText(member.Element("state")?.Value),
                    CleanText(member.Element("vote_cast")?.Value)))
                .Where(vote => !string.IsNullOrWhiteSpace(vote.Name))
                .ToList() ?? [];

            return new ChamberVoteSummary(
                "Senate",
                voteNumber.ToString(CultureInfo.InvariantCulture),
                measure,
                CleanText(root.Element("question")?.Value),
                CleanText(root.Element("vote_result")?.Value),
                title,
                ParseDateTime(root.Element("vote_date")?.Value),
                $"https://www.senate.gov/legislative/LIS/roll_call_vote_cfm.cfm?congress=119&session=2&vote={voteNumber:00000}",
                ParseInt(root.Element("count")?.Element("yeas")?.Value),
                ParseInt(root.Element("count")?.Element("nays")?.Value),
                ParseInt(root.Element("count")?.Element("present")?.Value),
                ParseInt(root.Element("count")?.Element("absent")?.Value),
                votes,
                BuildFederalLegislationBrief(
                    "Senate",
                    measure,
                    title,
                    CleanText(root.Element("question")?.Value),
                    CleanText(root.Element("vote_result")?.Value),
                    ParseDateTime(root.Element("vote_date")?.Value),
                    $"https://www.senate.gov/legislative/LIS/roll_call_vote_cfm.cfm?congress=119&session=2&vote={voteNumber:00000}",
                    ParseInt(root.Element("count")?.Element("yeas")?.Value),
                    ParseInt(root.Element("count")?.Element("nays")?.Value),
                    ParseInt(root.Element("count")?.Element("present")?.Value),
                    ParseInt(root.Element("count")?.Element("absent")?.Value),
                    votes));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Senate vote detail {VoteNumber}", voteNumber);
            return null;
        }
    }

    private async Task<ChamberVoteSummary?> LoadHouseVoteAsync(string identifier, CancellationToken ct)
    {
        if (identifier.Length <= 4)
        {
            return null;
        }

        var year = identifier[..4];
        var rollCallNumber = identifier[4..];
        var xmlUrl = $"https://clerk.house.gov/evs/{year}/roll{rollCallNumber}.xml";
        var xml = await GetStringOrNullAsync(xmlUrl, ct);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var metadata = doc.Root?.Element("vote-metadata");
            if (metadata is null)
            {
                return null;
            }

            var totals = metadata.Element("vote-totals")?.Element("totals-by-vote");
            var votes = doc.Root?.Element("vote-data")?.Elements("recorded-vote")
                .Select(vote => new MemberVoteRecord(
                    CleanText(vote.Element("legislator")?.Value),
                    CleanText(vote.Element("legislator")?.Attribute("party")?.Value),
                    CleanText(vote.Element("legislator")?.Attribute("state")?.Value),
                    CleanText(vote.Element("vote")?.Value)))
                .Where(vote => !string.IsNullOrWhiteSpace(vote.Name))
                .ToList() ?? [];

            return new ChamberVoteSummary(
                "House",
                rollCallNumber,
                CleanText(metadata.Element("legis-num")?.Value),
                CleanText(metadata.Element("vote-question")?.Value),
                CleanText(metadata.Element("vote-result")?.Value),
                CleanText(metadata.Element("vote-desc")?.Value),
                ParseHouseDate(metadata.Element("action-date")?.Value, metadata.Element("action-time")?.Value),
                $"https://clerk.house.gov/Votes/{identifier}",
                ParseInt(totals?.Element("yea-total")?.Value),
                ParseInt(totals?.Element("nay-total")?.Value),
                ParseInt(totals?.Element("present-total")?.Value),
                ParseInt(totals?.Element("not-voting-total")?.Value),
                votes,
                BuildFederalLegislationBrief(
                    "House",
                    CleanText(metadata.Element("legis-num")?.Value),
                    CleanText(metadata.Element("vote-desc")?.Value),
                    CleanText(metadata.Element("vote-question")?.Value),
                    CleanText(metadata.Element("vote-result")?.Value),
                    ParseHouseDate(metadata.Element("action-date")?.Value, metadata.Element("action-time")?.Value),
                    $"https://clerk.house.gov/Votes/{identifier}",
                    ParseInt(totals?.Element("yea-total")?.Value),
                    ParseInt(totals?.Element("nay-total")?.Value),
                    ParseInt(totals?.Element("present-total")?.Value),
                    ParseInt(totals?.Element("not-voting-total")?.Value),
                    votes));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse House vote detail {Identifier}", identifier);
            return null;
        }
    }

    private async Task<IReadOnlyList<StateLegislativeVoteSummary>> LoadGeorgiaLegislativeVotesAsync(
        int chamber,
        string chamberLabel,
        CancellationToken ct)
    {
        var token = await GetGeorgiaAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var currentSessionId = await GetCurrentGeorgiaSessionIdAsync(token, ct);
        if (currentSessionId <= 0)
        {
            return [];
        }

        var voteList = await GetGeorgiaJsonAsync<GeorgiaVoteListItem[]>(
            $"Vote/list/{chamber}/{currentSessionId}",
            token,
            ct);
        if (voteList is null || voteList.Length == 0)
        {
            return [];
        }

        var summaries = new List<StateLegislativeVoteSummary>();
        var legislationCache = new Dictionary<int, JsonElement>();

        foreach (var vote in voteList.Take(MaxStateVoteCandidatesPerChamber))
        {
            if (summaries.Count >= MaxStateVotesPerChamber)
            {
                break;
            }

            var detail = await GetGeorgiaJsonAsync<GeorgiaVoteDetailResponse>(
                $"Vote/detail/{vote.Id}",
                token,
                ct);
            if (detail?.Legislation is null || detail.Legislation.Length == 0)
            {
                continue;
            }

            var primaryLegislation = detail.Legislation[0];
            if (!legislationCache.TryGetValue(primaryLegislation.LegislationId, out var legislationDetail))
            {
                legislationDetail = await GetGeorgiaJsonAsync<JsonElement>(
                    $"legislation/detail/{primaryLegislation.LegislationId}",
                    token,
                    ct);
                legislationCache[primaryLegislation.LegislationId] = legislationDetail;
            }

            var memberVotes = detail.Votes?
                .Where(row => row.Member.Id > 0 && !string.Equals(row.Member.Name, "VACANT", StringComparison.OrdinalIgnoreCase))
                .Select(row => new StateMemberVoteRecord(
                    CleanText(row.Member.Name),
                    MapGeorgiaMemberVote(row.MemberVoted)))
                .OrderBy(row => StateVoteSortOrder(row.Vote))
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            var title = ReadJsonString(legislationDetail, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = ReadJsonString(legislationDetail, "firstReader", "summary");
            }

            var detailUrl = BuildGeorgiaLegislationUrl(primaryLegislation.LegislationId);
            var brief = BuildGeorgiaLegislationBrief(
                chamberLabel,
                vote.Number.ToString(CultureInfo.InvariantCulture),
                CleanText(primaryLegislation.Description),
                string.IsNullOrWhiteSpace(title) ? CleanText(primaryLegislation.Description) : title,
                ReadJsonString(legislationDetail, "status"),
                ReadJsonString(legislationDetail, "firstReader", "summary"),
                ParseDateTime(vote.Date),
                detailUrl,
                vote.Yea,
                vote.Nay,
                vote.NotVoting,
                vote.Excused,
                memberVotes,
                legislationDetail);

            summaries.Add(new StateLegislativeVoteSummary(
                chamberLabel,
                vote.Number.ToString(CultureInfo.InvariantCulture),
                CleanText(vote.Caption),
                CleanText(primaryLegislation.Description),
                string.IsNullOrWhiteSpace(title) ? CleanText(primaryLegislation.Description) : title,
                ReadJsonString(legislationDetail, "status"),
                ParseDateTime(vote.Date),
                detailUrl,
                vote.Yea,
                vote.Nay,
                vote.NotVoting,
                vote.Excused,
                memberVotes,
                brief));
        }

        return summaries;
    }

    private async Task<string?> GetGeorgiaAccessTokenAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(GeorgiaAccessTokenCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = GeorgiaAccessTokenCacheDuration;

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var key = ComputeGeorgiaAuthenticationKey(timestamp);
            var tokenUrl = $"{GeorgiaAuthenticationTokenUrl}?key={Uri.EscapeDataString(key)}&ms={timestamp.ToString(CultureInfo.InvariantCulture)}";
            var rawToken = await GetStringOrNullAsync(tokenUrl, ct);
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<string>(rawToken, GeorgiaJsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse Georgia General Assembly access token");
                return rawToken.Trim().Trim('"');
            }
        });

    private async Task<int> GetCurrentGeorgiaSessionIdAsync(string token, CancellationToken ct) =>
        await cache.GetOrCreateAsync(GeorgiaCurrentSessionCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = GeorgiaSessionCacheDuration;

            var sessions = await GetGeorgiaJsonAsync<GeorgiaSessionResponse[]>("sessions", token, ct);
            return sessions?
                .OrderByDescending(session => session.IsCurrent)
                .ThenByDescending(session => session.Id)
                .Select(session => session.Id)
                .FirstOrDefault() ?? 0;
        });

    private async Task<T?> GetGeorgiaJsonAsync<T>(string relativeUrl, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return default;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(GeorgiaApiBaseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Government Intelligence fetch failed for {Url} with status code {StatusCode}",
                    request.RequestUri,
                    (int)response.StatusCode);
                return default;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, GeorgiaJsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Government Intelligence fetch failed for {Url}", request.RequestUri);
            return default;
        }
    }

    private async Task<string?> GetStringOrNullAsync(string url, CancellationToken ct)
    {
        try
        {
            return await http.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Government Intelligence fetch failed for {Url}", url);
            return null;
        }
    }

    private static IEnumerable<CivicUpdate> ParseCountyAnnouncements(string html)
    {
        foreach (Match match in CountyAnnouncementRegex.Matches(html))
        {
            var title = CleanText(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            yield return new CivicUpdate(
                title,
                ToAbsoluteUrl(match.Groups["href"].Value, CountyHomeUrl),
                string.Empty,
                null,
                "Houston County");
        }
    }

    private static IEnumerable<CivicMeeting> ParseCountyMeetings(string html)
    {
        foreach (Match match in CountyMeetingRegex.Matches(html))
        {
            var title = CleanText(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var location = string.Empty;
            var separatorIndex = title.LastIndexOf(',');
            if (separatorIndex >= 0 && separatorIndex < title.Length - 1)
            {
                location = title[(separatorIndex + 1)..].Trim();
            }

            yield return new CivicMeeting(
                title,
                ToAbsoluteUrl(match.Groups["href"].Value, CountyHomeUrl),
                ParseCountyMeetingDate(match.Groups["href"].Value, match.Groups["month"].Value, match.Groups["day"].Value),
                location,
                "Houston County");
        }
    }

    private static IEnumerable<CivicUpdate> ParseGeorgiaPressReleases(string html)
    {
        foreach (Match match in GeorgiaPressReleaseRegex.Matches(html))
        {
            var title = CleanText(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            yield return new CivicUpdate(
                title,
                ToAbsoluteUrl(match.Groups["href"].Value, GovernorPressReleasesUrl),
                string.Empty,
                ParseDateTime(match.Groups["date"].Value),
                "Office of the Governor");
        }
    }

    private static IEnumerable<LawSummary> ParseGeorgiaSignedLaws(string html)
    {
        foreach (Match match in GeorgiaSignedLawRegex.Matches(html))
        {
            var documentNumber = CleanText(match.Groups["document"].Value);
            var title = CleanText(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(documentNumber) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            yield return new LawSummary(
                documentNumber,
                title,
                ToAbsoluteUrl(match.Groups["href"].Value, GovernorSignedLegislationUrl),
                "Office of the Governor",
                BuildGeorgiaSignedLawBrief(documentNumber, title, ToAbsoluteUrl(match.Groups["href"].Value, GovernorSignedLegislationUrl)));
        }
    }

    private static IReadOnlyList<LegislationDetailBrief> BuildCommunityLegislationBriefs() =>
    [
        new LegislationDetailBrief(
            "Local ordinance research",
            "Houston County, Georgia",
            "Houston County Board of Commissioners",
            "County Code of Ordinances",
            "Current county ordinances in force",
            "Codified county law in force",
            "Use the codified county code when you need the operative ordinance text that currently governs Kathleen and the rest of Houston County.",
            CountyCodeUrl,
            [
                new LegislationFact("Community served", "Kathleen is governed at the county level because it is an unincorporated community."),
                new LegislationFact("Best for", "Existing local law, code sections, amendment notes, and enforceable ordinance text."),
                new LegislationFact("Research path", "Start with the county code, then confirm pending action through commission calendar items and county notices.")
            ],
            [
                new LegislationLink("Code of ordinances", CountyCodeUrl, "Official codified Houston County ordinances through Municode."),
                new LegislationLink("Commission calendar", CountyCalendarUrl, "County meeting entries where proposed ordinance action and hearings are posted."),
                new LegislationLink("County announcements", CountyAnnouncementsUrl, "Official public notices, service notices, and hearing-related updates.")
            ],
            [
                new LegislationTimelineEntry("Step 1", "Read the current ordinance text and note the code section involved.", "Current law"),
                new LegislationTimelineEntry("Step 2", "Check the commission calendar for hearings, readings, or votes tied to that topic.", "Pending action"),
                new LegislationTimelineEntry("Step 3", "Watch the notices feed to confirm adoption, amendment, or repeal activity.", "Public notice")
            ]),
        new LegislationDetailBrief(
            "Local action tracking",
            "Houston County, Georgia",
            "Houston County Board of Commissioners",
            "Commission actions and agenda items",
            "Pending county actions and public hearings",
            "Meeting-driven local policy flow",
            "Track county actions here before they appear in the permanent code, especially ordinances, rezonings, budgets, resolutions, and public hearings.",
            CountyCalendarUrl,
            [
                new LegislationFact("Best for", "Pending county decisions that may become local law or materially affect the community."),
                new LegislationFact("Primary source", "Commission meeting entries and linked county pages."),
                new LegislationFact("Follow-through", "Verify whether adopted actions later appear in the codified ordinance set.")
            ],
            [
                new LegislationLink("Commission calendar", CountyCalendarUrl, "Official Houston County commission schedule and meeting entries."),
                new LegislationLink("County home", CountyHomeUrl, "Homepage for featured updates and countywide notices."),
                new LegislationLink("Residents portal", CountyResidentsUrl, "Resident-facing county information that often surfaces implementation details.")
            ],
            [
                new LegislationTimelineEntry("Before a vote", "Review the meeting entry and any linked materials to see what is scheduled for consideration.", "Agenda stage"),
                new LegislationTimelineEntry("At the meeting", "Track whether the board adopts, rejects, tables, or revises the action.", "Public meeting"),
                new LegislationTimelineEntry("After the meeting", "Confirm whether the adopted action becomes codified or is reflected in county notices.", "Implementation")
            ]),
        new LegislationDetailBrief(
            "School governance",
            "Houston County, Georgia",
            "Houston County School District Board of Education",
            "Board agendas, minutes, and district priorities",
            "Education governance affecting Kathleen residents",
            "Education policy and formal board action",
            "School board decisions often carry the practical local policy impact for Kathleen residents, so this is the official place to review agendas, votes, minutes, and district legislative priorities.",
            SchoolsSimbliUrl,
            [
                new LegislationFact("Best for", "School governance, district policy changes, and legislative positions affecting county families."),
                new LegislationFact("Meeting record", "Use board packets, formal minutes, and meeting video together for full context."),
                new LegislationFact("Community scope", "Countywide education decisions serve Kathleen directly.")
            ],
            [
                new LegislationLink("Board agendas and minutes", SchoolsSimbliUrl, "Official board packets, agendas, and adopted minutes."),
                new LegislationLink("Board meeting video", SchoolsBoardVideoUrl, "Recorded board meetings for full debate and vote context."),
                new LegislationLink("Legislative priorities", SchoolsLegislativePrioritiesUrl, "District advocacy and policy priorities."),
                new LegislationLink("District news", SchoolsNewsUrl, "Official school district notices and announcements.")
            ],
            [
                new LegislationTimelineEntry("Board packet", "Start with the agenda packet to see the precise action under consideration.", "Before the meeting"),
                new LegislationTimelineEntry("Recorded vote", "Use video and minutes together to confirm who moved, debated, and approved the item.", "Meeting day"),
                new LegislationTimelineEntry("District rollout", "Review district notices for implementation and community impact after approval.", "After adoption")
            ])
    ];

    private static LegislationDetailBrief BuildGeorgiaSignedLawBrief(string documentNumber, string title, string url) =>
        new(
            "Signed Georgia law",
            "State of Georgia",
            "Office of the Governor and Georgia General Assembly",
            documentNumber,
            title,
            "Signed by Governor",
            "This entry is part of the Governor's signed-legislation record. Use it to inspect the signed document and then trace the measure back through the Georgia General Assembly for bill history and prior votes.",
            url,
            [
                new LegislationFact("Measure", documentNumber),
                new LegislationFact("Current status", "Signed by Governor"),
                new LegislationFact("Best next step", "Read the signed document, then use the legislature record for bill history, versions, and chamber activity.")
            ],
            [
                new LegislationLink("Signed law PDF", url, "Governor-hosted signed legislation document."),
                new LegislationLink("Governor legislation hub", GovernorLegislationUrl, "Executive-action landing page for signed and vetoed Georgia legislation."),
                new LegislationLink("Signed by Governor", GeorgiaSignedByGovernorUrl, "General Assembly view of measures signed by the Governor.")
            ],
            [
                new LegislationTimelineEntry("Executive action", "The Governor's office published the signed measure.", "Current cycle"),
                new LegislationTimelineEntry("Legislative history", "Use the General Assembly record to inspect prior versions, sponsors, committees, and votes.", "Follow-up research")
            ]);

    private static LegislationDetailBrief BuildGeorgiaLegislationBrief(
        string chamberLabel,
        string rollCallNumber,
        string measure,
        string title,
        string status,
        string summary,
        DateTimeOffset? votedAt,
        string detailUrl,
        int yeaCount,
        int nayCount,
        int notVotingCount,
        int excusedCount,
        IReadOnlyList<StateMemberVoteRecord> votes,
        JsonElement legislationDetail)
    {
        var sponsors = ReadJsonValues(legislationDetail, ["sponsors", "primarySponsors", "authors"], ["displayName", "name", "fullName", "sponsorName"]);
        var committees = ReadJsonValues(legislationDetail, ["committees", "assignedCommittees"], ["name", "displayName", "committeeName"]);
        var links = new List<LegislationLink>
        {
            new("Official bill detail", detailUrl, "Georgia General Assembly bill detail page.")
        };

        foreach (var versionLink in BuildGeorgiaVersionLinks(legislationDetail))
        {
            links.Add(versionLink);
        }

        var timeline = BuildGeorgiaTimeline(legislationDetail, chamberLabel, rollCallNumber, votedAt, status);
        if (timeline.Count == 0 && !string.IsNullOrWhiteSpace(status))
        {
            timeline.Add(new LegislationTimelineEntry("Current status", status, "Latest reported status"));
        }

        var facts = new List<LegislationFact>
        {
            new("Measure", measure),
            new("Latest status", string.IsNullOrWhiteSpace(status) ? "Status unavailable" : status),
            new("Floor vote", $"{chamberLabel} roll call {rollCallNumber}"),
            new("Vote totals", $"Yea {yeaCount} · Nay {nayCount} · Not Voting {notVotingCount} · Excused {excusedCount}")
        };

        if (sponsors.Count > 0)
        {
            facts.Add(new LegislationFact("Sponsors", JoinValues(sponsors)));
        }

        if (committees.Count > 0)
        {
            facts.Add(new LegislationFact("Committees", JoinValues(committees)));
        }

        if (votes.Count > 0)
        {
            facts.Add(new LegislationFact("Member breakdown", $"{votes.Count} member vote records loaded in this session."));
        }

        return new LegislationDetailBrief(
            "Georgia legislation",
            "State of Georgia",
            "Georgia General Assembly",
            measure,
            title,
            string.IsNullOrWhiteSpace(status) ? "Status unavailable" : status,
            string.IsNullOrWhiteSpace(summary) ? title : summary,
            detailUrl,
            facts,
            links,
            timeline);
    }

    private static LegislationDetailBrief BuildFederalLegislationBrief(
        string chamber,
        string measure,
        string title,
        string question,
        string result,
        DateTimeOffset? votedAt,
        string detailUrl,
        int yeaCount,
        int nayCount,
        int presentCount,
        int notVotingCount,
        IReadOnlyList<MemberVoteRecord> votes)
    {
        var georgiaVotes = votes
            .Where(member => string.Equals(member.State, "GA", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var facts = new List<LegislationFact>
        {
            new("Measure", string.IsNullOrWhiteSpace(measure) ? "Measure unavailable" : measure),
            new("Chamber action", $"{chamber} roll call result: {result}"),
            new("Vote totals", $"Yea {yeaCount} · Nay {nayCount} · Present {presentCount} · Not Voting {notVotingCount}")
        };

        if (georgiaVotes.Count > 0)
        {
            facts.Add(new LegislationFact("Georgia delegation", JoinValues(georgiaVotes.Select(member => $"{member.Name} {member.Vote}"))));
        }

        return new LegislationDetailBrief(
            "Federal measure",
            "United States",
            chamber == "Senate" ? "United States Senate" : "United States House of Representatives",
            string.IsNullOrWhiteSpace(measure) ? title : measure,
            string.IsNullOrWhiteSpace(title) ? question : title,
            string.IsNullOrWhiteSpace(result) ? "Roll-call action recorded" : result,
            string.IsNullOrWhiteSpace(question) ? title : question,
            detailUrl,
            facts,
            [
                new LegislationLink("Official vote detail", detailUrl, "Official chamber roll-call detail."),
                new LegislationLink("Congress bill search", BuildCongressSearchUrl(string.IsNullOrWhiteSpace(measure) ? title : measure), "Search Congress.gov for bill text, sponsors, actions, committees, and versions."),
                new LegislationLink("Public laws index", CongressPublicLawsUrl, "Official Congress.gov enacted-law index for the current Congress.")
            ],
            [
                new LegislationTimelineEntry("Roll-call action", $"{result} on {question}", FormatTimelineWhen(votedAt)),
                new LegislationTimelineEntry("Detailed research", "Use the Congress.gov bill search link for the official text, sponsor list, committees, and full status history.", "Official follow-up")
            ]);
    }

    private static List<LegislationTimelineEntry> BuildGeorgiaTimeline(
        JsonElement legislationDetail,
        string chamberLabel,
        string rollCallNumber,
        DateTimeOffset? votedAt,
        string status)
    {
        var timeline = new List<LegislationTimelineEntry>();

        if (votedAt is not null)
        {
            timeline.Add(new LegislationTimelineEntry(
                "Floor vote",
                $"{chamberLabel} roll call {rollCallNumber} was recorded for this measure.",
                FormatTimelineWhen(votedAt)));
        }

        foreach (var historyEntry in ReadJsonArray(legislationDetail, "statusHistory", "history", "actions"))
        {
            var label = ReadJsonString(historyEntry, "status", "action", "title");
            var detail = ReadJsonString(historyEntry, "description", "summary", "caption", "detail");
            var when = ReadJsonString(historyEntry, "date", "actionDate", "updatedDate");
            if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            timeline.Add(new LegislationTimelineEntry(
                string.IsNullOrWhiteSpace(label) ? "History" : label,
                string.IsNullOrWhiteSpace(detail) ? label : detail,
                string.IsNullOrWhiteSpace(when) ? "Reported history" : CleanText(when)));
        }

        if (timeline.Count == 0 && !string.IsNullOrWhiteSpace(status))
        {
            timeline.Add(new LegislationTimelineEntry("Current status", status, "Latest reported status"));
        }

        return timeline
            .Take(6)
            .ToList();
    }

    private static IReadOnlyList<LegislationLink> BuildGeorgiaVersionLinks(JsonElement legislationDetail)
    {
        var links = new List<LegislationLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in ReadJsonArray(legislationDetail, "versions", "documents", "documentVersions"))
        {
            var url = ReadJsonString(version, "url", "link", "href");
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
            {
                continue;
            }

            var label = ReadJsonString(version, "description", "title", "name");
            links.Add(new LegislationLink(
                string.IsNullOrWhiteSpace(label) ? "Bill text or version" : label,
                url,
                "Georgia legislation text or supporting version document."));
        }

        return links;
    }

    private static string BuildCongressSearchUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return CongressSearchUrl;
        }

        var payload = JsonSerializer.Serialize(new
        {
            source = "legislation",
            search = query
        });

        return $"https://www.congress.gov/search?q={Uri.EscapeDataString(payload)}";
    }

    private static IReadOnlyList<string> ReadJsonValues(
        JsonElement element,
        IReadOnlyList<string> arrayNames,
        IReadOnlyList<string> valueNames)
    {
        var values = new List<string>();

        foreach (var arrayName in arrayNames)
        {
            foreach (var item in ReadJsonArray(element, arrayName))
            {
                var value = item.ValueKind switch
                {
                    JsonValueKind.String => CleanText(item.GetString()),
                    JsonValueKind.Object => ReadJsonString(item, valueNames.ToArray()),
                    _ => string.Empty
                };

                if (!string.IsNullOrWhiteSpace(value) &&
                    !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<JsonElement> ReadJsonArray(JsonElement element, params string[] propertyNames)
    {
        if (!IsUsableJson(element))
        {
            return [];
        }

        foreach (var propertyName in propertyNames)
        {
            if (TryGetJsonProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray().ToList();
            }
        }

        return [];
    }

    private static string ReadJsonString(JsonElement element, params string[] propertyNames)
    {
        if (!IsUsableJson(element))
        {
            return string.Empty;
        }

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonProperty(element, propertyName, out var property))
            {
                continue;
            }

            var value = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                return CleanText(value);
            }
        }

        return string.Empty;
    }

    private static bool TryGetJsonProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static bool IsUsableJson(JsonElement element) =>
        element.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;

    private static string JoinValues(IEnumerable<string> values, int maxItems = 4)
    {
        var items = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems + 1)
            .ToList();
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (items.Count <= maxItems)
        {
            return string.Join(", ", items);
        }

        return $"{string.Join(", ", items.Take(maxItems))}, +{items.Count - maxItems} more";
    }

    private static string FormatTimelineWhen(DateTimeOffset? value) =>
        value is null
            ? "Date unavailable"
            : value.Value.ToString("MMMM dd, yyyy", CultureInfo.InvariantCulture);

    private static IEnumerable<int> ParseSenateVoteNumbers(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Root?.Element("votes")?.Elements("vote")
            .Select(vote => ParseInt(vote.Element("vote_number")?.Value))
            .Where(number => number > 0)
            .ToList() ?? [];
    }

    private static IEnumerable<string> ParseHouseVoteIdentifiers(string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HouseVoteIdentifierRegex.Matches(html))
        {
            var identifier = CleanText(match.Groups["id"].Value);
            if (identifier.Length > 4 && seen.Add(identifier))
            {
                yield return identifier;
            }
        }
    }

    private static string BuildVoteTitle(string primary, string secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return secondary;
        }

        if (string.IsNullOrWhiteSpace(secondary) || string.Equals(primary, secondary, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        return $"{primary}: {secondary}";
    }

    private static string ComputeGeorgiaAuthenticationKey(long timestamp)
    {
        var payload = $"QFpCwKfd7f{GeorgiaApiObscureKey}letvarconst{timestamp.ToString(CultureInfo.InvariantCulture)}";
        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildGeorgiaLegislationUrl(int legislationId) =>
        $"{GeorgiaLegislationPageUrl}{legislationId.ToString(CultureInfo.InvariantCulture)}";

    private static string MapGeorgiaMemberVote(int memberVoteCode) =>
        memberVoteCode switch
        {
            0 => "Yea",
            1 => "Nay",
            2 => "Excused",
            3 => "Not Voting",
            _ => "Unknown"
        };

    private static int StateVoteSortOrder(string vote) =>
        vote switch
        {
            "Yea" => 0,
            "Nay" => 1,
            "Not Voting" => 2,
            "Excused" => 3,
            _ => 4
        };

    private static string ToAbsoluteUrl(string href, string baseUrl)
    {
        var decoded = WebUtility.HtmlDecode(href).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return baseUrl;
        }

        if (Uri.TryCreate(decoded, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return absolute.ToString();
        }

        return new Uri(new Uri(baseUrl), decoded).ToString();
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutTags = HtmlTagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static DateOnly? ParseCountyMeetingDate(string href, string month, string day)
    {
        var decodedHref = WebUtility.HtmlDecode(href);
        if (Uri.TryCreate(ToAbsoluteUrl(decodedHref, CountyHomeUrl), UriKind.Absolute, out var uri))
        {
            var dateValue = GetQueryParameter(uri.Query, "date");
            if (TryParseDateOnly(dateValue, out var date))
            {
                return date;
            }
        }

        var currentYear = DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture);
        return TryParseDateOnly($"{currentYear}-{CleanText(month)}-{CleanText(day)}", out var fallback)
            ? fallback
            : null;
    }

    private static bool TryParseDateOnly(string? value, out DateOnly date)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateOnly.TryParseExact(
                value.Trim(),
                ["yyyy-MMM-d", "yyyy-MMM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }

        return null;
    }

    private static DateTimeOffset? ParseHouseDate(string? dateValue, string? timeValue)
    {
        var combined = $"{dateValue} {timeValue}".Trim();
        return ParseDateTime(combined);
    }

    private static int ParseInt(string? value) =>
        int.TryParse(CleanText(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static string? GetQueryParameter(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static CommunityCoverage EmptyCommunityCoverage() =>
        new(
            "Community coverage is temporarily unavailable.",
            [],
            [],
            [],
            []);

    private static StateGovernmentCoverage EmptyStateCoverage() =>
        new(
            "State coverage is temporarily unavailable.",
            [],
            [],
            [],
            [],
            []);

    private static FederalGovernmentCoverage EmptyFederalCoverage() =>
        new(
            "Federal coverage is temporarily unavailable.",
            "Live federal vote data could not be loaded.",
            [],
            [],
            []);

    private sealed record GeorgiaSessionResponse(
        int Id,
        bool IsCurrent);

    private sealed record GeorgiaVoteListItem(
        int Id,
        int Number,
        string Caption,
        string Date,
        int Yea,
        int Nay,
        int NotVoting,
        int Excused);

    private sealed record GeorgiaVoteDetailResponse(
        GeorgiaVoteMemberResponse[] Votes,
        GeorgiaVoteLegislationReference[] Legislation);

    private sealed record GeorgiaVoteMemberResponse(
        GeorgiaVoteMember Member,
        int MemberVoted);

    private sealed record GeorgiaVoteMember(
        int Id,
        string Name);

    private sealed record GeorgiaVoteLegislationReference(
        string Description,
        int LegislationId);
}
