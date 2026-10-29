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
    public void SituationLinker_ShouldLinkEventsOnOneStory_AndKeepOthersAndOldOnesApart()
    {
        var rally = Event("rally", "Rally before the election", [1f, 0.1f, 0f], 4);
        var results = Event("results", "Election results", [0.95f, 0.25f, 0f], 6);
        var crash = Event("crash", "Train crash", [0f, 0.1f, 1f], 5);
        var later = Event("later", "New election called", [1f, 0.15f, 0f], 3);
        later.FirstSeen = Now.AddDays(20);
        later.LastSeen = Now.AddDays(20);
        var situations = new List<NewsSituation>();
        var ids = 0;

        var links = SituationLinker.Link([later, crash, results, rally], situations, SituationLinker.DefaultThreshold,
            SituationLinker.DefaultWindow, () => $"s{ids++}");

        links.Should().HaveCount(4);
        links.Single(l => l.EventId == "results").Situation.Should().BeSameAs(links.Single(l => l.EventId == "rally").Situation);
        links.Single(l => l.EventId == "crash").Situation.Should().NotBeSameAs(links.Single(l => l.EventId == "rally").Situation);
        links.Single(l => l.EventId == "later").Situation.Should().NotBeSameAs(links.Single(l => l.EventId == "rally").Situation,
            "twenty days on is outside the window, so it starts a new situation");
        situations.Should().HaveCount(3);
        SituationLinker.IsActive(Now.AddHours(-47), Now).Should().BeTrue();
        SituationLinker.IsActive(Now.AddHours(-49), Now).Should().BeFalse();
    }

    [Fact]
    public void Store_ShouldListSituationsOfTwoOrMoreEvents_AndKeepLinksThroughMergesAndPruning()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "rally", Reports("rally", 3), [1f, 0.1f]);
        SeedEvent(store, "results", Reports("results", 5), [0.95f, 0.2f]);
        SeedEvent(store, "crash", Reports("crash", 4), [0f, 1f]);
        SeedEvent(store, "small", Reports("small", 2), [1f, 0.1f]);
        var ids = 0;

        var links = SituationLinker.Link(store.EventsToLink(Now.AddDays(-14), SituationLinker.MinArticles),
            store.ActiveSituations(Now.AddDays(-14)), SituationLinker.DefaultThreshold, SituationLinker.DefaultWindow, () => $"s{ids++}");
        store.SaveLinks(links);

        links.Select(l => l.EventId).Should().BeEquivalentTo(["rally", "results", "crash"], "an event needs 3 reports to be placed");
        store.EventsToLink(Now.AddDays(-14), SituationLinker.MinArticles).Should().BeEmpty("each event is placed once");
        var situation = store.TopSituations(Now.AddDays(-1), 10).Should().ContainSingle("the crash is a situation of one event, not shown").Subject;
        situation.EventCount.Should().Be(2);
        situation.ArticleCount.Should().Be(8);
        situation.Title.Should().Be("results report 0", "the biggest event names it");
        store.EventsForSituation(situation.Id).Select(e => e.Id).Should().BeEquivalentTo(["rally", "results"]);

        // The merged event keeps the situation; a situation with no events left is pruned.
        var rally = store.ActiveEvents(Now.AddDays(-1)).Single(e => e.Id == "rally");
        var small = store.ActiveEvents(Now.AddDays(-1)).Single(e => e.Id == "small");
        small.Absorb(rally);
        store.ApplyMerges([new EventMerge(small, rally)]);
        store.EventsForSituation(situation.Id).Select(e => e.Id).Should().BeEquivalentTo(["small", "results"]);
        store.Prune(Now.AddDays(1));
        store.ActiveSituations(Now.AddDays(-30)).Should().BeEmpty();
    }

    [Fact]
    public void Store_ShouldAddTheSituationColumn_ToADatabaseFromBeforeSituations()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE events (id TEXT PRIMARY KEY, title TEXT NOT NULL, title_language TEXT NOT NULL, vector_sum BLOB NOT NULL,
                    article_count INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        var store = new WorldNewsStore(_dbPath);

        store.EventsToLink(Now.AddDays(-14), 1).Should().BeEmpty();
        new WorldNewsStore(_dbPath).TopSituations(Now.AddDays(-1), 10).Should().BeEmpty("opening it again doesn't add the column twice");
    }

    [Fact]
    public void Store_ShouldAlertAWatcherOnlyWhenTheirSituationGainsAnEvent()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "rally", Reports("rally", 3), [1f, 0f]);
        var situation = new NewsSituation { Id = "election", Sum = [3f, 0f], LastSeen = Now };
        store.SaveLinks([new SituationLink("rally", situation)]);

        store.Watch("Grant", situation.Id, Now);
        store.IsWatching("grant", situation.Id).Should().BeTrue();
        store.CreateWatchAlerts(Now).Should().BeEmpty("watching starts at the current event count");

        SeedEvent(store, "results", Reports("results", 4), [1f, 0f]);
        situation.Sum[0] = 7f;
        situation.LastSeen = Now.AddHours(1);
        store.SaveLinks([new SituationLink("results", situation)]);
        var alert = store.CreateWatchAlerts(Now.AddHours(1)).Should().ContainSingle().Subject;
        alert.Username.Should().Be("grant");
        alert.Message.Should().Be("1 new event in this watched situation.");
        store.CreateWatchAlerts(Now.AddHours(2)).Should().BeEmpty("the watch baseline advances atomically");

        store.Alerts("GRANT").Should().ContainSingle(a => !a.IsRead);
        store.MarkAlertRead("grant", alert.Id);
        store.Alerts("grant").Should().ContainSingle(a => a.IsRead);
        store.Unwatch("grant", situation.Id);
        store.IsWatching("grant", situation.Id).Should().BeFalse();
    }

    [Fact]
    public void Store_ShouldBuildTheWorldViewFromReportsInsideTheRequestedWindow()
    {
        var store = new WorldNewsStore(_dbPath);
        var flood = Reports("flood", 5).Select((a, i) => a with { Country = i < 3 ? "Turkey" : "Germany" }).ToArray();
        var election = Reports("election", 3).Select(a => a with { Country = "Brazil" }).ToArray();
        var markets = Reports("markets", 1).Select(a => a with { Country = "Germany" }).ToArray();
        var old = Reports("old", 5).Select(a => a with { Country = "France", SeenAt = Now.AddHours(-10) }).ToArray();
        SeedEvent(store, "flood", flood);
        SeedEvent(store, "election", election);
        SeedEvent(store, "markets", markets);
        SeedEvent(store, "old", old);
        store.SaveLinks([new SituationLink("election", new NewsSituation { Id = "vote", Sum = [1f, 0f], LastSeen = Now })]);

        var world = store.World(Now.AddHours(-3), storiesPerCountry: 1, biggestStories: 2);

        world.Countries.Select(c => c.Name).Should().Equal(["Brazil", "Germany", "Turkey"],
            "countries rank by recent report count, then name");
        world.Countries.Single(c => c.Name == "Germany").TopStories.Should().ContainSingle()
            .Which.Event.Id.Should().Be("flood", "it has more recent outlets than the markets story");
        world.Countries.Should().NotContain(c => c.Name == "France", "old reports are outside the window");
        world.BiggestStories.Select(s => s.Event.Id).Should().Equal("flood", "election");
        world.BiggestStories.Single(s => s.Event.Id == "election").SituationId.Should().Be("vote");
    }

    [Fact]
    public void Search_ShouldRankByMeaning_DropUnrelatedEvents_AndLimitEachSituation()
    {
        WorldNewsCandidate Candidate(string id, float[] direction, string? situation = null) => new(id, direction, situation);
        var crash = new[] { 1f, 0.1f, 0f };

        var hits = WorldNewsSearch.Rank(crash,
        [
            Candidate("crash", [1f, 0.12f, 0f], "s1"),
            Candidate("mourning", [0.95f, 0.2f, 0f], "s1"),
            Candidate("inquiry", [0.9f, 0.3f, 0f], "s1"),
            Candidate("tributes", [0.92f, 0.25f, 0f], "s1"),
            Candidate("other-crash", [0.8f, 0f, 0.4f]),
            Candidate("football", [0f, 0.1f, 1f]),
        ]);

        hits.Select(h => h.EventId).Should().Equal(["crash", "mourning", "tributes", "other-crash"],
            "three from one situation, then the next story; the football match is unrelated");
        hits.Should().BeInDescendingOrder(h => h.Score);
    }

    [Fact]
    public void Search_ShouldMatchHeadlineWords_WhenThereIsNoModel_AndPickVariedSources()
    {
        EventOverview Overview(string id, string title, int outlets, string? headline = null) =>
            new(id, title, outlets, outlets, [], [], Now, Now, headline);

        var ids = WorldNewsSearch.MatchWords("What about the train crash?", [
            Overview("a", "Spanish train crash leaves 68 dead", 10),
            Overview("b", "Train strike called off", 40),
            Overview("c", "Tren chocan en España", 90, headline: "Two trains crash in Spain"),
            Overview("d", "The elections: what happens next", 200),
        ]);
        ids.Should().Equal(["c", "a", "b"], "both words before one, then by coverage; question words like 'what' and 'the' don't count");

        var sources = WorldNewsSearch.Sources([
            Article("1", "Crash in Spain", "English", Now), Article("2", "More on the crash", "English", Now.AddMinutes(-5)),
            Article("3", "Third English report", "English", Now.AddMinutes(-9)), Article("4", "Choque de trenes", "Spanish", Now),
            Article("5", "Otro informe", "Spanish", Now.AddMinutes(-3)), Article("6", "Treinramp", "Dutch", Now)]);
        sources.Select(s => s.Language).Should().Equal(["English", "English", "Spanish", "Dutch"]);
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
        var options = new WorldNewsOptions { Editions = [France, new NewsEdition("Japan", "Japanese", "ja", "JP", "JP:ja")], Outlets = [] };
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

    // Shapes taken from the live feeds (21-10-26): an RSS 2.0 item with HTML in a CDATA
    // description and a "+0300" date, one with no link, one dated in the future.
    private const string OutletRss = """
        <rss version="2.0"><channel><title>Example</title>
          <item>
            <title>Floods hit Istanbul</title>
            <link>https://www.example.com/world/floods?utm_source=rss</link>
            <pubDate>Tue, 20 Oct 2026 12:00:00 +0300</pubDate>
            <description><![CDATA[<p>Heavy rain <b>flooded</b> streets &amp; homes in Istanbul on Tuesday.</p><img src="x.jpg">]]></description>
          </item>
          <item><title>No link here</title></item>
          <item>
            <title>Dated tomorrow</title>
            <link>https://www.example.com/world/tomorrow</link>
            <pubDate>Thu, 22 Oct 2026 12:00:00 GMT</pubDate>
            <description>Dated tomorrow</description>
          </item>
        </channel></rss>
        """;

    [Fact]
    public void OutletFeeds_ShouldReadRssRdfAndAtom_WithTheOutletsDescriptions()
    {
        var outlet = new NewsOutlet("Example", "English", "Turkey", "https://www.example.com/rss");
        var rss = OutletFeeds.Parse(new MemoryStream(Encoding.UTF8.GetBytes(OutletRss)), outlet, Now);
        rss.Should().HaveCount(2, "an item without a link is skipped");
        rss[0].Should().BeEquivalentTo(new
        {
            Id = ArticleNormalizer.IdFor("https://www.example.com/world/floods"),
            Title = "Floods hit Istanbul", Outlet = "Example", Language = "English", Country = "Turkey",
            SeenAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), Feed = "outlet:https://www.example.com/rss",
            Snippet = "Heavy rain flooded streets & homes in Istanbul on Tuesday.", GroupHint = (string?)null
        });
        rss[1].SeenAt.Should().Be(Now, "a date in the future is taken as now");
        rss[1].Snippet.Should().BeNull("a description that only repeats the headline adds nothing");

        // DW's feeds are RSS 1.0 (RDF), with the date in dc:date.
        const string rdf = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel><title>DW</title><link>https://www.dw.com</link><description>News</description></channel>
              <item><title>Flut in Istanbul</title><link>https://www.dw.com/de/flut/a-1</link>
                <description>Starkregen hat Istanbul überflutet.</description><dc:date>2026-10-20T08:30:00Z</dc:date></item>
            </rdf:RDF>
            """;
        var dw = OutletFeeds.Parse(new MemoryStream(Encoding.UTF8.GetBytes(rdf)), outlet with { Language = "German" }, Now).Should().ContainSingle().Subject;
        dw.Should().BeEquivalentTo(new { Title = "Flut in Istanbul", Url = "https://www.dw.com/de/flut/a-1", Snippet = "Starkregen hat Istanbul überflutet." });
        dw.SeenAt.Should().Be(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero));

        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
              <title type="html">Inondations &amp;amp; pluie</title>
              <link rel="self" href="https://example.org/self"/><link rel="alternate" href="https://example.org/inondations"/>
              <updated>2026-10-20T10:00:00+02:00</updated><summary>Des pluies torrentielles ont inondé Istanbul.</summary>
            </entry></feed>
            """;
        var entry = OutletFeeds.Parse(new MemoryStream(Encoding.UTF8.GetBytes(atom)), outlet, Now).Should().ContainSingle().Subject;
        entry.Should().BeEquivalentTo(new { Title = "Inondations & pluie", Url = "https://example.org/inondations", Snippet = "Des pluies torrentielles ont inondé Istanbul." });
        entry.SeenAt.Should().Be(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

        // The XML declaration picks the encoding, and a DOCTYPE is skipped rather than fetched.
        var latin1 = Encoding.Latin1.GetBytes("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <!DOCTYPE rss PUBLIC "-//Netscape Communications//DTD RSS 0.91//EN" "http://my.netscape.com/publish/formats/rss-0.91.dtd">
            <rss version="0.91"><channel><item><title>Crise em Brasília</title><link>https://example.com.br/a</link></item></channel></rss>
            """);
        OutletFeeds.Parse(new MemoryStream(latin1), outlet, Now).Single().Title.Should().Be("Crise em Brasília");

        var snippet = ArticleNormalizer.CleanSnippet(string.Join(' ', Enumerable.Repeat("word", 200)), "t", maxLength: 50)!;
        snippet.Should().EndWith("word…");
        snippet.Length.Should().BeLessThanOrEqualTo(51);
    }

    [Theory]
    [InlineData("Tue, 20 Oct 2026 14:49:57 GMT", "2026-10-20T14:49:57Z")]
    [InlineData("Wed, 20 Oct 2026 14:49:57 +0300", "2026-10-20T11:49:57Z")] // the wrong weekday, as some feeds write it
    [InlineData("20 Oct 2026 14:49 -0400", "2026-10-20T18:49:00Z")]
    [InlineData("Tue, 20 Oct 2026 14:49:57 EDT", "2026-10-20T18:49:57Z")]
    [InlineData("Tue, 20 Oct 2026 14:49:57 Z", "2026-10-20T14:49:57Z")]
    [InlineData("2026-10-20T14:49:57+02:00", "2026-10-20T12:49:57Z")]
    [InlineData("", null)]
    [InlineData("yesterday", null)]
    public void ParseDate_ShouldReadFeedDatesAsTheyAreWritten(string value, string? expected) =>
        OutletFeeds.ParseDate(value).Should().Be(expected is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Store_ShouldAddTheSnippetColumn_ToADatabaseFromBeforeOutletFeeds()
    {
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE articles (id TEXT PRIMARY KEY, url TEXT NOT NULL, title TEXT NOT NULL, outlet TEXT NOT NULL,
                    language TEXT NOT NULL, country TEXT NOT NULL, seen_at INTEGER NOT NULL, feed TEXT NOT NULL,
                    group_hint TEXT, embedding BLOB, event_id TEXT);
                INSERT INTO articles (id, url, title, outlet, language, country, seen_at, feed) VALUES ('old', 'u', 'Old', 'o', 'English', 'UK', 1, 'f');
                """;
            create.ExecuteNonQuery();
        }

        var store = new WorldNewsStore(_dbPath);
        store.AddArticles([Article("new", "New", "English", Now) with { Snippet = "What happened." }]);

        store.ArticlesToEmbed(10).Select(a => (a.Id, a.Snippet)).Should().Equal(("old", null), ("new", "What happened."));
        new WorldNewsStore(_dbPath).ArticlesToEmbed(10).Should().HaveCount(2, "opening it again doesn't add the column twice");
    }

    [Fact]
    public void Store_ShouldKeepSummaryVersions_AndSayWhenAnEventIsDueAnother()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "big", Reports("big", 4));
        SeedEvent(store, "small", Reports("small", 2));
        var since = Now.AddDays(-1);

        store.EventsDueSummary(since, 3, 10).Select(e => e.Id).Should().Equal(["big"], "only events with 3 or more outlets");
        store.SaveSummary("big", "Floods in Istanbul", "First version.", "llama3.2", 4, Now).Version.Should().Be(1);
        store.EventsDueSummary(since, 3, 10).Should().BeEmpty("nothing new since the summary");
        store.TopEvents(since, 10).Single(e => e.Id == "big").Headline.Should().Be("Floods in Istanbul");
        store.Event("big")!.Headline.Should().Be("Floods in Istanbul");
        store.Event("missing").Should().BeNull();

        store.SaveClustering(new ClusterResult([], [Event("big", "Floods", [1f, 0f], 6)], 0));
        store.EventsDueSummary(since, 3, 10).Should().BeEmpty("2 more reports isn't enough");
        store.SaveClustering(new ClusterResult([], [Event("big", "Floods", [1f, 0f], 7)], 0));
        store.EventsDueSummary(since, 3, 10).Select(e => e.Id).Should().Equal("big");

        var second = store.SaveSummary("big", null, "Second version.", "llama3.2", 7, Now.AddHours(1));
        second.Version.Should().Be(2);
        store.LatestSummary("big").Should().BeEquivalentTo(second);
        store.Event("big")!.Headline.Should().Be("Floods in Istanbul", "a version without a headline keeps the last one");

        var keep = store.ActiveEvents(since).Single(e => e.Id == "small");
        var drop = store.ActiveEvents(since).Single(e => e.Id == "big");
        keep.Absorb(drop);
        store.ApplyMerges([new EventMerge(keep, drop)]);
        store.LatestSummary("big").Should().BeNull("a merged-away event's summaries go with it");

        store.SaveSummary("small", "Small", "A version.", "llama3.2", 6, Now);
        store.Prune(Now);
        store.LatestSummary("small").Should().BeNull("pruned events take their summaries with them");
    }

    [Fact]
    public void Document_ShouldCollapseWireCopies_AndLetEveryLanguageIntoThePrompt()
    {
        var wire = "Heavy rain flooded streets and homes across Istanbul on Tuesday, officials said, as eight people died.";
        var spiegel = Article("de", "Flut in Istanbul", "German", Now.AddHours(-4)) with { Outlet = "Der Spiegel", Snippet = "Starkregen hat Istanbul überflutet." };
        var hindu = Article("hindu", "Floods kill eight in Istanbul", "English", Now.AddHours(-3)) with { Outlet = "The Hindu", Snippet = wire };
        var dawn = Article("dawn", "Eight dead as floods hit Istanbul", "English", Now.AddHours(-2)) with { Outlet = "Dawn", Snippet = wire };
        var nhk = Article("nhk", "イスタンブールで洪水", "Japanese", Now.AddHours(-1)) with { Outlet = "NHK" };
        var e = new EventOverview("e1", "Floods kill eight in Istanbul", 4, 4, ["Somewhere"], ["English", "German", "Japanese"],
            Now.AddHours(-4), Now.AddHours(-1), "Istanbul floods kill eight");
        string Format(DateTimeOffset at) => at.ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var sources = EventDocument.Sources([nhk, dawn, hindu, spiegel]);
        sources.Select(s => s.Report.Id).Should().Equal("de", "hindu", "nhk");
        sources[1].AlsoCarriedBy.Should().Equal("Dawn");

        var summary = new EventSummary("e1", 2, "Istanbul floods kill eight", "Eight people died.", "llama3.2", 4, Now);
        var md = EventDocument.ToMarkdown(e, [nhk, dawn, hindu, spiegel], summary, Format);
        md.Should().StartWith("# Istanbul floods kill eight").And.Contain("Original headline: Floods kill eight in Istanbul")
            .And.Contain("## Summary").And.Contain("_Written by llama3.2 from 4 reports, 20-10-26 15:00 (version 2)._")
            .And.Contain("### Der Spiegel: Somewhere, German, 20-10-26 11:00")
            .And.Contain("[Floods kill eight in Istanbul](https://example.com/hindu)").And.Contain("> " + wire)
            .And.Contain("Also carried by: Dawn").And.NotContain("Eight dead as floods hit Istanbul");
        md.IndexOf("Flut in Istanbul", StringComparison.Ordinal).Should().BeLessThan(md.IndexOf("イスタンブール", StringComparison.Ordinal));

        // Five English sources and room for three lines: English, German and Japanese each get one.
        var english = Enumerable.Range(1, 5).Select(i => Article($"en{i}", $"English report {i}", "English", Now.AddHours(-3).AddMinutes(i))
            with { Snippet = new string((char)('a' + i), 100) }).ToList();
        var prompt = EventDocument.ToPrompt(e, [.. english, spiegel, nhk], Format, maxChars: 560);
        prompt.Length.Should().BeLessThanOrEqualTo(560);
        prompt.Should().StartWith("Event: Floods kill eight in Istanbul").And.Contain("English report 1")
            .And.Contain("Flut in Istanbul").And.Contain("イスタンブールで洪水").And.NotContain("English report 5");
    }

    [Fact]
    public void Summarizer_ShouldReadTheModelsAnswer()
    {
        var text = "Heavy rain flooded Istanbul on Tuesday and eight people died, officials said.";
        EventSummarizer.Parse($"HEADLINE: Floods kill eight in Istanbul\nSUMMARY: {text}").Should().Be(("Floods kill eight in Istanbul", text));
        EventSummarizer.Parse($"**HEADLINE:** \"Floods kill eight\"\n\n**SUMMARY:**\n{text}").Should().Be(("Floods kill eight", text));
        EventSummarizer.Parse($"<think>Let me see.</think>{text}").Should().Be(((string?)null, text));
        EventSummarizer.Parse($"SUMMARY: {text}\nHEADLINE: Floods in Istanbul").Should().Be(("Floods in Istanbul", text));
        EventSummarizer.Parse("SUMMARY: Too short.").Should().BeNull();
    }

    [Fact]
    public async Task Summarizer_ShouldSummarizeTheBusiestEvents_AndStopWhenTheModelFails()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "big", Reports("big", 5));
        SeedEvent(store, "mid", Reports("mid", 3));
        SeedEvent(store, "small", Reports("small", 2));
        var prompts = new List<string>();
        Func<string, CancellationToken, Task<string>> answer = (_, _) =>
            Task.FromResult("HEADLINE: An English headline\nSUMMARY: What happened, where and when, in the model's own words.");
        var summarizer = new EventSummarizer(store, (system, prompt, ct) =>
        {
            system.Should().Be(EventSummarizer.SystemPrompt);
            prompts.Add(prompt);
            return answer(prompt, ct);
        }, "test-model", new ManualTime(Now), at => at.ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture), new SummaryOptions { Timeout = TimeSpan.FromMilliseconds(200) });

        var first = await summarizer.RunAsync(CancellationToken.None);
        first.Should().BeEquivalentTo(new { Written = 2, Notes = Array.Empty<string>() });
        prompts.Should().HaveCount(2);
        prompts[0].Should().Contain("Outlet big0").And.Contain("Outlet big4");
        store.LatestSummary("big").Should().BeEquivalentTo(new { Headline = "An English headline", Model = "test-model", ArticleCount = 5, Version = 1 });
        store.LatestSummary("small").Should().BeNull();

        (await summarizer.RunAsync(CancellationToken.None)).Written.Should().Be(0, "nothing has grown");
        prompts.Should().HaveCount(2);

        store.SaveClustering(new ClusterResult([], [Event("big", "big", [1f, 0f], 10), Event("mid", "mid", [1f, 0f], 10)], 0));
        answer = (_, _) => throw new HttpRequestException("connection refused");
        var down = await summarizer.RunAsync(CancellationToken.None);
        down.Written.Should().Be(0);
        down.Notes.Should().ContainSingle().Which.Should().Be("test-model couldn't write a summary: connection refused");
        prompts.Should().HaveCount(3, "after one failure the rest wait for the next read");

        answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; };
        (await summarizer.RunAsync(CancellationToken.None)).Notes.Should().ContainSingle().Which.Should().StartWith("test-model didn't write a summary within");
    }

    [Fact]
    public async Task Pipeline_ShouldReadOutletFeeds_AndNoteOnesThatFail()
    {
        var store = new WorldNewsStore(_dbPath);
        var working = new NewsOutlet("Example", "English", "Turkey", "https://www.example.com/rss");
        var broken = new NewsOutlet("Broken", "French", "France", "https://broken.example/rss");
        var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host switch
        {
            "www.example.com" => (HttpStatusCode.OK, OutletRss),
            "broken.example" => (HttpStatusCode.ServiceUnavailable, ""),
            _ => (HttpStatusCode.InternalServerError, "no model")
        }));
        var pipeline = new WorldNewsPipeline(store, http, new GdeltDocClient(http, new ManualTime(Now)),
            new OllamaClient(new Uri("http://localhost:11434/"), http), new ManualTime(Now),
            new WorldNewsOptions { Editions = [], Outlets = [working, broken] });

        var report = await pipeline.RunCycleAsync(CancellationToken.None);

        report.Added.Should().Be(2);
        report.Notes.Should().Contain(n => n.StartsWith("Broken (French):"));
        store.ArticlesToEmbed(10).Should().Contain(a => a.Feed == working.Feed && a.Snippet == "Heavy rain flooded streets & homes in Istanbul on Tuesday.");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
    }

    private static WorldArticle Article(string id, string title, string language, DateTimeOffset seen, string? hint = null) =>
        new(id, $"https://example.com/{id}", title, "Outlet " + id, language, "Somewhere", seen, "test", hint);

    [Fact]
    public void Store_ShouldSendEachSurgeAlertToEveryoneWhoAsked_AtMostOnceADay()
    {
        var store = new WorldNewsStore(_dbPath);
        store.WatchSurges("Grant", true, Now);
        store.WatchSurges("sam", true, Now);
        store.WatchSurges("sam", false, Now);
        store.IsWatchingSurges("grant").Should().BeTrue();
        store.IsWatchingSurges("sam").Should().BeFalse();

        var surging = new List<(string, string, string)> { ("crash", "Trains collide", "Surging: 8 outlets") };
        store.CreateSurgeAlerts(surging, Now).Should().ContainSingle().Which.Username.Should().Be("grant");
        store.CreateSurgeAlerts(surging, Now.AddHours(3)).Should().BeEmpty("one alert a day per story");
        store.CreateSurgeAlerts(surging, Now.AddHours(25)).Should().ContainSingle("a new surge the next day is news again");
        store.Alerts("grant").Should().HaveCount(2);
    }

    [Fact]
    public void Store_ShouldKeepKinds_AndOnlyFillGapsWhenAskedTo()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "rally", Reports("rally", 3), [1f, 0f]);
        SeedEvent(store, "results", Reports("results", 3), [1f, 0f]);
        var situation = new NewsSituation { Id = "election", Sum = [6f, 0f], LastSeen = Now };
        store.SaveLinks([new SituationLink("rally", situation), new SituationLink("results", situation)]);

        store.TopSituations(Now.AddDays(-1), 10).Single().Kind.Should().BeNull("not classified yet");
        store.SaveKinds([("election", SituationKinds.Politics)]);
        store.SaveKinds([("election", SituationKinds.Sport)], onlyMissing: true);
        store.TopSituations(Now.AddDays(-1), 10).Single().Kind.Should().Be(SituationKinds.Politics, "the keyword fallback doesn't overwrite");
    }

    private static WorldArticle[] Reports(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => Article($"{prefix}{i}", $"{prefix} report {i}", "English", Now.AddHours(-2))).ToArray();

    // One event holding the given reports, stored as clustering would leave it.
    private static void SeedEvent(WorldNewsStore store, string eventId, WorldArticle[] reports, float[]? direction = null)
    {
        store.AddArticles(reports);
        store.SaveClustering(new ClusterResult(reports.Select(r => (r.Id, eventId)).ToList(),
            [Event(eventId, reports[0].Title, direction ?? [1f, 0f], reports.Length)], 1));
    }

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
