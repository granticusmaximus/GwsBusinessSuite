using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class FederalCivicFeedServiceTests
{
    private const string BillsJson = """
        {
          "bills": [
            { "congress": 118, "number": "101", "type": "S", "title": "Senate bill one",
              "originChamber": "Senate", "latestAction": { "actionDate": "2026-08-01", "text": "Referred to committee" } },
            { "congress": 118, "number": "202", "type": "HR", "title": "House bill one",
              "originChamber": "House", "latestAction": { "actionDate": "2026-08-02", "text": "Passed House" } }
          ]
        }
        """;

    private const string RecordJson = """
        {
          "Results": {
            "Issues": [
              {
                "Congress": "118", "Session": "2", "Volume": "172", "Issue": "130",
                "PublishDate": "TODAY",
                "Links": {
                  "Senate": { "PDF": [ { "Url": "https://congress.gov/senate.pdf" } ] },
                  "House": { "PDF": [ { "Url": "https://congress.gov/house.pdf" } ] }
                }
              }
            ]
          }
        }
        """;

    private const string HouseFloorDayJson = """{ "_id": "2026-08-10" }""";

    private const string HouseBroadcastLiveJson = """
        [
          {
            "isLiveBroadcast": "True",
            "asset": { "files": [
              { "type": "HLS", "url": "https://video.house.gov/east/floor.m3u8" },
              { "type": "HLS", "url": "https://video.house.gov/west/floor.m3u8" }
            ] }
          }
        ]
        """;

    private const string HouseBroadcastNotLiveJson = """[ { "isLiveBroadcast": "False", "asset": null } ]""";

    [Fact]
    public async Task RefreshAsync_ShouldSplitBillsIntoSenateAndHouseNewsCaches()
    {
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.Contains("/v3/bill") => Json(BillsJson),
            var uri when uri.Contains("/v3/congressional-record") => Json(RecordJson.Replace("TODAY", "1999-01-01")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await fixture.Service.RefreshAsync();

        var senateNews = fixture.Service.GetCachedSenateNewsOrEmpty();
        var houseNews = fixture.Service.GetCachedHouseNewsOrEmpty();
        senateNews.Should().ContainSingle(item => item.Title == "Senate bill one");
        houseNews.Should().ContainSingle(item => item.Title == "House bill one");
        senateNews[0].Source.Should().Be("Congress.gov");
    }

    [Fact]
    public async Task RefreshAsync_WhenTheBillsEndpointFails_ShouldLeaveNewsCachesEmptyAndNotThrow()
    {
        await using var fixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = async () => await fixture.Service.RefreshAsync();

        await act.Should().NotThrowAsync();
        fixture.Service.GetCachedSenateNewsOrEmpty().Should().BeEmpty();
        fixture.Service.GetCachedHouseNewsOrEmpty().Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenTheMostRecentRecordIsOld_ShouldReportBothChambersNotInSession()
    {
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.Contains("/v3/bill") => Json(BillsJson),
            var uri when uri.Contains("/v3/congressional-record") => Json(RecordJson.Replace("TODAY", "1999-01-01")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await fixture.Service.RefreshAsync();

        var senateFloor = fixture.Service.GetCachedSenateFloorOrEmpty();
        senateFloor.InSession.Should().BeFalse();
        senateFloor.LiveEmbedUrl.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_WhenTheRecordIsRecentAndHouseIsLive_ShouldPopulateHouseEmbedUrlButNeverSenate()
    {
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.Contains("/v3/bill") => Json(BillsJson),
            var uri when uri.Contains("/v3/congressional-record") => Json(RecordJson.Replace("TODAY", today)),
            var uri when uri.Contains("/latest/floor") => Json(HouseFloorDayJson),
            var uri when uri.Contains("/broadcastevents/") => Json(HouseBroadcastLiveJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await fixture.Service.RefreshAsync();

        var senateFloor = fixture.Service.GetCachedSenateFloorOrEmpty();
        var houseFloor = fixture.Service.GetCachedHouseFloorOrEmpty();
        senateFloor.InSession.Should().BeTrue();
        senateFloor.LiveEmbedUrl.Should().BeNull("no confirmed working live-video source exists for the Senate");
        houseFloor.InSession.Should().BeTrue();
        houseFloor.LiveEmbedUrl.Should().Be("https://video.house.gov/east/floor.m3u8", "only the /east/ HLS file is selected");
    }

    [Fact]
    public async Task RefreshAsync_WhenTheHouseBroadcastIsNotLive_ShouldNotPopulateAnEmbedUrl()
    {
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.Contains("/v3/bill") => Json(BillsJson),
            var uri when uri.Contains("/v3/congressional-record") => Json(RecordJson.Replace("TODAY", today)),
            var uri when uri.Contains("/latest/floor") => Json(HouseFloorDayJson),
            var uri when uri.Contains("/broadcastevents/") => Json(HouseBroadcastNotLiveJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await fixture.Service.RefreshAsync();

        fixture.Service.GetCachedHouseFloorOrEmpty().InSession.Should().BeFalse();
        fixture.Service.GetCachedHouseFloorOrEmpty().LiveEmbedUrl.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_ShouldUpsertACongressionalFloorTranscriptForEachChamber()
    {
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.Contains("/v3/bill") => Json(BillsJson),
            var uri when uri.Contains("/v3/congressional-record") => Json(RecordJson.Replace("TODAY", today)),
            var uri when uri.Contains("/latest/floor") => Json(HouseFloorDayJson),
            var uri when uri.Contains("/broadcastevents/") => Json(HouseBroadcastNotLiveJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await fixture.Service.RefreshAsync();

        await using var db = fixture.CreateDbContext();
        var transcripts = await db.CongressionalFloorTranscripts.ToListAsync();
        transcripts.Should().HaveCount(2);
        transcripts.Select(t => t.Chamber).Should().BeEquivalentTo(["Senate", "House"]);
        transcripts.Should().OnlyContain(t => t.SourceUrl.Contains("congress.gov"));
    }

    [Fact]
    public async Task RefreshAsync_CalledTwiceForTheSameSessionDate_ShouldUpdateTheExistingRowRatherThanDuplicate()
    {
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.Contains("/v3/bill") => Json(BillsJson),
            var uri when uri.Contains("/v3/congressional-record") => Json(RecordJson.Replace("TODAY", today)),
            var uri when uri.Contains("/latest/floor") => Json(HouseFloorDayJson),
            var uri when uri.Contains("/broadcastevents/") => Json(HouseBroadcastNotLiveJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await fixture.Service.RefreshAsync();
        await fixture.Service.RefreshAsync();

        await using var db = fixture.CreateDbContext();
        (await db.CongressionalFloorTranscripts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ListTranscriptArchiveAsync_ShouldOrderByMostRecentSessionDateAndRespectTheChamberFilter()
    {
        await using var fixture = await Fixture.CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await using (var db = fixture.CreateDbContext())
        {
            db.CongressionalFloorTranscripts.AddRange(
                new()
                {
                    Chamber = "Senate", SessionDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    SourceUrl = "https://a", FullText = "a", CreatedBy = "test"
                },
                new()
                {
                    Chamber = "Senate", SessionDate = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                    SourceUrl = "https://b", FullText = "b", CreatedBy = "test"
                },
                new()
                {
                    Chamber = "House", SessionDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                    SourceUrl = "https://c", FullText = "c", CreatedBy = "test"
                });
            await db.SaveChangesAsync();
        }

        var senateArchive = await fixture.Service.ListTranscriptArchiveAsync("Senate");
        senateArchive.Should().HaveCount(2);
        senateArchive[0].SourceUrl.Should().Be("https://b", "the most recent SessionDate should come first");
        senateArchive[1].SourceUrl.Should().Be("https://a");

        var everything = await fixture.Service.ListTranscriptArchiveAsync();
        everything.Should().HaveCount(3);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, FederalCivicFeedService service)
        {
            _connection = connection;
            Service = service;
        }

        public FederalCivicFeedService Service { get; }

        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();

            var http = new HttpClient(new RecordingHandler(responseFactory));
            var service = new FederalCivicFeedService(
                http,
                new MemoryCache(new MemoryCacheOptions()),
                new TestDbContextFactory(connection),
                new CongressApiSettings("TEST_KEY"),
                NullLogger<FederalCivicFeedService>.Instance);
            return new Fixture(connection, service);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
