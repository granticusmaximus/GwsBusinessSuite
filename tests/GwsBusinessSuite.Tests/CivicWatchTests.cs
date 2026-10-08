using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GwsBusinessSuite.Tests;

// Fixtures are trimmed copies of what each source actually returned on 2026-10-07.
public sealed class CivicWatchTests
{
    // ---- Location and districts ------------------------------------------------------------

    private const string CensusCoordinatesJson = """
        {"result":{"geographies":{
          "States":[{"GEOID":"13","STUSAB":"GA","NAME":"Georgia"}],
          "Counties":[{"GEOID":"13153","BASENAME":"Houston","NAME":"Houston County"}],
          "2026 State Legislative Districts - Upper":[{"BASENAME":"20","SLDU":"020","NAME":"State Senate District 20"}],
          "120th Congressional Districts":[{"BASENAME":"8","NAME":"Congressional District 8"}],
          "2026 State Legislative Districts - Lower":[{"BASENAME":"148","SLDL":"148","NAME":"State House District 148"}],
          "Census Tracts":[{"BASENAME":"215.01"}]}}}
        """;

    [Fact]
    public void ParseDistricts_ShouldReadEveryDistrict_WhateverTheLayerYear()
    {
        var districts = CivicWatchService.ParseDistricts(CensusCoordinatesJson);

        districts.Should().Be(new CivicWatchService.ResolvedDistricts("GA", "Houston County", 8, 20, 148));
    }

    [Fact]
    public void ParseGeocodedPoint_ShouldReturnTheFirstMatch_OrNullWhenNothingMatched()
    {
        CivicWatchService.ParseGeocodedPoint("""{"result":{"addressMatches":[{"coordinates":{"x":-83.61,"y":32.65}}]}}""")
            .Should().Be((32.65, -83.61));
        CivicWatchService.ParseGeocodedPoint("""{"result":{"addressMatches":[]}}""").Should().BeNull();
        CivicWatchService.ParseGeocodedPoint("not json").Should().BeNull();
    }

    [Fact]
    public void MilesFrom_ShouldUseVenueCoordinates_ThenTheTownCentre_FromAnyHome()
    {
        var perry = CivicPlaces.TownCoordinates[CivicPlaces.Perry];
        var withVenue = new CivicEvent("A", "u", null, null, "", "s", null, CivicPlaces.Macon, null, perry.Latitude, perry.Longitude);
        var townOnly = new CivicEvent("B", "u", null, null, "", "s", null, CivicPlaces.Perry);
        var unknown = new CivicEvent("C", "u", null, null, "", "s", null);

        CivicPlaces.MilesFrom(perry.Latitude, perry.Longitude, withVenue).Should().Be(0);
        CivicPlaces.MilesFrom(perry.Latitude, perry.Longitude, townOnly).Should().Be(0);
        CivicPlaces.MilesFrom(CivicPlaces.HomeLatitude, CivicPlaces.HomeLongitude, townOnly).Should().BeInRange(6, 8);
        CivicPlaces.MilesFrom(perry.Latitude, perry.Longitude, unknown).Should().BeNull();
    }

    // ---- Representatives -------------------------------------------------------------------

    private const string LegislatorsJson = """
        [
          {"id":{"bioguide":"O000174","lis":"S414"},"name":{"first":"Jon","last":"Ossoff","official_full":"Jon Ossoff"},
           "terms":[{"type":"sen","state":"GA","party":"Democrat","url":"https://www.ossoff.senate.gov","phone":"202-224-3521"}]},
          {"id":{"bioguide":"S001189"},"name":{"first":"Austin","last":"Scott","official_full":"Austin Scott"},
           "terms":[{"type":"rep","state":"GA","district":7,"party":"Republican"},{"type":"rep","state":"GA","district":8,"party":"Republican","url":"https://austinscott.house.gov"}]},
          {"id":{"bioguide":"C001103"},"name":{"first":"Buddy","last":"Carter","official_full":"Earl L. \"Buddy\" Carter"},
           "terms":[{"type":"rep","state":"GA","district":1,"party":"Republican"}]},
          {"id":{"bioguide":"X000001","lis":"S999"},"name":{"first":"Other","last":"State","official_full":"Other State"},
           "terms":[{"type":"sen","state":"AL","party":"Republican"}]}
        ]
        """;

    [Fact]
    public void ParseFederalRepresentatives_ShouldReturnBothSenatorsAndTheDistrictsHouseMember_ByCurrentTerm()
    {
        var reps = CivicWatchService.ParseFederalRepresentatives(LegislatorsJson, "GA", 8);

        reps.Select(r => (r.Chamber, r.Name, r.MemberId, r.District)).Should().Equal(
            ("Senate", "Jon Ossoff", "S414", "GA"),
            ("House", "Austin Scott", "S001189", "GA-08"));
        CivicWatchService.ParseFederalRepresentatives(LegislatorsJson, "GA", null)
            .Should().ContainSingle(r => r.Chamber == "Senate", "with no district only senators are known");
    }

    [Fact]
    public void PickGeorgiaMember_ShouldPreferTheSittingMember_OverOneWhoVacatedTheSeat()
    {
        const string json = """
            [{"id":852,"fullName":"John F. Kennedy","districtNumber":18,"party":1,"dateVacated":"2025-12-09T00:00:00-05:00"},
             {"id":5093,"fullName":"Steven McNeel","districtNumber":18,"party":1,"dateVacated":null},
             {"id":4878,"fullName":"Larry Walker, III","districtNumber":20,"party":1,"dateVacated":null},
             {"id":5025,"fullName":"Solomon Adesanya","districtNumber":43,"party":0}]
            """;

        CivicWatchService.PickGeorgiaMember(json, 18)!.Name.Should().Be("Steven McNeel");
        var walker = CivicWatchService.PickGeorgiaMember(json, 20)!;
        (walker.MemberId, walker.Party).Should().Be(("4878", "Republican"));
        CivicWatchService.PickGeorgiaMember(json, 43)!.Party.Should().Be("Democrat");
        CivicWatchService.PickGeorgiaMember(json, 99).Should().BeNull();
    }

    // ---- Bills -----------------------------------------------------------------------------

    [Theory]
    [InlineData("ga", "HB 68", "HB", 68, "HB 68")]
    [InlineData("ga", "h.b.68", "HB", 68, "HB 68")]
    [InlineData("ga", "SR 4", "SR", 4, "SR 4")]
    [InlineData("us", "H.R. 1", "hr", 1, "H.R. 1")]
    [InlineData("us", "s25", "s", 25, "S. 25")]
    [InlineData("us", "H.J.Res. 7", "hjres", 7, "H.J.Res. 7")]
    public void ParseBill_ShouldNormalizeTheUsualSpellings(string jurisdiction, string text, string type, int number, string label)
    {
        CivicWatchService.ParseBill(jurisdiction, text).Should().Be(new CivicWatchService.ParsedBill(jurisdiction, type, number, label));
    }

    [Theory]
    [InlineData("ga", "H.R. 1x")]
    [InlineData("ga", "S 5")]
    [InlineData("us", "HB 68")]
    [InlineData("us", "HR 0")]
    [InlineData("xx", "HB 1")]
    public void ParseBill_ShouldRejectWhatTheJurisdictionDoesNotUse(string jurisdiction, string text)
    {
        CivicWatchService.ParseBill(jurisdiction, text).Should().BeNull();
    }

    [Fact]
    public void PickGeorgiaBill_ShouldMatchTypeChamberAndNumber_PreferringTheCurrentSession()
    {
        const string json = """
            {"results":[
              {"legislationId":1,"documentType":1,"chamberType":1,"number":680,"caption":"Wrong number","status":"x","session":{"isCurrent":true}},
              {"legislationId":2,"documentType":2,"chamberType":1,"number":68,"caption":"House resolution 68","status":"x","session":{"isCurrent":true}},
              {"legislationId":3,"documentType":1,"chamberType":1,"number":68,"caption":"Old session HB 68","status":"Old","session":{"isCurrent":false}},
              {"legislationId":69395,"documentType":1,"chamberType":1,"number":68,"caption":"General appropriations;  FY 2026","status":"House Date Signed by Governor ","statusDate":"2025-05-09T00:00:00","session":{"isCurrent":true}}]}
            """;

        var hit = CivicWatchService.PickGeorgiaBill(json, CivicWatchService.ParseBill("ga", "HB 68")!);

        hit.Should().Be(new CivicWatchService.GeorgiaBillHit(69395, "General appropriations; FY 2026", "House Date Signed by Governor", "2025-05-09"));
        CivicWatchService.PickGeorgiaBill(json, CivicWatchService.ParseBill("ga", "HR 68")!)!.LegislationId.Should().Be(2);
        CivicWatchService.PickGeorgiaBill(json, CivicWatchService.ParseBill("ga", "SB 68")!).Should().BeNull();
    }

    [Fact]
    public void ParseFederalBill_ShouldReadTitleAndLatestAction()
    {
        const string json = """{"bill":{"title":"One Big Beautiful Bill Act","latestAction":{"actionDate":"2025-07-04","text":"Became Public Law No: 119-21."},"legislationUrl":"https://www.congress.gov/bill/119th-congress/house-bill/1"}}""";

        CivicWatchService.ParseFederalBill(json, CivicWatchService.ParseBill("us", "HR 1")!, 119).Should().Be(
            new CivicWatchService.FederalBillStatus("One Big Beautiful Bill Act", "Became Public Law No: 119-21.", "2025-07-04",
                "https://www.congress.gov/bill/119th-congress/house-bill/1"));
    }

    // ---- Keyword feeds ---------------------------------------------------------------------

    [Fact]
    public void ParseFederalRegister_ShouldKeepCommentDeadlinesAndAgencies()
    {
        const string json = """
            {"results":[
              {"document_number":"2026-20611","title":"Privacy Act of 1974; Implementation","type":"Proposed Rule",
               "html_url":"https://www.federalregister.gov/documents/2026/10/07/2026-20611/x","publication_date":"2026-10-07",
               "comments_close_on":"2026-11-06","agencies":[{"name":"Defense Department"},{"raw_name":"ARMY"}],"abstract":"Summary"},
              {"document_number":"2026-1","title":null,"html_url":"x"}]}
            """;

        var docs = CivicWatchService.ParseFederalRegister(json, "privacy");

        docs.Should().ContainSingle();
        var doc = docs[0];
        (doc.Type, doc.Agencies, doc.MatchedKeyword).Should().Be(("Proposed Rule", "Defense Department, ARMY", "privacy"));
        doc.IsOpenForComment(new DateOnly(2026, 11, 6)).Should().BeTrue();
        doc.IsOpenForComment(new DateOnly(2026, 11, 7)).Should().BeFalse();
    }

    [Fact]
    public void ParseGrants_ShouldBuildDetailLinksAndUsDates()
    {
        const string json = """
            {"errorcode":0,"data":{"hitCount":2,"oppHits":[
              {"id":"336962","number":"FA8651-22-S0021","title":"Partnership Intermediary Agreement (PIA)","agency":"Munitions Directorate","openDate":"12/14/2021","closeDate":"","oppStatus":"forecasted"},
              {"id":"360001","number":"SBA-1","title":"Small Business &amp; Training","agencyCode":"SBA","openDate":"10/01/2026","closeDate":"11/30/2026","oppStatus":"posted"}]}}
            """;

        var grants = CivicWatchService.ParseGrants(json, "small business");

        grants.Should().HaveCount(2);
        grants[0].Url.Should().Be("https://www.grants.gov/search-results-detail/336962");
        grants[0].CloseDate.Should().BeNull();
        grants[1].Title.Should().Be("Small Business & Training");
        grants[1].Agency.Should().Be("SBA");
        grants[1].CloseDate.Should().Be(new DateOnly(2026, 11, 30));
    }

    // ---- County documents and elections ----------------------------------------------------

    [Fact]
    public void ParseMeetingDocuments_ShouldReadDatesAndKinds_AndEscapeSpacesInFileNames()
    {
        const string html = """
            <a href="/skins/userfiles/files/2026%20Comm%20Mtg.pdf" target="_blank">Schedule</a>
            <a href="/minutes/2026-10-06.pdf">Meeting Agenda</a>
            <a href="/minutes/2026-09-22 Department Head Copy.pdf">Meeting Agenda</a>
            <a href="/minutes/2026-09-22  Minutes.pdf">Meeting Minutes</a>
            <a href="/minutes/2026-10-06.pdf">Meeting Agenda</a>
            """;

        var docs = CivicWatchService.ParseMeetingDocuments(html);

        docs.Should().Equal(
            new CivicWatchService.MeetingDocumentLink("https://www.houstoncountyga.gov/minutes/2026-10-06.pdf", new DateOnly(2026, 10, 6), "Agenda"),
            new CivicWatchService.MeetingDocumentLink("https://www.houstoncountyga.gov/minutes/2026-09-22%20Department%20Head%20Copy.pdf", new DateOnly(2026, 9, 22), "Agenda"),
            new CivicWatchService.MeetingDocumentLink("https://www.houstoncountyga.gov/minutes/2026-09-22%20%20Minutes.pdf", new DateOnly(2026, 9, 22), "Minutes"));
    }

    [Fact]
    public void ParseElectionDates_ShouldReadEachRow_AndLabelTheFederalDeadline()
    {
        const string html = """
            <table><thead><tr><th>ELECTION</th><th>ELECTION DATE</th><th>REGISTRATION DEADLINE</th></tr></thead><tbody>
            <tr><td style="text-align: center;">Special Election</td><td>March 17, 2026</td><td>2/16/2026</td></tr>
            <tr><td>General Election / Special Election</td><td>November 03, 2026</td><td>10/05/2026</td></tr>
            <tr><td>General Election / Special Election Runoff</td><td style="text-align: center;">December 01, 2026</td>
                <td><p>10/05/2026</p><p><span style="color:#ff0000;">*</span>11/02/2026</p></td></tr>
            </tbody></table>
            """;

        var elections = CivicWatchService.ParseElectionDates(html);

        elections.Should().Equal(
            new CivicElection("Special Election", new DateOnly(2026, 3, 17), "2/16/2026"),
            new CivicElection("General Election / Special Election", new DateOnly(2026, 11, 3), "10/05/2026"),
            new CivicElection("General Election / Special Election Runoff", new DateOnly(2026, 12, 1), "10/05/2026; 11/02/2026 (federal contests)"));
    }

    [Fact]
    public void ParseElectionDocuments_ShouldKeepElectionPdfsAndVotingPages_AndNameTheSampleBallot()
    {
        const string html = """
            <a href="/residents/board-of-elections.cms">Board of Elections</a>
            <a href="/residents/polling-locations.cms">Polling Locations</a>
            <a href="/residents/earlyvotingtotals.cms">Early Voting - Totals</a>
            <a href="/skins/userfiles/files/EARLY%20VOTING%20%20CALENDAR%20%20-%20Nov%203%2C%202026.pdf">Early Voting Calendar &ndash; 11-3-2026</a>
            <a href="/skins/userfiles/files/Qualifying_Fees_2026.pdf">Qualifying Fees - 2026</a>
            <a href="/skins/userfiles/files/November%202026%20Sample%20Ballot.pdf">Click Here</a>
            <a href="/residents/earlyvotingtotals.cms">Click Here</a>
            """;

        var docs = CivicWatchService.ParseElectionDocuments(html);

        docs.Select(d => d.Title).Should().Equal("Polling Locations", "Early Voting Calendar – 11-3-2026", "Sample ballot");
    }

    [Fact]
    public void ExtractPdfText_ShouldReadTheWordsOnEachPage()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter)
            .AddText("Approval of Minutes", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);

        CivicWatchService.ExtractPdfText(builder.Build()).Should().Contain("Approval of Minutes");
    }

    // ---- Visit Macon -----------------------------------------------------------------------

    [Theory]
    [InlineData("Oct 8", "2026-10-08", null)]
    [InlineData("Oct 7 - 31", "2026-10-07", "2026-10-31")]
    [InlineData("Oct 30 - Nov 2", "2026-10-30", "2026-11-02")]
    [InlineData("Dec 31 - Jan 2", "2026-12-31", "2027-01-02")]
    [InlineData("Jan 4", "2027-01-04", null)]
    public void ParseVisitMaconDateRange_ShouldInferTheYear(string text, string start, string? end)
    {
        var (s, e) = LocalEventsScraperService.ParseVisitMaconDateRange(text, new DateOnly(2026, 10, 7));

        s!.Value.ToString("yyyy-MM-dd").Should().Be(start);
        e?.ToString("yyyy-MM-dd").Should().Be(end);
        if (end is null) e.Should().BeNull();
    }

    [Fact]
    public void ParseVisitMaconDateRange_ShouldReturnNothingForUnreadableText()
    {
        LocalEventsScraperService.ParseVisitMaconDateRange("Ongoing", new DateOnly(2026, 10, 7)).Should().Be((null, null));
    }

    // ---- Database-backed behaviour ---------------------------------------------------------

    private static GovernmentIntelligenceSnapshot Snapshot(params string[] announcementUrls) => new(
        "Area", DateTimeOffset.UtcNow,
        new CommunityCoverage("", announcementUrls.Select(u => new CivicUpdate("Notice " + u, u, "", null, "Houston County")).ToList(), [], [], [], []),
        new StateGovernmentCoverage("", [], [new LawSummary("HB 1", "Law", "https://gov.example/hb1", "Gov", null)], [], [], []),
        new FederalGovernmentCoverage("", "", [], [], [], [], [], new FloorStatus(false, "", null, null), new FloorStatus(false, "", null, null), []));

    [Fact]
    public async Task Sightings_ShouldTreatTheFirstRunAsBaseline_ThenReportOnlyNewItems()
    {
        await using var f = await Fixture.CreateAsync();

        await f.Service.RecordSightingsAsync(Snapshot("https://c.example/1"));
        (await f.Service.GetChangesAsync(TimeSpan.FromDays(1))).Items.Should().BeEmpty("the first run is the baseline");

        await f.Service.RecordSightingsAsync(Snapshot("https://c.example/1", "https://c.example/2"));
        var changes = await f.Service.GetChangesAsync(TimeSpan.FromDays(1));

        changes.Items.Should().ContainSingle().Which.Url.Should().Be("https://c.example/2");
        changes.NewKeys.Should().Contain(CivicItemKeys.For("community", "https://c.example/2"));
    }

    [Fact]
    public async Task SaveSettings_ShouldGeocodeTheAddress_AndResolveDistricts()
    {
        await using var f = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath switch
        {
            "/geocoder/locations/onelineaddress" => Json("""{"result":{"addressMatches":[{"coordinates":{"x":-83.73,"y":32.46}}]}}"""),
            "/geocoder/geographies/coordinates" => Json(CensusCoordinatesJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var defaults = await f.Service.GetSettingsAsync();

        var saved = await f.Service.SaveSettingsAsync(defaults with
        {
            HomeLabel = "Perry, GA", HomeAddress = "1 Main St, Perry, GA", FederalRegisterKeywords = "cybersecurity\n\ncybersecurity\nAI policy"
        }, "grant");

        (saved.HomeLatitude, saved.HomeLongitude).Should().Be((32.46, -83.73));
        (saved.StateCode, saved.CongressionalDistrict, saved.StateSenateDistrict, saved.StateHouseDistrict).Should().Be(("GA", 8, 20, 148));
        saved.FederalRegisterKeywordList.Should().Equal("cybersecurity", "AI policy");
        (await f.Service.GetSettingsAsync()).HomeLabel.Should().Be("Perry, GA");
    }

    [Fact]
    public async Task SaveSettings_ShouldRejectAlertsWithoutARealEmailAddress()
    {
        await using var f = await Fixture.CreateAsync();
        var defaults = await f.Service.GetSettingsAsync();

        var act = () => f.Service.SaveSettingsAsync(defaults with { AlertsEnabled = true, AlertRecipient = "bob" }, "grant");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Watchlist_ShouldAddAFederalBill_ThenEmailAndTriggerWhenItsStatusChanges()
    {
        var latest = "Introduced in House";
        await using var f = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath.StartsWith("/v3/bill/", StringComparison.Ordinal)
            ? Json("""{"bill":{"title":"A bill","latestAction":{"actionDate":"2026-10-01","text":""" + "\"" + latest + "\"}}}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var defaults = await f.Service.GetSettingsAsync();
        await f.Service.SaveSettingsAsync(defaults with { AlertsEnabled = true, AlertRecipient = "grant@example.com" }, "grant");

        var added = await f.Service.AddWatchedBillAsync("us", "H.R. 42", "grant");
        added.Label.Should().Be("H.R. 42");
        added.LatestStatus.Should().Be("Introduced in House");
        await FluentActions.Awaiting(() => f.Service.AddWatchedBillAsync("us", "hr42", "grant"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*already*");

        (await f.Service.CheckWatchedBillsAsync()).Should().BeEmpty("nothing moved");

        latest = "Passed House";
        var changes = await f.Service.CheckWatchedBillsAsync();

        changes.Should().ContainSingle().Which.Should().Match<WatchedBillChange>(c => c.PreviousStatus == "Introduced in House" && c.Status == "Passed House");
        f.Mail.Sent.Should().ContainSingle().Which.Subject.Should().Contain("H.R. 42");
        f.Triggers.Fired.Should().ContainSingle().Which.Bill.Should().Be("H.R. 42");
        (await f.Service.ListWatchedBillsAsync()).Single().LatestStatus.Should().Be("Passed House");
    }

    [Fact]
    public async Task Watchlist_ShouldRefuseABillTheSourceCannotFind()
    {
        await using var f = await Fixture.CreateAsync();

        await FluentActions.Awaiting(() => f.Service.AddWatchedBillAsync("us", "S. 9999", "grant"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Couldn't find*");
        (await f.Service.ListWatchedBillsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Watchlist_ShouldSayCongressIsRateLimiting_NotThatTheBillDoesNotExist()
    {
        await using var f = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        await FluentActions.Awaiting(() => f.Service.AddWatchedBillAsync("us", "H.R. 1", "grant"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*rate-limiting*");
    }

    [Fact]
    public async Task MeetingDocuments_ShouldStoreNewPdfs_AndSummarizeTheNewestFirst()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter).AddText("Approval of a Quote (Court Software)", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var pdf = builder.Build();
        await using var f = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath switch
        {
            "/commissioner/meeting-minutes.cms" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""<a href="/minutes/2026-10-06.pdf">Meeting Agenda</a><a href="/minutes/2026-09-22  Minutes.pdf">Meeting Minutes</a>""")
            },
            var path when path.EndsWith(".pdf", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        f.Ollama.Response = "- Approves court software server replacement";

        await f.Service.RefreshMeetingDocumentsAsync(maxSummaries: 1);
        var docs = await f.Service.ListMeetingDocumentsAsync();

        docs.Should().HaveCount(2);
        docs[0].MeetingDate.Should().Be(new DateOnly(2026, 10, 6));
        docs[0].AiSummary.Should().Be("- Approves court software server replacement");
        docs[1].AiSummary.Should().BeNull("only one summary per run");
        f.Ollama.Prompts.Should().ContainSingle().Which.Should().Contain("Court Software");
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---- Fixture ---------------------------------------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        public CivicWatchService Service { get; private set; } = null!;
        public RecordingMail Mail { get; } = new();
        public RecordingOllama Ollama { get; } = new();
        public RecordingTriggers Triggers { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage>? http = null)
        {
            var fixture = new Fixture { _connection = new SqliteConnection("Data Source=:memory:") };
            await fixture._connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(fixture._connection).Options;
            await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();
            fixture.Triggers = RecordingTriggers.Create();
            fixture.Service = new CivicWatchService(
                new Factory(options),
                new HttpClient(new Handler(http ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
                new MemoryCache(new MemoryCacheOptions()),
                new CongressApiSettings("TEST_KEY"),
                fixture.Ollama,
                new OllamaWorkloadScheduler(),
                Options.Create(new ContentStudioOptions()),
                fixture.Mail,
                Options.Create(new GrowthReportEmailOptions { Host = "smtp.example.com", FromAddress = "noreply@example.com" }),
                TimeProvider.System,
                NullLogger<CivicWatchService>.Instance,
                (IAutomationTriggerService)(object)fixture.Triggers);
            return fixture;
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class RecordingMail : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public MailRoute Describe(ISmtpTransportOptions options) => new(MailRouteKind.Smtp, "test");
        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOllama : IOllamaService
    {
        public string Response { get; set; } = string.Empty;
        public List<string> Prompts { get; } = [];

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            Prompts.Add(userPrompt);
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    // Only TriggerCivicBillStatusChangedAsync is expected; anything else returns 0.
    public class RecordingTriggers : System.Reflection.DispatchProxy
    {
        public List<(string Bill, string Payload)> Fired { get; } = [];

        public static RecordingTriggers Create() =>
            (RecordingTriggers)(object)Create<IAutomationTriggerService, RecordingTriggers>();

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAutomationTriggerService.TriggerCivicBillStatusChangedAsync))
            {
                Fired.Add(((string)args![0]!, (string)args[1]!));
            }
            return Task.FromResult(0);
        }
    }
}
