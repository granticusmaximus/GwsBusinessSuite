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

        snapshot.Federal.HouseVotes.Should().ContainSingle();
        var houseVote = snapshot.Federal.HouseVotes[0];
        houseVote.RollCallNumber.Should().Be("233");
        houseVote.Measure.Should().Be("H R 1041");
        houseVote.Result.Should().Be("Passed");
        houseVote.YeaCount.Should().Be(216);
        houseVote.NayCount.Should().Be(201);
        houseVote.Votes.Should().Contain(x => x.Name == "Allen" && x.State == "GA" && x.Vote == "Yea");
        houseVote.Votes.Should().Contain(x => x.Name == "Bishop" && x.State == "GA" && x.Vote == "Nay");
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
            responses.TryGetValue(url, out var body).Should().BeTrue($"Missing test fixture for {url}");

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
