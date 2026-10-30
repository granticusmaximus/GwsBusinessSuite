using FluentAssertions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

// World News headlines embedded on the Mac (30-10-26): only a Mac with the server's own embedding
// model gets any, the server stands aside while it keeps asking, and the vectors it sends back are
// checked, saved where still missing and grouped straight away.
public sealed class WorldNewsEmbedRelayTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gws-embed-relay", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OnlyAMacWithTheServersModel_ShouldGetHeadlines_AndTheServerThenStandsAside()
    {
        var news = NewService();
        news.Store.AddArticles([Article("a1", "Flood in Izmir"), Article("a2", "Izmir flood rescue")]);

        var (model, jobs) = news.EmbedJobsForRelay(["llama3.2", "nomic-embed-text"], 64);
        model.Should().Be("embeddinggemma");
        jobs.Should().BeEmpty("its vectors couldn't be compared with the server's");
        news.EmbedRelayActive(DateTimeOffset.UtcNow).Should().BeFalse("so the server keeps embedding");

        (_, jobs) = news.EmbedJobsForRelay(["gemma4", "embeddinggemma:latest"], 64);
        jobs.Select(job => job.Id).Should().BeEquivalentTo(["a1", "a2"]);
        news.EmbedRelayActive(DateTimeOffset.UtcNow).Should().BeTrue();
        news.EmbedRelayActive(DateTimeOffset.UtcNow.AddMinutes(11)).Should().BeFalse("ten minutes without the Mac and the server embeds again");
    }

    [Fact]
    public void TheMacsVectors_ShouldBeChecked_AndGroupedStraightAway()
    {
        var news = NewService();
        news.Store.AddArticles([Article("a1", "Flood in Izmir"), Article("a2", "Izmir flood rescue")]);

        news.SaveRelayEmbeddings("nomic-embed-text", [("a1", [1f, 0f])]).Should().Contain("embeddinggemma");
        news.SaveRelayEmbeddings("embeddinggemma", [("a1", [1f, 0f]), ("a2", [1f, 0f, 0f])]).Should().Contain("2 numbers");
        news.Store.TopEvents(DateTimeOffset.UtcNow.AddDays(-1), 5).Should().BeEmpty("nothing was saved from a refused batch");

        news.SaveRelayEmbeddings("embeddinggemma", [("a1", [1f, 0f]), ("a2", [0.99f, 0.05f])]).Should().BeNull();

        news.Store.TopEvents(DateTimeOffset.UtcNow.AddDays(-1), 5).Should().ContainSingle()
            .Which.ArticleCount.Should().Be(2, "grouped as they arrived, not at the next read");
        news.Store.FillEmbeddings([("a1", [0f, 1f])]).Should().Be(0, "a grouped report keeps what it has");
    }

    private WorldNewsService NewService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorldNews:DatabasePath"] = Path.Combine(_folder, "worldnews.db") })
            .Build();
        return new WorldNewsService(configuration, Options.Create(new ContentStudioOptions()), new PlainHttp(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<WorldNewsService>.Instance);
    }

    private static WorldArticle Article(string id, string title) =>
        new(id, $"https://example.com/{id}", title, "Outlet " + id, "English", "Turkey", DateTimeOffset.UtcNow.AddMinutes(-5), "test", null);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed class PlainHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
