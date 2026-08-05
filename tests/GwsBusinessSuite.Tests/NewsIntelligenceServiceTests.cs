using System.Diagnostics;
using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class NewsIntelligenceServiceTests
{
    // Regression guard for a real incident: BatchSummarizeAsync's Ollama call had no timeout
    // of its own, relying on IOllamaService's HttpClient's 2-hour default - and this method is
    // reachable directly from RefreshAllAsync/RefreshTopicAsync/RefreshTopNewsAsync button
    // clicks on NewsIntelligence.razor, not just the background refresh timers. A wedged
    // Ollama could hold the single global OllamaWorkloadScheduler lease for up to 2 hours from
    // a live page click, freezing every AI feature app-wide - the same mechanism that caused a
    // production 524 elsewhere. This intentionally runs a real ~60s clock (the production
    // bound) rather than a shortened one, to prove actual behavior, not just that some
    // CancellationToken object got created.
    // Uses an explicit WaitAsync(...) below rather than [Fact(Timeout=...)] - xunit v2's
    // Timeout attribute doesn't reliably abort a hung async Task across all runners, whereas
    // WaitAsync genuinely throws if RefreshTopicAsync doesn't complete in time.
    [Fact]
    public async Task RefreshTopicAsync_WhenOllamaHangsIndefinitely_StillCompletesWithinTheBoundedTimeout()
    {
        var (db, factory) = await CreateDbAsync();
        var topic = new WatchedTopic
        {
            Name = "Python",
            Keywords = "python",
            ColorHex = "#2563eb",
            TopicType = WatchedTopicTypes.General // routes through Google News RSS, not Hacker News
        };
        db.WatchedTopics.Add(topic);
        await db.SaveChangesAsync();

        var handler = new RecordingHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            return url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleGoogleNewsRss, Encoding.UTF8, "application/rss+xml")
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var ollama = new HangingOllamaService();
        var service = CreateService(factory, new HttpClient(handler), ollama);

        var stopwatch = Stopwatch.StartNew();
        await service.RefreshTopicAsync(topic.Id).WaitAsync(TimeSpan.FromSeconds(80));
        stopwatch.Stop();

        Assert.True(ollama.WasCancelled, "the bounded per-call timeout should have cancelled the hung generation");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(80),
            $"RefreshTopicAsync should bound its own Ollama call (~60s) rather than hang toward the HttpClient's 2-hour default; took {stopwatch.Elapsed}.");
    }

    private const string SampleGoogleNewsRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
          <title>Google News - python</title>
          <item>
            <title>Python 4.0 announced - Example Times</title>
            <link>https://example.com/python-4</link>
            <pubDate>Wed, 05 Aug 2026 12:00:00 GMT</pubDate>
            <description>A short description.</description>
          </item>
        </channel></rss>
        """;

    private sealed class HangingOllamaService : IOllamaService
    {
        public bool WasCancelled { get; private set; }

        public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            return string.Empty;
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }


    [Fact]
    public async Task GetFeedAsync_WithNullTopicId_ReturnsTopAndTopicArticlesTogether()
    {
        var (db, factory) = await CreateDbAsync();

        var topic = new WatchedTopic
        {
            Name = "AI",
            Keywords = "ai, agents",
            ColorHex = "#2563eb"
        };
        db.WatchedTopics.Add(topic);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        db.NewsItems.AddRange(
            new NewsItem
            {
                TopicId = null,
                Title = "Top story",
                Url = "https://example.com/top-story",
                Source = "Reuters",
                PublishedAt = now.AddMinutes(-5),
                FetchedAt = now.AddMinutes(-3)
            },
            new NewsItem
            {
                TopicId = topic.Id,
                Title = "Topic story",
                Url = "https://example.com/topic-story",
                Source = "The Verge",
                PublishedAt = now.AddMinutes(-2),
                FetchedAt = now.AddMinutes(-1)
            },
            new NewsItem
            {
                TopicId = topic.Id,
                Title = "Expired story",
                Url = "https://example.com/expired-story",
                Source = "AP News",
                PublishedAt = now.AddDays(-2),
                FetchedAt = now.AddDays(-2)
            });
        await db.SaveChangesAsync();

        var service = CreateService(factory);

        var result = await service.GetFeedAsync(null);

        Assert.Collection(result.Items,
            item =>
            {
                Assert.Equal("Topic story", item.Title);
                Assert.Equal(topic.Id, item.TopicId);
                Assert.Equal("AI", item.TopicName);
            },
            item =>
            {
                Assert.Equal("Top story", item.Title);
                Assert.Null(item.TopicId);
                Assert.Equal("Top News", item.TopicName);
            });
        Assert.Equal(now.AddMinutes(-1), result.LastRefreshedAt);
    }

    [Fact]
    public async Task GetFeedAsync_WithTopicId_ReturnsOnlyRequestedTopicArticles()
    {
        var (db, factory) = await CreateDbAsync();

        var firstTopic = new WatchedTopic
        {
            Name = "AI",
            Keywords = "ai",
            ColorHex = "#2563eb"
        };
        var secondTopic = new WatchedTopic
        {
            Name = "Security",
            Keywords = "security",
            ColorHex = "#ef4444"
        };
        db.WatchedTopics.AddRange(firstTopic, secondTopic);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        db.NewsItems.AddRange(
            new NewsItem
            {
                TopicId = firstTopic.Id,
                Title = "AI story",
                Url = "https://example.com/ai-story",
                Source = "Reuters",
                PublishedAt = now.AddMinutes(-3),
                FetchedAt = now.AddMinutes(-2)
            },
            new NewsItem
            {
                TopicId = secondTopic.Id,
                Title = "Security story",
                Url = "https://example.com/security-story",
                Source = "BBC",
                PublishedAt = now.AddMinutes(-2),
                FetchedAt = now.AddMinutes(-1)
            },
            new NewsItem
            {
                TopicId = null,
                Title = "Top story",
                Url = "https://example.com/top-story",
                Source = "AP News",
                PublishedAt = now.AddMinutes(-1),
                FetchedAt = now
            });
        await db.SaveChangesAsync();

        var service = CreateService(factory);

        var result = await service.GetFeedAsync(firstTopic.Id);

        var item = Assert.Single(result.Items);
        Assert.Equal("AI story", item.Title);
        Assert.Equal(firstTopic.Id, item.TopicId);
        Assert.Equal("AI", item.TopicName);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldPersistTopicType()
    {
        var (_, factory) = await CreateDbAsync();
        var service = CreateService(factory);

        var created = await service.CreateTopicAsync("Blazor", "blazor", "#2563eb", WatchedTopicTypes.Technical);

        Assert.Equal(WatchedTopicTypes.Technical, created.TopicType);
        var topics = await service.ListTopicsAsync();
        Assert.Equal(WatchedTopicTypes.Technical, Assert.Single(topics).TopicType);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldDefaultToGeneral_WhenTopicTypeIsNotRecognized()
    {
        var (_, factory) = await CreateDbAsync();
        var service = CreateService(factory);

        var created = await service.CreateTopicAsync("Atlanta", "atlanta", "#ef4444", "not-a-real-type");

        Assert.Equal(WatchedTopicTypes.General, created.TopicType);
    }

    [Fact]
    public async Task UpdateTopicAsync_ShouldChangeTopicType()
    {
        var (_, factory) = await CreateDbAsync();
        var service = CreateService(factory);
        var created = await service.CreateTopicAsync("Python", "python", "#16a34a", WatchedTopicTypes.General);

        var updated = await service.UpdateTopicAsync(created.Id, "Python", "python", "#16a34a", isActive: true, WatchedTopicTypes.Technical);

        Assert.Equal(WatchedTopicTypes.Technical, updated.TopicType);
    }

    private static NewsIntelligenceService CreateService(IAppDbContextFactory factory) =>
        CreateService(factory, new HttpClient(), new FakeOllamaService());

    private static NewsIntelligenceService CreateService(IAppDbContextFactory factory, HttpClient httpClient, IOllamaService ollama)
    {
        var options = Options.Create(new ContentStudioOptions { Model = "llama3.2" });

        return new NewsIntelligenceService(
            factory,
            ollama,
            options,
            httpClient,
            new MemoryCache(new MemoryCacheOptions()),
            new NewsRefreshState(),
            NullLogger<NewsIntelligenceService>.Instance);
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

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class FakeOllamaService : IOllamaService
    {
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
