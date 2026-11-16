using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
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

    // 05-11-26: with no model on the server, kinds had fallen back to keywords since 31-10-26.
    [Fact]
    public void TheKindsExamples_ShouldBeEmbeddedByTheMac_AlongsideHeadlines_UntilTheyHaveVectors()
    {
        var news = NewService();
        news.Store.AddArticles([Article("a1", "Flood in Izmir")]);
        news.KindExampleJobsForRelay(["llama3.2"]).Should().BeEmpty("only the server's embedding model can make comparable vectors");

        var kinds = news.KindExampleJobsForRelay(["embeddinggemma:latest"]);
        var examples = SituationKinds.Examples.Sum(pair => pair.Value.Length);
        kinds.Should().HaveCount(examples).And.OnlyContain(job => job.Id.StartsWith(WorldNewsService.KindJobPrefix));
        kinds.Select(job => job.Text).Should().Contain(SituationKinds.Examples.First().Value.First());

        // Half of them arrive with a headline; the headline is saved, the kinds wait for the rest.
        var half = kinds.Take(examples / 2).Select(job => (job.Id, new[] { 1f, 0f })).ToList();
        news.SaveRelayEmbeddings("embeddinggemma", [.. half, ("a1", [0f, 1f])]).Should().BeNull();
        news.KindExampleJobsForRelay(["embeddinggemma:latest"]).Should().HaveCount(examples, "they're still asked for until every one has a vector");
        news.Store.FillEmbeddings([("a1", [1f, 0f])]).Should().Be(0, "the headline's vector was saved");

        news.SaveRelayEmbeddings("embeddinggemma", kinds.Skip(examples / 2).Select(job => (job.Id, new[] { 0f, 1f })).ToList()).Should().BeNull();
        news.KindExampleJobsForRelay(["embeddinggemma:latest"]).Should().BeEmpty("kinds are classified by meaning again");
    }

    [Fact]
    public void WhenTheServersModelIsTheOnlySource_TheMacShouldGetNoWork_AndTheServerNeverStandsAside()
    {
        var news = NewService(new() { ["ServerAi:Mode"] = "Full", ["ServerAi:SingleSource"] = "true" });
        news.Store.AddArticles([Article("a1", "Flood in Izmir")]);
        news.Store.SetLastFetched("world-news:embed-relay", DateTimeOffset.UtcNow);

        news.EmbedJobsForRelay(["embeddinggemma:latest"], 64).Jobs.Should().BeEmpty();
        news.KindExampleJobsForRelay(["embeddinggemma:latest"]).Should().BeEmpty();
        news.SummaryJobsForRelay(5).Should().BeEmpty();
        news.EmbedRelayActive(DateTimeOffset.UtcNow).Should().BeFalse("a Mac that asked earlier doesn't make the server stand aside");
        news.RelayActive(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public async Task EnglishPass_ShouldProcessBoundedUngroupedReports_WithoutOpeningAnAnalysis()
    {
        var model = new EnglishAi();
        using var news = NewService(ollama: model);
        var reports = Enumerable.Range(0, 12).Select(i =>
            Article($"report-{i}", $"City roads reopen after flooding {i}") with
            { SeenAt = DateTimeOffset.UtcNow.AddDays(-i) }).ToArray();
        news.Store.AddArticles(reports);

        (await news.ProcessEnglishAsync(CancellationToken.None)).Should().BeTrue();

        var processed = news.Store.RecentReports(DateTimeOffset.UtcNow.AddDays(-30))
            .Where(report => report.English?.Summary is not null).ToList();
        processed.Should().HaveCount(HeadlineTranslator.ReportsPerCall);
        processed.Select(report => report.Id).Should().Contain(["report-0", "report-1", "report-10", "report-11"],
            "each pass serves incoming reports and the retained backlog");
        news.Store.TopEvents(DateTimeOffset.UtcNow.AddDays(-30), 10).Should().BeEmpty("translation does not require or fabricate a grouped event");
    }

    [Fact]
    public async Task EnglishPass_ShouldWaitForUnavailableAi_WithoutMarkingReportsTranslatedOrHotLooping()
    {
        var model = new EnglishAi { Available = false };
        using var news = NewService(ollama: model);
        news.Store.AddArticles([Article("waiting", "City roads reopen after flooding")]);

        (await news.ProcessEnglishAsync(CancellationToken.None)).Should().BeFalse();
        news.EnglishStatus.Should().Contain("waiting");
        news.Store.RecentReports(DateTimeOffset.UtcNow.AddDays(-1)).Single().English.Should().BeNull();
        (await news.ProcessEnglishAsync(CancellationToken.None)).Should().BeFalse();
        model.Calls.Should().Be(1, "failed reports have a durable retry delay");
        news.Store.ReportsDueEnglish(DateTimeOffset.UtcNow.AddMinutes(16), 8).Single().Id.Should().Be("waiting");
    }

    private WorldNewsService NewService(Dictionary<string, string?>? settings = null, IOllamaService? ollama = null)
    {
        var values = new Dictionary<string, string?>(settings ?? []) { ["WorldNews:DatabasePath"] = Path.Combine(_folder, "worldnews.db") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        if (ollama is not null) services.AddSingleton(ollama);
        return new WorldNewsService(configuration, Options.Create(new ContentStudioOptions()), new PlainHttp(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<WorldNewsService>.Instance);
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

    private sealed class EnglishAi : IOllamaService
    {
        public bool Available { get; init; } = true;
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (!Available) throw new ServerAiUnavailableException("Test AI", "Wait for a model.");
            using var input = JsonDocument.Parse(userPrompt);
            return Task.FromResult(JsonSerializer.Serialize(input.RootElement.EnumerateArray().Select(report => new
            {
                number = report.GetProperty("number").GetInt32(),
                headline = report.GetProperty("headline").GetString(),
                description = report.GetProperty("description").GetString(),
                summary = "English feed report summary based on the supplied headline."
            })));
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
