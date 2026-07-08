using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class GovernmentIntelligenceServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ShouldParseCountyAnnouncementsAndMeetings()
    {
        var service = CreateService();

        var snapshot = await service.GetSnapshotAsync(forceRefresh: true);

        snapshot.AreaLabel.Should().Be("Kathleen, Houston County, Georgia");
        snapshot.Community.Announcements.Should().HaveCount(2);
        snapshot.Community.Announcements[0].Title.Should().Be("Independence Day Closure and Trash Services Will Run on Normal Schedule");
        snapshot.Community.Announcements[0].Url.Should().Be("https://www.houstoncountyga.gov/residents/news.cms/2026/15/independence-day-closure-and-trash-services-will-run-on-normal-schedule");
        snapshot.Community.Meetings.Should().ContainSingle();
        snapshot.Community.Meetings[0].Title.Should().Be("Commissioners Meeting, Warner Robins");
        snapshot.Community.Meetings[0].Location.Should().Be("Warner Robins");
        snapshot.Community.Meetings[0].MeetingDate.Should().Be(new DateOnly(2026, 7, 21));
        snapshot.Community.LegislationBriefs.Should().HaveCount(3);
        snapshot.Community.LegislationBriefs[0].Measure.Should().Be("County Code of Ordinances");
        snapshot.Community.LegislationBriefs[0].Links.Should().Contain(x => x.Title == "Code of ordinances");
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldParseStatePressReleasesAndSignedLegislation()
    {
        var service = CreateService();

        var snapshot = await service.GetSnapshotAsync(forceRefresh: true);

        snapshot.State.PressReleases.Should().HaveCount(2);
        snapshot.State.PressReleases[0].Title.Should().Be("Gov. Kemp Leads Economic Development Mission to Scotland and Ireland");
        snapshot.State.PressReleases[0].PublishedAt.Should().NotBeNull();
        snapshot.State.SignedLegislation.Should().HaveCount(2);
        snapshot.State.SignedLegislation[0].DocumentNumber.Should().Be("HB 1020");
        snapshot.State.SignedLegislation[0].Title.Should().Be("Judicial Retirement System; payment of monthly retirement benefits for creditable service as a district attorney at the age of 65 years; provide");
        snapshot.State.SignedLegislation[0].Url.Should().Be("https://gov.georgia.gov/document/2026-signed-legislation/hb-1020/download");
        var signedLawBrief = snapshot.State.SignedLegislation[0].Legislation;
        signedLawBrief.Should().NotBeNull();
        signedLawBrief!.Status.Should().Be("Signed by Governor");
        signedLawBrief.Links.Should().Contain(x => x.Title == "Signed law PDF");
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldLoadGeorgiaLegislativeVotesAndMemberBreakdown()
    {
        var service = CreateService();

        var snapshot = await service.GetSnapshotAsync(forceRefresh: true);

        snapshot.State.HouseVotes.Should().ContainSingle();
        var houseVote = snapshot.State.HouseVotes[0];
        houseVote.RollCallNumber.Should().Be("880");
        houseVote.Measure.Should().Be("HB 1409");
        houseVote.Title.Should().Be("Domestic relations; revise mandated reporting of child abuse");
        houseVote.Status.Should().Be("House Date Vetoed by Governor");
        houseVote.YeaCount.Should().Be(155);
        houseVote.NayCount.Should().Be(12);
        houseVote.NotVotingCount.Should().Be(11);
        houseVote.ExcusedCount.Should().Be(2);
        houseVote.DetailUrl.Should().Be("https://www.legis.ga.gov/legislation/73462");
        houseVote.Votes.Should().Contain(x => x.Name == "ALEXANDER, 66TH" && x.Vote == "Yea");
        houseVote.Votes.Should().Contain(x => x.Name == "BARNES, 86TH" && x.Vote == "Nay");
        houseVote.Votes.Should().Contain(x => x.Name == "GRIFFIN, 149TH" && x.Vote == "Excused");
        houseVote.Votes.Should().Contain(x => x.Name == "BURNS, 159TH" && x.Vote == "Not Voting");
        houseVote.Votes.Should().NotContain(x => x.Name == "VACANT");
        houseVote.Legislation.Should().NotBeNull();
        houseVote.Legislation!.Facts.Should().Contain(x => x.Label == "Sponsors" && x.Value.Contains("Rep. Penny Houston"));
        houseVote.Legislation.Links.Should().Contain(x => x.Title == "Current bill text");
        houseVote.Legislation.Timeline.Should().Contain(x => x.Label.Contains("Vetoed by Governor"));

        snapshot.State.SenateVotes.Should().ContainSingle();
        var senateVote = snapshot.State.SenateVotes[0];
        senateVote.RollCallNumber.Should().Be("990");
        senateVote.Measure.Should().Be("SB 359");
        senateVote.Title.Should().Be("Henry County; Board of Commissioners; code of ethics; revise and restate provisions");
        senateVote.Status.Should().Be("Senate Date Signed by Governor");
        senateVote.YeaCount.Should().Be(44);
        senateVote.NayCount.Should().Be(6);
        senateVote.NotVotingCount.Should().Be(5);
        senateVote.ExcusedCount.Should().Be(1);
        senateVote.DetailUrl.Should().Be("https://www.legis.ga.gov/legislation/71630");
        senateVote.Votes.Should().Contain(x => x.Name == "ALBERS, 56TH" && x.Vote == "Yea");
        senateVote.Votes.Should().Contain(x => x.Name == "BEARDEN, 30TH" && x.Vote == "Nay");
        senateVote.Votes.Should().Contain(x => x.Name == "HATCHETT, 50TH" && x.Vote == "Not Voting");
        senateVote.Votes.Should().Contain(x => x.Name == "MCNEEL, 18TH" && x.Vote == "Excused");
        senateVote.Legislation.Should().NotBeNull();
        senateVote.Legislation!.Facts.Should().Contain(x => x.Label == "Committees" && x.Value.Contains("State and Local Governmental Operations"));
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldParseFederalVotesAndGeorgiaDelegation()
    {
        var service = CreateService();

        var snapshot = await service.GetSnapshotAsync(forceRefresh: true);

        snapshot.Federal.SenateVotes.Should().ContainSingle();
        var senateVote = snapshot.Federal.SenateVotes[0];
        senateVote.RollCallNumber.Should().Be("192");
        senateVote.Measure.Should().Be("S.J.Res. 185");
        senateVote.Result.Should().Be("Motion to Proceed Rejected");
        senateVote.YeaCount.Should().Be(47);
        senateVote.NayCount.Should().Be(50);
        senateVote.Votes.Should().Contain(x => x.Name == "Ossoff (D-GA)" && x.Vote == "Yea");
        senateVote.Votes.Should().Contain(x => x.Name == "Warnock (D-GA)" && x.Vote == "Yea");
        senateVote.Legislation.Should().NotBeNull();
        senateVote.Legislation!.Links.Should().Contain(x => x.Title == "Congress bill search");

        snapshot.Federal.HouseVotes.Should().ContainSingle();
        var houseVote = snapshot.Federal.HouseVotes[0];
        houseVote.RollCallNumber.Should().Be("233");
        houseVote.Measure.Should().Be("H R 1041");
        houseVote.Result.Should().Be("Passed");
        houseVote.YeaCount.Should().Be(216);
        houseVote.NayCount.Should().Be(201);
        houseVote.Votes.Should().Contain(x => x.Name == "Allen" && x.State == "GA" && x.Vote == "Yea");
        houseVote.Votes.Should().Contain(x => x.Name == "Bishop" && x.State == "GA" && x.Vote == "Nay");
        houseVote.Legislation.Should().NotBeNull();
        houseVote.Legislation!.Facts.Should().Contain(x => x.Label == "Georgia delegation" && x.Value.Contains("Allen Yea"));
    }

    private static GovernmentIntelligenceService CreateService()
    {
        var responses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://www.houstoncountyga.gov/"] =
                """
                <div class="container-full news-wrapper" role="region" aria-labelledby="news">
                    <a class="news" href=" &#x2f;residents&#x2f;news.cms&#x2f;2026&#x2f;15&#x2f;independence-day-closure-and-trash-services-will-run-on-normal-schedule">
                        <div class="news-content">
                            <h4>Independence Day Closure and Trash Services Will Run on Normal Schedule</h4>
                        </div>
                    </a>
                    <a class="news" href=" &#x2f;residents&#x2f;news.cms&#x2f;2026&#x2f;14&#x2f;public-notice-fy27-budget">
                        <div class="news-content">
                            <h4>Public Notice FY27 Budget</h4>
                        </div>
                    </a>
                </div>
                <div class="container-full events-wrapper" role="region" aria-labelledby="events">
                    <a class="event" href="/commissioner/calendar.cms?date=2026-Jul-21&view=day&news_id=325&recurrence_id=540">
                        <div class="date">
                            <div class="month">Jul</div>
                            <div class="day-num">21</div>
                        </div>
                        <p>Commissioners Meeting, Warner Robins</p>
                    </a>
                </div>
                """,
            ["https://gov.georgia.gov/press-releases"] =
                """
                <a href="/press-releases/2026-07-08/gov-kemp-leads-economic-development-mission-scotland-and-ireland" class="global-teaser global-teaser--no-image news-teaser--no-image">
                    <h2 class="global-teaser__title">Gov. Kemp Leads Economic Development Mission to Scotland and Ireland</h2>
                    <div class="global-teaser__description">July 08, 2026</div>
                </a>
                <a href="/press-releases/2026-07-08/june-net-tax-revenues-down-68-due-fuel-tax-suspension" class="global-teaser global-teaser--no-image news-teaser--no-image">
                    <h2 class="global-teaser__title">June Net Tax Revenues Down 6.8% Due to Fuel Tax Suspension</h2>
                    <div class="global-teaser__description">July 08, 2026</div>
                </a>
                """,
            ["https://gov.georgia.gov/executive-action/legislation/signed-legislation/2026"] =
                """
                <table id="datatable">
                    <tr>
                        <td headers="view-rendered-entity-table-column" class="views-field views-field-rendered-entity">
                            <a href="/document/2026-signed-legislation/hb-1020/download" data-text="HB 1020">HB&nbsp;<wbr>1020</a>
                        </td>
                        <td headers="view-field-document-description-table-column" class="views-field views-field-field-document-description">Judicial Retirement System; payment of monthly retirement benefits for creditable service as a district attorney at the age of 65 years; provide</td>
                    </tr>
                    <tr>
                        <td headers="view-rendered-entity-table-column" class="views-field views-field-rendered-entity">
                            <a href="/document/2026-signed-legislation/sb-111/download" data-text="SB 111">SB&nbsp;<wbr>111</a>
                        </td>
                        <td headers="view-field-document-description-table-column" class="views-field views-field-field-document-description">&quot;Georgia Consumer Privacy Protection Act&quot;; enact</td>
                    </tr>
                </table>
                """,
            ["https://www.legis.ga.gov/api/sessions"] =
                """
                [{"id":1033,"isCurrent":true},{"id":1034,"isCurrent":false}]
                """,
            ["https://www.legis.ga.gov/api/Vote/list/1/1033"] =
                """
                [{"id":26581,"number":880,"caption":"Agree to Senate Substitute","date":"2026-04-03T00:53:00","yea":155,"nay":12,"notVoting":11,"excused":2}]
                """,
            ["https://www.legis.ga.gov/api/Vote/detail/26581"] =
                """
                {
                  "votes": [
                    { "member": { "id": 806, "name": "ALEXANDER, 66TH" }, "memberVoted": 0 },
                    { "member": { "id": 5037, "name": "BARNES, 86TH" }, "memberVoted": 1 },
                    { "member": { "id": 5083, "name": "GRIFFIN, 149TH" }, "memberVoted": 2 },
                    { "member": { "id": 73, "name": "BURNS, 159TH" }, "memberVoted": 3 },
                    { "member": { "id": 0, "name": "VACANT" }, "memberVoted": 3 }
                  ],
                  "legislation": [
                    { "description": "HB 1409", "legislationId": 73462 }
                  ]
                }
                """,
            ["https://www.legis.ga.gov/api/legislation/detail/73462"] =
                """
                {
                  "title": "Domestic relations; revise mandated reporting of child abuse",
                  "status": "House Date Vetoed by Governor ",
                  "firstReader": "A BILL to revise mandated reporting of child abuse.",
                  "sponsors": [
                    { "name": "Rep. Penny Houston" },
                    { "name": "Rep. Soo Hong" }
                  ],
                  "committees": [
                    { "name": "Juvenile Justice" }
                  ],
                  "versions": [
                    { "description": "Current bill text", "url": "https://www.legis.ga.gov/api/legislation/document/73462/current" }
                  ],
                  "statusHistory": [
                    { "date": "2026-04-03T00:53:00", "status": "House agreed to Senate substitute", "description": "Final House floor action completed." },
                    { "date": "2026-05-10T00:00:00", "status": "Vetoed by Governor", "description": "Returned without approval." }
                  ]
                }
                """,
            ["https://www.legis.ga.gov/api/Vote/list/2/1033"] =
                """
                [{"id":26646,"number":990,"caption":"AGREE TO HOUSE SUBSTITUTE","date":"2026-04-03T01:02:00","yea":44,"nay":6,"notVoting":5,"excused":1}]
                """,
            ["https://www.legis.ga.gov/api/Vote/detail/26646"] =
                """
                {
                  "votes": [
                    { "member": { "id": 754, "name": "ALBERS, 56TH" }, "memberVoted": 0 },
                    { "member": { "id": 62, "name": "BEARDEN, 30TH" }, "memberVoted": 1 },
                    { "member": { "id": 4984, "name": "HATCHETT, 50TH" }, "memberVoted": 3 },
                    { "member": { "id": 5093, "name": "MCNEEL, 18TH" }, "memberVoted": 2 }
                  ],
                  "legislation": [
                    { "description": "SB 359", "legislationId": 71630 }
                  ]
                }
                """,
            ["https://www.legis.ga.gov/api/legislation/detail/71630"] =
                """
                {
                  "title": "Henry County; Board of Commissioners; code of ethics; revise and restate provisions",
                  "status": "Senate Date Signed by Governor ",
                  "firstReader": "A BILL to revise Henry County board ethics provisions.",
                  "sponsors": [
                    { "name": "Sen. Emanuel Jones" }
                  ],
                  "committees": [
                    { "name": "State and Local Governmental Operations" }
                  ],
                  "versions": [
                    { "description": "Signed version", "url": "https://www.legis.ga.gov/api/legislation/document/71630/signed" }
                  ],
                  "statusHistory": [
                    { "date": "2026-04-03T01:02:00", "status": "Senate agreed to House substitute", "description": "Final Senate action completed." },
                    { "date": "2026-05-14T00:00:00", "status": "Signed by Governor", "description": "The Governor signed the measure into law." }
                  ]
                }
                """,
            ["https://www.senate.gov/legislative/LIS/roll_call_lists/vote_menu_119_2.xml"] =
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <vote_summary>
                  <votes>
                    <vote>
                      <vote_number>00192</vote_number>
                    </vote>
                  </votes>
                </vote_summary>
                """,
            ["https://www.senate.gov/legislative/LIS/roll_call_votes/vote1192/vote_119_2_00192.xml"] =
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <roll_call_vote>
                  <vote_date>June 24, 2026, 10:30 PM</vote_date>
                  <question>On the Motion to Proceed</question>
                  <vote_title>Motion to Proceed to S. J. Res. 185</vote_title>
                  <vote_document_text>A joint resolution to direct the removal of United States Armed Forces from hostilities within or against the Islamic Republic of Iran that have not been authorized by Congress.</vote_document_text>
                  <vote_result>Motion to Proceed Rejected</vote_result>
                  <document>
                    <document_name>S.J.Res. 185</document_name>
                  </document>
                  <count>
                    <yeas>47</yeas>
                    <nays>50</nays>
                    <present>1</present>
                    <absent>2</absent>
                  </count>
                  <members>
                    <member>
                      <member_full>Ossoff (D-GA)</member_full>
                      <party>D</party>
                      <state>GA</state>
                      <vote_cast>Yea</vote_cast>
                    </member>
                    <member>
                      <member_full>Warnock (D-GA)</member_full>
                      <party>D</party>
                      <state>GA</state>
                      <vote_cast>Yea</vote_cast>
                    </member>
                    <member>
                      <member_full>Cruz (R-TX)</member_full>
                      <party>R</party>
                      <state>TX</state>
                      <vote_cast>Nay</vote_cast>
                    </member>
                  </members>
                </roll_call_vote>
                """,
            ["https://clerk.house.gov/Votes/MemberVotes?CongressNum=119&Session=2nd"] =
                """
                <div class="role-call-vote">
                    <div class="heading">
                        Roll Call Number: <a href="/Votes/2026233?Page=2" aria-label="Roll number, 233">233</a>
                    </div>
                    <a href="/Votes/2026233" class="btn btn-library btn-lg" type="button">View Details</a>
                </div>
                """,
            ["https://clerk.house.gov/evs/2026/roll233.xml"] =
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <rollcall-vote>
                  <vote-metadata>
                    <legis-num>H R 1041</legis-num>
                    <vote-question>On Passage</vote-question>
                    <vote-result>Passed</vote-result>
                    <action-date>21-May-2026</action-date>
                    <action-time time-etz="18:12">6:12 PM</action-time>
                    <vote-desc>Veterans 2nd Amendment Protection Act</vote-desc>
                    <vote-totals>
                      <totals-by-vote>
                        <yea-total>216</yea-total>
                        <nay-total>201</nay-total>
                        <present-total>0</present-total>
                        <not-voting-total>13</not-voting-total>
                      </totals-by-vote>
                    </vote-totals>
                  </vote-metadata>
                  <vote-data>
                    <recorded-vote>
                      <legislator party="R" state="GA">Allen</legislator>
                      <vote>Yea</vote>
                    </recorded-vote>
                    <recorded-vote>
                      <legislator party="D" state="GA">Bishop</legislator>
                      <vote>Nay</vote>
                    </recorded-vote>
                    <recorded-vote>
                      <legislator party="R" state="GA">Clyde</legislator>
                      <vote>Yea</vote>
                    </recorded-vote>
                  </vote-data>
                </rollcall-vote>
                """
        };

        var handler = new RecordingHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            string? body = null;
            if (url.StartsWith("https://www.legis.ga.gov/api/authentication/token?", StringComparison.OrdinalIgnoreCase))
            {
                body = "\"test-georgia-token\"";
            }
            else
            {
                responses.TryGetValue(url, out body).Should().BeTrue($"Missing test fixture for {url}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body!, Encoding.UTF8, "text/html")
            };
        });

        var client = new HttpClient(handler);
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new GovernmentIntelligenceService(client, memoryCache, NullLogger<GovernmentIntelligenceService>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
