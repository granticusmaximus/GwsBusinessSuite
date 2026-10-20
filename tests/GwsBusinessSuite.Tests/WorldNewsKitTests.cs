using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

public sealed class WorldNewsKitTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly NewsEdition France = new("France", "French", "fr", "FR", "FR:fr");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"world-news-{Guid.NewGuid():N}.db");

    // Trimmed from the live France edition (20-10-26): one story with related coverage, one without.
    private const string FranceFeed = """
        <rss version="2.0"><channel>
          <item>
            <title>Couvre-feu à Paris après les violences - Le Monde.fr</title>
            <link>https://news.google.com/rss/articles/AAA?oc=5</link>
            <guid isPermaLink="false">story-1</guid>
            <pubDate>Tue, 20 Oct 2026 14:49:57 GMT</pubDate>
            <description>&lt;ol&gt;&lt;li&gt;&lt;a href="https://news.google.com/rss/articles/AAA?oc=5" target="_blank"&gt;Couvre-feu à Paris après les violences&lt;/a&gt;&amp;nbsp;&amp;nbsp;&lt;font color="#6f6f6f"&gt;Le Monde.fr&lt;/font&gt;&lt;/li&gt;&lt;li&gt;&lt;a href="https://news.google.com/rss/articles/BBB?oc=5" target="_blank"&gt;Paris : un couvre-feu &amp;amp; des renforts&lt;/a&gt;&amp;nbsp;&amp;nbsp;&lt;font color="#6f6f6f"&gt;franceinfo&lt;/font&gt;&lt;/li&gt;&lt;/ol&gt;</description>
            <source url="https://www.lemonde.fr">Le Monde.fr</source>
          </item>
          <item>
            <title>Le Japon lance une mission vers les lunes de Mars - RFI</title>
            <link>https://news.google.com/rss/articles/CCC?oc=5</link>
            <guid isPermaLink="false">story-2</guid>
            <pubDate>Tue, 20 Oct 2026 12:21:01 GMT</pubDate>
            <description>&lt;a href="https://news.google.com/rss/articles/CCC?oc=5"&gt;Le Japon lance une mission vers les lunes de Mars&lt;/a&gt;</description>
            <source url="https://www.rfi.fr">RFI</source>
          </item>
        </channel></rss>
        """;

    // Trimmed from a live GDELT DOC answer (20-10-26).
    private const string GdeltAnswer = """
        {"articles":[
          {"url":"https://www.sondakika.com/haber/sel-123/?utm_source=x","url_mobile":"","title":"İstanbulda sele kapılan motokurye metrelerce sürüklendi , o anlar kamerada","seendate":"20261020T090000Z","socialimage":"","domain":"sondakika.com","language":"Turkish","sourcecountry":"Turkey"},
          {"url":"https://at.wetter.com/news/flut","url_mobile":"","title":"Istanbul nach der Flut : Zahl der Todesopfer steigt auf acht","seendate":"20261019T173000Z","socialimage":"","domain":"at.wetter.com","language":"German","sourcecountry":"Germany"},
          {"url":"","title":"no url"}
        ]}
        """;

    [Fact]
    public void Normalizer_ShouldGiveOneIdPerStory_AndTidyFeedTitles()
    {
        ArticleNormalizer.CanonicalUrl("https://WWW.Example.com/news/story/?utm_source=x&id=7&fbclid=y#top")
            .Should().Be("https://example.com/news/story?id=7");
        ArticleNormalizer.IdFor("https://www.example.com/a/?utm_medium=rss")
            .Should().Be(ArticleNormalizer.IdFor("https://example.com/a"));
        ArticleNormalizer.CleanTitle("Istanbul nach der Flut : Zahl steigt , heute &amp; morgen  ")
            .Should().Be("Istanbul nach der Flut: Zahl steigt, heute & morgen");
    }

    [Fact]
    public void GoogleNews_ShouldReadEachStoryAndItsRelatedCoverage()
    {
        var articles = GoogleNewsFeed.Parse(FranceFeed, France);

        articles.Should().HaveCount(3);
        articles[0].Title.Should().Be("Couvre-feu à Paris après les violences");
        articles[0].Outlet.Should().Be("Le Monde.fr");
        articles[1].Title.Should().Be("Paris : un couvre-feu & des renforts".Replace(" :", ":"));
        articles[1].Outlet.Should().Be("franceinfo");
        articles[0].GroupHint.Should().Be(articles[1].GroupHint).And.Be("gn:FR:fr:story-1");
        articles[2].Title.Should().Be("Le Japon lance une mission vers les lunes de Mars", "the outlet suffix is trimmed");
        articles[2].Outlet.Should().Be("RFI");
        articles.Should().OnlyContain(a => a.Language == "French" && a.Country == "France" && a.Feed == "google-news:FR:fr");
        articles[2].SeenAt.Should().Be(new DateTimeOffset(2026, 10, 20, 12, 21, 1, TimeSpan.Zero));
        GoogleNewsFeed.Editions.Select(e => e.Ceid).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Gdelt_ShouldParseArticlesWithTheirLanguageAndCountry()
    {
        var articles = GdeltDocClient.Parse(GdeltAnswer);

        articles.Should().HaveCount(2);
        articles[0].Title.Should().Be("İstanbulda sele kapılan motokurye metrelerce sürüklendi, o anlar kamerada");
        articles[0].Outlet.Should().Be("sondakika.com");
        articles[0].Language.Should().Be("Turkish");
        articles[0].Country.Should().Be("Turkey");
        articles[0].SeenAt.Should().Be(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        articles[0].Id.Should().Be(ArticleNormalizer.IdFor("https://sondakika.com/haber/sel-123"));
        GdeltDocClient.Parse("""{"articles":null}""").Should().BeEmpty();
    }

    [Fact]
    public async Task Gdelt_ShouldPaceRequests_AndPauseAfterBeingToldToSlowDown()
    {
        var time = new ManualTime(Now);
        var waits = new List<TimeSpan>();
        var handler = new FakeHttp(_ => (HttpStatusCode.OK, GdeltAnswer));
        var client = new GdeltDocClient(new HttpClient(handler), time, (wait, _) => { waits.Add(wait); time.Advance(wait); return Task.CompletedTask; });

        (await client.SearchAsync("istanbul flood", Now.AddHours(-2), CancellationToken.None)).Articles.Should().HaveCount(2);
        time.Advance(TimeSpan.FromSeconds(1));
        await client.SearchAsync("sourcecountry:turkey", Now.AddDays(-3), CancellationToken.None);

        waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(5), "calls are spaced 6 seconds apart");
        handler.Urls[0].Should().Contain("query=istanbul%20flood").And.Contain("startdatetime=20261020130000").And.Contain("maxrecords=250");
        handler.Urls[1].Should().Contain("startdatetime=20261019150006", "GDELT is never asked more than a day back");

        handler.Answer = _ => (HttpStatusCode.TooManyRequests, "Please limit requests to one every 5 seconds");
        (await client.SearchAsync("a", Now, CancellationToken.None)).Note.Should().Contain("paused");
        client.IsCoolingDown.Should().BeTrue();
        (await client.SearchAsync("b", Now, CancellationToken.None)).Note.Should().Contain("paused");
        handler.Urls.Should().HaveCount(3, "nothing is sent while paused");

        time.Advance(GdeltDocClient.Cooldown);
        handler.Answer = _ => (HttpStatusCode.OK, "Your query was too short or too long.");
        (await client.SearchAsync("x", Now, CancellationToken.None)).Note.Should().StartWith("GDELT: Your query was too short");
    }

    [Fact]
    public void Clusterer_ShouldGroupByMeaning_WithinTheWindow()
    {
        var flood = new[] { 1f, 0.1f, 0f };
        var vote = new[] { 0f, 1f, 0.1f };
        var events = new List<NewsEvent>();
        var ids = 0;
        var result = EventClusterer.Assign(
        [
            (Article("tr1", "İstanbul'da sel", "Turkish", Now.AddHours(-5)), flood),
            (Article("de1", "Flut in Istanbul", "German", Now.AddHours(-4)), new[] { 0.95f, 0.15f, 0.05f }),
            (Article("en1", "Istanbul floods kill eight", "English", Now.AddHours(-3)), new[] { 0.9f, 0.05f, 0f }),
            (Article("en2", "Alberta votes to stay", "English", Now.AddHours(-2)), vote),
            (Article("tr2", "İstanbul'da yeni sel", "Turkish", Now.AddDays(3)), flood),
        ], events, [], EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"e{ids++}");

        result.Created.Should().Be(3, "the flood, the vote, and the same flood three days later (outside the window)");
        var floodEvent = events.Single(e => e.Id == "e0");
        floodEvent.ArticleCount.Should().Be(3);
        floodEvent.Title.Should().Be("Istanbul floods kill eight", "an English headline is shown once there is one");
        floodEvent.FirstSeen.Should().Be(Now.AddHours(-5));
        floodEvent.LastSeen.Should().Be(Now.AddHours(-3));
        result.Assignments.Should().Contain(("en2", "e1")).And.Contain(("tr2", "e2"));
    }

    [Fact]
    public void Clusterer_ShouldFollowAFeedsGrouping_OnlyWhenTheReportIsClose()
    {
        var events = new List<NewsEvent>();
        var ids = 0;
        var hints = new Dictionary<string, string>();
        EventClusterer.Assign(
        [
            (Article("a", "Curfew in Paris", "French", Now.AddHours(-3), "g1"), new[] { 1f, 0f, 0f }),
            // 0.6 to the event: below the threshold, within the slack, so the feed's grouping wins.
            (Article("b", "Paris renforts", "French", Now.AddHours(-2), "g1"), new[] { 0.6f, 0.8f, 0f }),
            // 0.0 to the event: the feed listed it under the story, but it's something else.
            (Article("c", "Football tonight", "French", Now.AddHours(-1), "g1"), new[] { 0f, 0f, 1f }),
        ], events, hints, EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"e{ids++}");

        events.Should().HaveCount(2);
        events[0].ArticleCount.Should().Be(2);
        hints["g1"].Should().Be("e0");
    }

    [Fact]
    public void MergeTwins_ShouldJoinTwinEvents_IntoTheLargerOne()
    {
        var a = Event("a", "Alberta rejects separation", new[] { 1f, 0.05f, 0f }, 3);
        var b = Event("b", "Referendum in Alberta", new[] { 0.98f, 0.1f, 0f }, 2);
        var c = Event("c", "Trump on sanctions", new[] { 0.6f, 0.8f, 0f }, 4);
        var events = new List<NewsEvent> { a, b, c };

        var merges = EventClusterer.MergeTwins(events, new HashSet<NewsEvent> { b }, EventClusterer.DefaultMergeThreshold);

        merges.Should().ContainSingle().Which.Should().Be(new EventMerge(a, b));
        a.ArticleCount.Should().Be(5);
        events.Should().Equal(a, c);
    }

    [Fact]
    public void Store_ShouldKeepReportsEventsAndFeedState()
    {
        var store = new WorldNewsStore(_dbPath);
        var tr = Article("tr", "İstanbul'da sel", "Turkish", Now.AddHours(-3)) with { Country = "Turkey", Outlet = "Hürriyet" };
        var de = Article("de", "Flut in Istanbul", "German", Now.AddHours(-2)) with { Country = "Germany", Outlet = "FAZ" };
        store.AddArticles([tr, de]).Should().Be(2);
        store.AddArticles([tr]).Should().Be(0, "a stored report is never added twice");

        store.ArticlesToEmbed(10).Select(a => a.Id).Should().Equal("tr", "de");
        store.SaveEmbeddings([("tr", new[] { 1f, 0f }), ("de", new[] { 0.9f, 0.1f })]);
        store.ArticlesToEmbed(10).Should().BeEmpty();

        var events = store.ActiveEvents(Now.AddDays(-2));
        var ids = 0;
        var clustering = EventClusterer.Assign(store.EmbeddedUnassigned(), events, store.GroupHintEvents(Now.AddDays(-2)),
            EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"e{ids++}");
        store.SaveClustering(clustering);

        var top = store.TopEvents(Now.AddDays(-2), 5).Should().ContainSingle().Subject;
        top.OutletCount.Should().Be(2);
        top.Countries.Should().BeEquivalentTo("Turkey", "Germany");
        top.Languages.Should().BeEquivalentTo("Turkish", "German");
        store.ActiveEvents(Now.AddDays(-2)).Single().Sum.Should().HaveCount(2);
        store.ArticlesForEvent(top.Id).Select(a => a.Id).Should().Equal("tr", "de");

        var twin = Event("twin", "Istanbul flood", new[] { 1f, 0f }, 1);
        store.SaveClustering(new ClusterResult([], [twin], 1));
        var keep = store.ActiveEvents(Now.AddDays(-2)).Single(e => e.Id == top.Id);
        keep.Absorb(twin);
        store.ApplyMerges([new EventMerge(keep, twin)]);
        store.ActiveEvents(Now.AddDays(-2)).Should().ContainSingle().Which.ArticleCount.Should().Be(3);

        store.LastFetched("gdelt:x").Should().BeNull();
        store.SetLastFetched("gdelt:x", Now);
        store.LastFetched("gdelt:x").Should().Be(Now);

        store.Prune(Now.AddHours(-2.5));
        store.ArticlesForEvent(top.Id).Select(a => a.Id).Should().Equal("de");
    }

    [Fact]
    public async Task OllamaEmbed_ShouldReturnOneVectorPerInput()
    {
        var handler = new FakeHttp(_ => (HttpStatusCode.OK, """{"embeddings":[[0.1,0.2],[0.3,0.4]]}"""));
        var client = new OllamaClient(new Uri("http://localhost:11434/"), new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") });

        var vectors = await client.EmbedAsync("embeddinggemma", ["a", "b"], CancellationToken.None);

        vectors.Should().HaveCount(2);
        vectors[1].Should().Equal(0.3f, 0.4f);
        handler.Bodies.Single().Should().Contain("\"model\":\"embeddinggemma\"").And.Contain("\"input\":[\"a\",\"b\"]");
        (await client.EmbedAsync("embeddinggemma", [], CancellationToken.None)).Should().BeEmpty();
        var act = () => client.EmbedAsync("embeddinggemma", ["only one"], CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 embeddings for 1 inputs*");
    }

    [Fact]
    public async Task Pipeline_ShouldReadEmbedAndGroup_AndCopeWithoutOllama()
    {
        var store = new WorldNewsStore(_dbPath);
        var ollamaUp = true;
        var handler = new FakeHttp(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://news.google.com/rss?hl=fr", StringComparison.Ordinal)) return (HttpStatusCode.OK, FranceFeed);
            if (url.StartsWith("https://news.google.com/", StringComparison.Ordinal)) return (HttpStatusCode.ServiceUnavailable, "");
            if (!ollamaUp) return (HttpStatusCode.InternalServerError, "model not loaded");
            // A stand-in embedding model: curfew stories point one way, Mars stories another.
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            var vectors = body.RootElement.GetProperty("input").EnumerateArray()
                .Select(t => t.GetString()!.Contains("Mars") ? new[] { 0f, 1f } : new[] { 1f, 0f });
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new { embeddings = vectors }));
        });
        var http = new HttpClient(handler);
        var options = new WorldNewsOptions { Editions = [France, new NewsEdition("Japan", "Japanese", "ja", "JP", "JP:ja")] };
        var pipeline = new WorldNewsPipeline(store, http, new GdeltDocClient(http, new ManualTime(Now)),
            new OllamaClient(new Uri("http://localhost:11434/"), http), new ManualTime(Now), options);

        ollamaUp = false;
        var first = await pipeline.RunCycleAsync(CancellationToken.None);
        first.Added.Should().Be(3);
        first.Embedded.Should().Be(0);
        first.Notes.Should().Contain(n => n.StartsWith("Google News Japan")).And.Contain(n => n.StartsWith("Local Ollama couldn't embed"));

        ollamaUp = true;
        var second = await pipeline.RunCycleAsync(CancellationToken.None);
        second.Added.Should().Be(0);
        second.Embedded.Should().Be(3);
        second.NewEvents.Should().Be(2);
        store.TopEvents(Now.AddDays(-2), 5).Select(e => e.ArticleCount).Should().Equal(2, 1);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
    }

    private static WorldArticle Article(string id, string title, string language, DateTimeOffset seen, string? hint = null) =>
        new(id, $"https://example.com/{id}", title, "Outlet " + id, language, "Somewhere", seen, "test", hint);

    private static NewsEvent Event(string id, string title, float[] direction, int count)
    {
        var unit = EventClusterer.Normalize(direction);
        return new NewsEvent
        {
            Id = id, Title = title, TitleLanguage = "English", Sum = unit.Select(v => v * count).ToArray(),
            ArticleCount = count, FirstSeen = Now.AddHours(-3), LastSeen = Now.AddHours(-1)
        };
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Answer { get; set; } = answer;
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = Answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        }
    }
}
