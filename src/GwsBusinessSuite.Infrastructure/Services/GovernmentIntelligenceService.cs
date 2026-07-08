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
            ]);
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
                votes);
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
                votes);
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
        var legislationCache = new Dictionary<int, GeorgiaLegislationDetailResponse?>();

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
                legislationDetail = await GetGeorgiaJsonAsync<GeorgiaLegislationDetailResponse>(
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

            var title = CleanText(legislationDetail?.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = CleanText(legislationDetail?.FirstReader);
            }

            summaries.Add(new StateLegislativeVoteSummary(
                chamberLabel,
                vote.Number.ToString(CultureInfo.InvariantCulture),
                CleanText(vote.Caption),
                CleanText(primaryLegislation.Description),
                string.IsNullOrWhiteSpace(title) ? CleanText(primaryLegislation.Description) : title,
                CleanText(legislationDetail?.Status),
                ParseDateTime(vote.Date),
                BuildGeorgiaLegislationUrl(primaryLegislation.LegislationId),
                vote.Yea,
                vote.Nay,
                vote.NotVoting,
                vote.Excused,
                memberVotes));
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
                "Office of the Governor");
        }
    }

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

    private sealed record GeorgiaLegislationDetailResponse(
        string Title,
        string Status,
        string FirstReader);
}
