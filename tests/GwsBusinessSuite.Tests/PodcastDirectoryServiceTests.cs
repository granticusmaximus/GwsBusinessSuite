using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Podcasts;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class PodcastDirectoryServiceTests
{
    [Fact]
    public async Task SearchCatalogAsync_ShouldMapAndDeduplicateAppleResults()
    {
        var (_, factory) = await CreateDbAsync();
        var requests = new List<Uri>();
        using var handler = new RecordingHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "results": [
                        {
                          "collectionName": "Tech Weekly",
                          "artistName": "Grant",
                          "description": "<p>Fresh &amp; fast</p>",
                          "primaryGenreName": "Technology",
                          "feedUrl": "https://feeds.example.com/tech-weekly.xml",
                          "artworkUrl600": "https://images.example.com/tech600.jpg",
                          "collectionViewUrl": "https://podcasts.apple.com/us/podcast/tech-weekly/id101",
                          "collectionId": 101
                        },
                        {
                          "trackName": "Tech Weekly",
                          "artistName": "Grant",
                          "description": "<p>Duplicate entry</p>",
                          "primaryGenreName": "Technology",
                          "feedUrl": "https://feeds.example.com/tech-weekly.xml",
                          "artworkUrl100": "https://images.example.com/tech100.jpg",
                          "collectionViewUrl": "https://podcasts.apple.com/us/podcast/tech-weekly/id101",
                          "collectionId": 101
                        },
                        {
                          "collectionName": "History Hour",
                          "artistName": "Watson",
                          "description": "Deep dives",
                          "primaryGenreName": "History",
                          "feedUrl": "https://feeds.example.com/history-hour.xml",
                          "artworkUrl100": "https://images.example.com/history100.jpg",
                          "collectionViewUrl": "https://podcasts.apple.com/us/podcast/history-hour/id202",
                          "collectionId": 202
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var service = CreateService(factory, handler);

        var results = await service.SearchCatalogAsync("tech");

        results.Should().HaveCount(2);
        results[0].Title.Should().Be("Tech Weekly");
        results[0].Description.Should().Be("Fresh & fast");
        results[0].ImageUrl.Should().Be("https://images.example.com/tech600.jpg");
        results[1].Category.Should().Be("History");
        requests.Should().ContainSingle();
        requests[0].Query.Should().Contain("term=tech");
    }

    [Fact]
    public async Task DiscoverAsync_WithAllCategory_ShouldUseCuratedQueries_NotLiteralAll()
    {
        var (_, factory) = await CreateDbAsync();
        var requests = new List<Uri>();
        using var handler = new RecordingHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(factory, handler);

        var results = await service.DiscoverAsync("All");

        results.Should().BeEmpty();
        requests.Should().NotBeEmpty();
        requests.Should().OnlyContain(uri => !uri.Query.Contains("term=All", StringComparison.OrdinalIgnoreCase));
        requests.Should().Contain(uri => uri.Query.Contains("term=podcast", StringComparison.OrdinalIgnoreCase));
        requests.Should().Contain(uri => uri.Query.Contains("term=comedy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SavePodcastAsync_ShouldBeIdempotent_WhenFeedMatchesExistingShow()
    {
        var (db, factory) = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
        });
        var service = CreateService(factory, handler);

        var podcast = new PodcastCatalogItem(
            Title: "Tech Weekly",
            Author: "Grant",
            Description: "Latest episodes",
            Category: "Technology",
            FeedUrl: "https://feeds.example.com/tech-weekly.xml",
            ImageUrl: "https://images.example.com/tech.jpg",
            AppleUrl: "https://podcasts.apple.com/us/podcast/tech-weekly/id101",
            ItunesId: "101");

        var firstSave = await service.SavePodcastAsync(podcast);
        var secondSave = await service.SavePodcastAsync(podcast with { FeedUrl = "https://feeds.example.com/tech-weekly.xml" });

        firstSave.AlreadySaved.Should().BeFalse();
        secondSave.AlreadySaved.Should().BeTrue();
        (await db.PodcastShows.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ListLibraryAsync_ShouldApplyMigrations_WhenPodcastTablesAreMissing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
        });
        var service = CreateService(new FakeAppDbContextFactory(options), handler);

        var library = await service.ListLibraryAsync();

        library.Should().BeEmpty();

        await using var verifyDb = new ApplicationDbContext(options);
        (await verifyDb.PodcastShows.CountAsync()).Should().Be(0);
        (await verifyDb.PodcastEpisodes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetPodcastDetailAsync_ShouldRefreshEpisodesFromRssFeed()
    {
        var (db, factory) = await CreateDbAsync();
        var feedUrl = "https://feeds.example.com/tech-weekly.xml";
        using var handler = new RecordingHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be(feedUrl);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
                      <channel>
                        <title>Tech Weekly</title>
                        <item>
                          <title>Episode One</title>
                          <description><![CDATA[<p>First &amp; best</p>]]></description>
                          <enclosure url="https://cdn.example.com/ep-1.mp3" type="audio/mpeg" length="123" />
                          <guid>ep-1</guid>
                          <pubDate>Wed, 09 Jul 2026 12:00:00 GMT</pubDate>
                          <itunes:duration>01:02:03</itunes:duration>
                        </item>
                        <item>
                          <title>Episode Two</title>
                          <description>Second episode</description>
                          <enclosure url="https://cdn.example.com/ep-2.mp3" type="audio/mpeg" length="456" />
                          <guid>ep-2</guid>
                          <pubDate>Tue, 08 Jul 2026 12:00:00 GMT</pubDate>
                          <itunes:duration>45:30</itunes:duration>
                        </item>
                      </channel>
                    </rss>
                    """,
                    Encoding.UTF8,
                    "application/rss+xml")
            };
        });

        var service = CreateService(factory, handler);
        var save = await service.SavePodcastAsync(new PodcastCatalogItem(
            Title: "Tech Weekly",
            Author: "Grant",
            Description: "Latest episodes",
            Category: "Technology",
            FeedUrl: feedUrl,
            ImageUrl: string.Empty,
            AppleUrl: "https://podcasts.apple.com/us/podcast/tech-weekly/id101",
            ItunesId: "101"));

        var detail = await service.GetPodcastDetailAsync(save.Podcast.Id, refreshEpisodes: true);

        detail.Should().NotBeNull();
        detail!.Episodes.Should().HaveCount(2);
        detail.Episodes[0].Title.Should().Be("Episode One");
        detail.Episodes[0].DurationSeconds.Should().Be(3723);
        detail.Episodes[0].Description.Should().Be("First & best");
        detail.Episodes[1].DurationSeconds.Should().Be(2730);

        var persisted = await db.PodcastEpisodes.OrderBy(x => x.Title).ToListAsync();
        persisted.Should().HaveCount(2);
        persisted[0].AudioUrl.Should().Be("https://cdn.example.com/ep-1.mp3");

        var savedShow = await db.PodcastShows.SingleAsync();
        savedShow.LastEpisodeRefreshAt.Should().NotBeNull();
    }

    // Regression test: a transient feed outage (or malformed XML) used to delete-then-
    // replace every existing episode with an empty set, and still stamp
    // LastEpisodeRefreshAt, blocking retry for the full refresh window while showing zero
    // episodes. A failed fetch should now leave existing episodes and the refresh
    // timestamp untouched.
    [Fact]
    public async Task GetPodcastDetailAsync_ShouldPreserveExistingEpisodes_WhenAFeedRefreshFails()
    {
        var (db, factory) = await CreateDbAsync();
        var feedUrl = "https://feeds.example.com/tech-weekly.xml";
        var requestCount = 0;
        using var handler = new RecordingHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <?xml version="1.0" encoding="UTF-8"?>
                        <rss version="2.0">
                          <channel>
                            <title>Tech Weekly</title>
                            <item>
                              <title>Episode One</title>
                              <description>First episode</description>
                              <enclosure url="https://cdn.example.com/ep-1.mp3" type="audio/mpeg" length="123" />
                              <guid>ep-1</guid>
                              <pubDate>Wed, 09 Jul 2026 12:00:00 GMT</pubDate>
                            </item>
                          </channel>
                        </rss>
                        """,
                        Encoding.UTF8,
                        "application/rss+xml")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("feed is down", Encoding.UTF8, "text/plain")
            };
        });

        var service = CreateService(factory, handler);
        var save = await service.SavePodcastAsync(new PodcastCatalogItem(
            Title: "Tech Weekly",
            Author: "Grant",
            Description: "Latest episodes",
            Category: "Technology",
            FeedUrl: feedUrl,
            ImageUrl: string.Empty,
            AppleUrl: "https://podcasts.apple.com/us/podcast/tech-weekly/id101",
            ItunesId: "101"));

        var firstDetail = await service.GetPodcastDetailAsync(save.Podcast.Id, refreshEpisodes: true);
        firstDetail!.Episodes.Should().HaveCount(1);
        var firstRefreshAt = (await db.PodcastShows.SingleAsync()).LastEpisodeRefreshAt;
        firstRefreshAt.Should().NotBeNull();

        var secondDetail = await service.GetPodcastDetailAsync(save.Podcast.Id, refreshEpisodes: true);

        secondDetail!.Episodes.Should().HaveCount(1, "a failed refresh must not wipe existing episodes");
        secondDetail.Episodes[0].Title.Should().Be("Episode One");
        var secondRefreshAt = (await db.PodcastShows.SingleAsync()).LastEpisodeRefreshAt;
        secondRefreshAt.Should().Be(firstRefreshAt, "a failed refresh must not reset the retry window");
    }

    private static PodcastDirectoryService CreateService(IAppDbContextFactory factory, HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new PodcastDirectoryService(factory, client, NullLogger<PodcastDirectoryService>.Instance);
    }

    private static async Task<(ApplicationDbContext Db, IAppDbContextFactory Factory)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return (db, new FakeAppDbContextFactory(options));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
