using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Infrastructure.Services;
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
    public void GoogleNewsEditions_ShouldIncludeAdditionalVerifiedMarkets()
    {
        GoogleNewsFeed.Editions.Select(edition => edition.Ceid).Should().Contain(
        [
            "GH:en", "TZ:en", "UG:en", "BD:bn", "FI:fi", "IE:en", "CH:de", "CL:es-419", "PE:es-419", "MY:ms", "SG:en"
        ]);
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

        var merge = merges.Should().ContainSingle().Subject;
        (merge.Keep, merge.Drop).Should().Be((a, b));
        merge.Reason.Should().StartWith("similarity ");
        a.ArticleCount.Should().Be(5);
        events.Should().Equal(a, c);
    }

    [Fact]
    public void Clusterer_ShouldKeepStoriesFromDifferentCountriesApart_OnlyWithThePlaceGuard()
    {
        (WorldArticle, float[])[] reports =
        [
            (Article("es", "Floods in Spain kill 10", "English", Now.AddHours(-3)), new[] { 1f, 0.02f }),
            (Article("pt", "Floods in Portugal kill 4", "English", Now.AddHours(-2)), new[] { 1f, 0.03f }),
            (Article("es2", "Spain floods: death toll rises", "English", Now.AddHours(-1)), new[] { 1f, 0.01f }),
        ];
        var guarded = new List<NewsEvent>();
        var plain = new List<NewsEvent>();
        var ids = 0;

        EventClusterer.Assign(reports, guarded, [], EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"g{ids++}", new GroupingRules());
        EventClusterer.Assign(reports, plain, [], EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"p{ids++}");

        guarded.Select(e => e.ArticleCount).Should().Equal([2, 1], "both Spain reports, then Portugal on its own");
        plain.Should().ContainSingle("similarity alone can't tell the two floods apart");
    }

    [Fact]
    public void Clusterer_ShouldAskMoreOfAQuietEvent_BeforeItTakesANewReport()
    {
        var angle = Math.Sqrt(1 - 0.67 * 0.67);
        (WorldArticle, float[])[] reports =
        [
            (Article("old", "Bridge closed after crash", "English", Now.AddHours(-31)), new[] { 1f, 0f }),
            // 0.67 to the event: over the 0.65 threshold, under the 0.675 asked 30 hours on.
            (Article("new", "Bridge reopens", "English", Now.AddHours(-1)), new[] { 0.67f, (float)angle }),
        ];
        var guarded = new List<NewsEvent>();
        var plain = new List<NewsEvent>();
        var ids = 0;

        EventClusterer.Assign(reports, guarded, [], EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"g{ids++}", new GroupingRules());
        EventClusterer.Assign(reports, plain, [], EventClusterer.DefaultThreshold, EventClusterer.DefaultWindow, () => $"p{ids++}");

        guarded.Should().HaveCount(2);
        plain.Should().ContainSingle();
    }

    [Fact]
    public void MergeTwins_ShouldNotJoinEventsAboutDifferentCountries_WithThePlaceGuard()
    {
        var spain = Event("a", "Floods in Spain", new[] { 1f, 0.02f, 0f }, 3);
        var portugal = Event("b", "Portugal floods", new[] { 1f, 0.03f, 0f }, 2);
        var events = new List<NewsEvent> { spain, portugal };

        EventClusterer.MergeTwins(events, new HashSet<NewsEvent> { portugal }, EventClusterer.DefaultMergeThreshold, new GroupingRules())
            .Should().BeEmpty();
        events.Should().HaveCount(2);
    }

    [Fact]
    public void HeadlineNumbers_ShouldKeepDistinctiveNumbers_NotYearsOrLoneDigits()
    {
        HeadlineNumbers.In("Magnitude 7.1 quake kills 1,200 in 2026 as 3 aftershocks hit; 47 missing")
            .Should().BeEquivalentTo(["7.1", "1200", "47"]);
    }

    [Fact]
    public void MergeTwins_ShouldMergeJustBelowTheThreshold_OnlyWhenTheHeadlinesShareANumber()
    {
        var angle = (float)Math.Sqrt(1 - 0.835 * 0.835);
        List<NewsEvent> Pair(string titleA, string titleB) =>
            [Event("a", titleA, [1f, 0f], 3), Event("b", titleB, [0.835f, angle], 2)];

        var withNumber = Pair("Quake of 7.1 hits the coast", "7.1 tremor shakes coastal towns");
        var merge = EventClusterer.MergeTwins(withNumber, withNumber.ToHashSet(), EventClusterer.DefaultMergeThreshold, new GroupingRules())
            .Should().ContainSingle().Subject;
        merge.Reason.Should().Contain("both name 7.1");

        var without = Pair("Quake hits the coast", "Tremor shakes coastal towns");
        EventClusterer.MergeTwins(without, without.ToHashSet(), EventClusterer.DefaultMergeThreshold, new GroupingRules())
            .Should().BeEmpty("0.835 is under the 0.85 threshold without shared evidence");
    }

    [Fact]
    public void MergeTwins_ShouldNeverJoinAPairKeptApart_AndJoinAPairTheAiConfirmed()
    {
        var twinA = Event("a", "Bridge collapse", [1f, 0f, 0f], 3);
        var twinB = Event("b", "Bridge collapses", [0.99f, 0.14f, 0f], 2);
        var nearC = Event("c", "Rescue at the mine", [0f, 0f, 1f], 4);
        var nearD = Event("d", "Miners trapped underground", [0f, (float)Math.Sqrt(1 - 0.83 * 0.83), 0.83f], 2);
        var events = new List<NewsEvent> { twinA, twinB, nearC, nearD };

        var merges = EventClusterer.MergeTwins(events, events.ToHashSet(), EventClusterer.DefaultMergeThreshold, new GroupingRules(),
            keepApart: (x, y) => GroupingPairs.Key(x, y) == ("a", "b"), confirmed: new HashSet<(string, string)> { ("c", "d") });

        var merge = merges.Should().ContainSingle().Subject;
        (merge.Keep.Id, merge.Drop.Id).Should().Be(("c", "d"));
        merge.Reason.Should().StartWith("the AI judged them one story");
    }

    [Fact]
    public void CloseCalls_ShouldOfferPairsJustBelowTheThreshold_ClosestFirst_SkippingJudgedOnes()
    {
        NewsEvent Near(string id, double similarity) => Event(id, id, [(float)similarity, (float)Math.Sqrt(1 - similarity * similarity)], 2);
        var anchor = Event("anchor", "anchor", [1f, 0f], 3);
        List<NewsEvent> events = [anchor, Near("close", 0.84), Near("closer", 0.845), Near("far", 0.6), Near("judged", 0.83)];

        var calls = EventClusterer.CloseCalls(events, 0.80, 0.85, 10, skip: (a, b) => a == "judged" || b == "judged");

        calls.Where(c => c.A.Id == "anchor" || c.B.Id == "anchor").Select(c => c.A.Id == "anchor" ? c.B.Id : c.A.Id)
            .Should().Equal("closer", "close");
    }

    [Fact]
    public void PullPlanner_ShouldTakeEachKindInTurn_WithoutRepeats_UpToTheLimit()
    {
        var plan = PullPlanner.Plan(
            ["Kenya election results announced", "Floods swamp Valencia region"],
            ["Floods swamp Valencia region", "Bridge collapse Lisbon harbour"],
            ["semiconductor export rules"],
            max: 4);

        plan.Select(q => q.Why).Should().Equal(PullPlanner.Thin, PullPlanner.Surging, PullPlanner.Watch, PullPlanner.Thin);
        plan.Select(q => q.Query).Should().OnlyHaveUniqueItems();
        plan[0].Query.Should().Be("Floods swamp Valencia region");
    }

    [Theory]
    [InlineData("Sure. {\"same\": true, \"reason\": \"Both report the 7.1 quake.\"}", true, "Both report the 7.1 quake.")]
    [InlineData("{\"same\": \"false\", \"reason\": \"Different floods\"}", false, "Different floods")]
    [InlineData("Yes, the same rescue.", true, "the same rescue")]
    [InlineData("I can't tell.", null, "")]
    public void GroupingAdjudicator_ShouldReadTheVerdict_FromJsonOrPlainWords(string reply, bool? same, string reason)
    {
        GroupingAdjudicator.Parse(reply).Should().Be((same, reason));
    }

    [Fact]
    public void SplitSuggestions_ShouldFlagAMinorityNamingOtherCountries_OnlyWhenTheyAreFew()
    {
        WorldArticle[] story =
        [
            Article("s1", "Floods in Spain kill 10", "English", Now),
            Article("s2", "Spain floods: toll rises", "English", Now),
            Article("s3", "Spanish towns cut off by floods", "English", Now),
            Article("p1", "Portugal braces for storm", "English", Now),
        ];

        var suggestion = SplitSuggestions.For(story).Should().ContainSingle().Subject;
        suggestion.ArticleIds.Should().Equal("p1");
        suggestion.Reason.Should().Contain("Portugal");

        SplitSuggestions.For([story[0], story[1], story[3], Article("p2", "Portugal storm warning", "English", Now)])
            .Should().BeEmpty("half the reports isn't an outlier");
    }

    // 19-11-26: checklist suggestions show a short label and an Open link instead of a printed address.
    [Fact]
    public void DeskChecklist_ShouldShortenSuggestions_AndFindTheirLink()
    {
        var suggestion = "First report: Anadolu Ajansı, https://news.google.com/rss/articles/CBMi6gNBVV95cUxOZkRib3ZHeHRy" + new string('x', 400) + "?oc=5";

        DeskChecklist.ShortLabel(suggestion).Should().Be("First report: Anadolu Ajansı (news.google.com)");
        DeskChecklist.LinkIn(suggestion).Should().StartWith("https://news.google.com/rss/articles/").And.EndWith("?oc=5");
        DeskChecklist.LinkIn("19 independent sources: BERITA Mediacorp, CNN Arabic").Should().BeNull();
        DeskChecklist.LinkIn("javascript:alert(1)").Should().BeNull();
        DeskChecklist.ShortLabel(new string('a', 200)).Should().HaveLength(90).And.EndWith("…");
    }

    // 19-11-26: SentinelGPT tags stories the keywords couldn't, from the kind's own list only.
    [Fact]
    public void SubtypeRefinement_ShouldAcceptOnlyTheKindsIds_AndBeSavedPerStory()
    {
        SubtypeRefinement.Parse(SituationKinds.Disaster, " Flood.").Should().Be("flood");
        SubtypeRefinement.Parse(SituationKinds.Disaster, "none").Should().Be(SubtypeRefinement.None);
        SubtypeRefinement.Parse(SituationKinds.Disaster, "rail").Should().BeNull("rail is a transport subtype");
        SubtypeRefinement.Parse(SituationKinds.Disaster, "It is probably a flood").Should().BeNull();
        SubtypeRefinement.Prompt(SituationKinds.Disaster, ["Dam bursts upstream of town"]).Should().Contain("flood: Flood").And.Contain("none: none of these");

        SituationSubtypes.FromModel(SituationKinds.Disaster, "flood")!.Confidence.Should().Be(SituationTag.Model);
        SituationSubtypes.FromModel(SituationKinds.Disaster, SubtypeRefinement.None).Should().BeNull();

        var store = new WorldNewsStore(_dbPath);
        store.ModelSubtype("s1").Should().BeNull();
        store.SaveModelSubtype("s1", "flood", Now);
        store.ModelSubtype("s1").Should().Be("flood");
    }

    // 19-11-26: how late each source is seen after publishing, from dated reports only.
    [Fact]
    public void PublicationDelays_ShouldMeasureDatedReportsOnly()
    {
        var store = new WorldNewsStore(_dbPath);
        var reports = Enumerable.Range(0, 6).Select(i => Article($"late{i}", $"Late report {i}", "English", Now.AddMinutes(-30 - i))
            with { Outlet = "Slow Times", FetchedAt = Now })
            .Concat(Enumerable.Range(0, 6).Select(i => Article($"undated{i}", $"Undated report {i}", "English", Now) with { Outlet = "No Dates" }))
            .ToList();
        store.AddArticles(reports);

        var delay = store.PublicationDelays(Now.AddDays(-7)).Should().ContainSingle().Subject;
        delay.Outlet.Should().Be("Slow Times");
        delay.Reports.Should().Be(6);
        delay.Median.TotalMinutes.Should().BeInRange(30, 36);
    }

    // 19-11-26: the hourly re-check splits stories merged before the place guard caught them, but
    // leaves a story a person has corrected alone.
    [Fact]
    public void RecentMergeCheck_ShouldSplitAWronglyMergedStory_ButNotOneAPersonCorrected()
    {
        var store = new WorldNewsStore(_dbPath);
        WorldArticle[] Story(string prefix) =>
        [
            Article(prefix + "s1", "Floods in Spain kill 10", "English", Now),
            Article(prefix + "s2", "Spain floods: toll rises", "English", Now),
            Article(prefix + "s3", "Spanish towns cut off by floods", "English", Now),
            Article(prefix + "p1", "Portugal braces for storm", "English", Now),
        ];
        SeedEvent(store, "merged", Story("a"), [1f, 0f]);
        SeedEvent(store, "kept", Story("b"), [0f, 1f]);
        store.SplitReports("kept", ["bs3"], null, "grant", Now).Should().NotBeNull("a person corrects this story first");

        RecentMergeCheck.Run(store, Now).Should().Be(1);

        store.ArticlesForEvent("merged").Select(a => a.Id).Should().NotContain("ap1");
        store.GroupingHistory("merged").Should().Contain(h => h.Action == "split" && h.Actor == RecentMergeCheck.Actor);
        store.ArticlesForEvent("kept").Select(a => a.Id).Should().Contain("bp1", "a person's decision on a story stands");
        RecentMergeCheck.Run(store, Now).Should().Be(0, "nothing is left to split");
    }

    [Fact]
    public void Store_ShouldKeepASplitApart_ThroughRegroupAndLaterMerges_AndLogEachStep()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "quake", Reports("quake", 4), [1f, 0f]);
        SeedEvent(store, "big", Reports("big", 6), [0f, 1f]);
        var pipeline = Pipeline(store);

        var split = store.SplitReports("quake", ["quake3"], null, "grant", Now);

        split.Should().NotBeNull();
        store.ArticlesForEvent(split!).Select(a => a.Id).Should().Equal("quake3");
        store.Event("quake")!.ArticleCount.Should().Be(3);
        pipeline.Regroup().Merges.Should().BeEmpty("the split story reads exactly like its source, but a person kept them apart");
        store.MoveReports("quake", "big", ["quake0", "quake1", "quake2"], null, "grant", Now).Should().BeFalse("that would empty the story");

        store.MergeStories(split, "big", "grant", Now).Should().Be("big", "the larger story keeps its id");
        store.ApartPairs().Should().Contain(GroupingPairs.Key("quake", "big"), "the verdict follows the story it was merged into");
        store.GroupingHistory("quake").Select(h => h.Action).Should().Contain("split");
        store.GroupingHistory("big").Select(h => h.Action).Should().Contain(["split", "merged"]);
    }

    [Fact]
    public async Task Pipeline_ShouldSkipTheSlowSourceGap_AndRunExtraSearches_WhenAPersonPulls()
    {
        var store = new WorldNewsStore(_dbPath);
        var handler = new FakeHttp(request => request.RequestUri!.Host == "news.google.com"
            ? (HttpStatusCode.OK, FranceFeed)
            : (HttpStatusCode.InternalServerError, "model not loaded"));
        var http = new HttpClient(handler);
        var time = new ManualTime(Now);
        var options = new WorldNewsOptions { Editions = [France], Outlets = [], GdeltQueries = [], SlowSourceGap = TimeSpan.FromMinutes(15) };
        var pipeline = new WorldNewsPipeline(store, http, new GdeltDocClient(http, time),
            new OllamaClient(new Uri("http://localhost:11434/"), http), time, options);

        await pipeline.RunCycleAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        await pipeline.RunCycleAsync(new ReadRequest(Force: true, Queries: ["kenya election results"]), CancellationToken.None);

        handler.Urls.Count(url => url.StartsWith("https://news.google.com/", StringComparison.Ordinal)).Should().Be(2);
        handler.Urls.Should().Contain(url => url.Contains("kenya", StringComparison.OrdinalIgnoreCase));
    }

    private WorldNewsPipeline Pipeline(WorldNewsStore store)
    {
        var http = new HttpClient(new FakeHttp(_ => (HttpStatusCode.InternalServerError, "offline")));
        var time = new ManualTime(Now);
        return new WorldNewsPipeline(store, http, new GdeltDocClient(http, time), new OllamaClient(new Uri("http://localhost:11434/"), http), time,
            new WorldNewsOptions { Editions = [], Outlets = [], GdeltQueries = [] });
    }

    [Fact]
    public async Task Pipeline_ShouldReadGoogleNewsEditions_AtMostOncePerSlowSourceGap()
    {
        var store = new WorldNewsStore(_dbPath);
        var handler = new FakeHttp(request => request.RequestUri!.Host == "news.google.com"
            ? (HttpStatusCode.OK, FranceFeed)
            : (HttpStatusCode.InternalServerError, "model not loaded"));
        var http = new HttpClient(handler);
        var time = new ManualTime(Now);
        var options = new WorldNewsOptions { Editions = [France], Outlets = [], SlowSourceGap = TimeSpan.FromMinutes(15) };
        var pipeline = new WorldNewsPipeline(store, http, new GdeltDocClient(http, time),
            new OllamaClient(new Uri("http://localhost:11434/"), http), time, options);
        int GoogleReads() => handler.Urls.Count(url => url.StartsWith("https://news.google.com/", StringComparison.Ordinal));

        await pipeline.RunCycleAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        await pipeline.RunCycleAsync(CancellationToken.None);
        GoogleReads().Should().Be(1, "five minutes on, the edition isn't due");

        time.Advance(TimeSpan.FromMinutes(10));
        await pipeline.RunCycleAsync(CancellationToken.None);
        GoogleReads().Should().Be(2);
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
    public void Store_ShouldGiveAnOldDatabaseTheCoveringIndex_InPlaceOfTheEventIdOne()
    {
        // From before 30-10-26: reports indexed by event id alone.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE articles (
                    id TEXT PRIMARY KEY, url TEXT NOT NULL, title TEXT NOT NULL, outlet TEXT NOT NULL,
                    language TEXT NOT NULL, country TEXT NOT NULL, seen_at INTEGER NOT NULL, feed TEXT NOT NULL,
                    group_hint TEXT, embedding BLOB, event_id TEXT);
                CREATE INDEX ix_articles_event ON articles (event_id);
                """;
            command.ExecuteNonQuery();
        }

        var store = new WorldNewsStore(_dbPath);

        ArticleIndexes().Should().Contain("ix_articles_event_cover").And.NotContain("ix_articles_event", "a small table gets it as it opens");
        store.EnsureCoveringIndex().Should().BeFalse("it's already there, so a later start does nothing");
        new WorldNewsStore(_dbPath);
        ArticleIndexes().Should().NotContain("ix_articles_event", "opening it again doesn't bring the old one back");
        SeedEvent(store, "flood", Reports("flood", 3));
        store.TopEvents(Now.AddDays(-1), 5).Should().ContainSingle().Which.OutletCount.Should().Be(3);
    }

    [Fact]
    public void Store_ShouldDropAReportsEmbeddingOnceItsGrouped_WithoutAskingForItAgain()
    {
        var store = new WorldNewsStore(_dbPath);
        var reports = Reports("flood", 3);
        store.AddArticles(reports);
        store.SaveEmbeddings(reports.Select(r => (r.Id, new[] { 1f, 0f })).ToList());
        store.EmbeddedUnassigned().Should().HaveCount(3);

        store.SaveClustering(new ClusterResult(reports.Select(r => (r.Id, "flood")).ToList(), [Event("flood", reports[0].Title, [1f, 0f], 3)], 1));

        store.EmbeddedUnassigned().Should().BeEmpty();
        store.RecentArticlesToEmbed(10, Now.AddDays(-1)).Should().BeEmpty("an emptied embedding isn't one still to make");
        EmbeddingBytes().Should().Be(0, "the event's own vector carries the meaning now");
    }

    [Fact]
    public void Store_ShouldDropEmbeddingsGroupedBeforeThat_AndCompactTheFile()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "big", Reports("big", 300));
        Execute("UPDATE articles SET embedding = randomblob(3072)"); // as reports were kept before 30-10-26
        var waits = 0;

        store.ReleaseGroupedEmbeddings(batchSize: 50, betweenBatches: () => waits++).Should().Be(300);

        waits.Should().BeGreaterThan(1, "it goes in batches so the reader's writes aren't held up");
        EmbeddingBytes().Should().Be(0);
        store.ReleaseGroupedEmbeddings().Should().Be(0, "a later start has nothing to do");
        var before = DiskBytes();
        store.Compact();
        DiskBytes().Should().BeLessThan(before / 2, "the emptied rows are packed together again");
        store.TopEvents(Now.AddDays(-1), 5).Should().ContainSingle().Which.ArticleCount.Should().Be(300);
    }

    // The file and its write-ahead log, where recent writes sit until a checkpoint.
    private long DiskBytes() =>
        new FileInfo(_dbPath).Length + (File.Exists(_dbPath + "-wal") ? new FileInfo(_dbPath + "-wal").Length : 0);

    private long EmbeddingBytes()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(length(embedding)), 0) FROM articles";
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<string> ArticleIndexes()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'articles'";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
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
        store.Watch("grant", "second-story", Now.AddHours(3));
        store.Watch("someone-else", "third-story", Now.AddHours(4));
        store.WatchedSituationIds("GRANT").Should().Equal(["second-story", situation.Id], "the Watching list is per person, newest watch first");
        store.Unwatch("grant", situation.Id);
        store.IsWatching("grant", situation.Id).Should().BeFalse();
        store.WatchedSituationIds("grant").Should().Equal(["second-story"]);
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
    public void RecentReports_ShouldIncludeUngroupedReports_WithinTheWindowAndLimit()
    {
        var store = new WorldNewsStore(_dbPath);
        var grouped = Reports("grouped", 3);
        SeedEvent(store, "grouped", grouped);
        store.AddArticles([
            Article("ungrouped", "Election in Ghana", "English", Now.AddMinutes(1)),
            Article("old", "Older report", "English", Now.AddDays(-2))
        ]);

        var reports = store.RecentReports(Now.AddDays(-1), 2);

        reports.Should().HaveCount(2);
        reports[0].Id.Should().Be("ungrouped");
        reports[1].Id.Should().BeOneOf(grouped.Select(article => article.Id).ToArray());
        store.RecentReports(Now.AddDays(-1), 0).Should().BeEmpty();
    }

    [Fact]
    public void RecentReports_ShouldFilterByTextSourceAndSituationKind()
    {
        var store = new WorldNewsStore(_dbPath);
        var election = Article("election", "Election results announced", "English", Now)
            with { Country = "Ghana", Outlet = "Daily News" };
        SeedEvent(store, "election-event", [election]);
        store.SaveLinks([new SituationLink("election-event", new NewsSituation { Id = "election-situation", Sum = [1f, 0f], LastSeen = Now })]);
        store.SaveKinds([("election-situation", SituationKinds.Politics)]);
        store.AddArticles([Article("flood", "Flood warning issued", "French", Now.AddMinutes(-1)) with { Country = "France" }]);

        var reports = store.RecentReports(Now.AddDays(-1), 10, text: "election", country: "ghan",
            language: "english", outlet: "daily", kind: SituationKinds.Politics);

        reports.Should().ContainSingle().Which.Id.Should().Be("election");
        store.RecentReports(Now.AddDays(-1), 10, kind: "unclassified").Select(report => report.Id).Should().ContainSingle().Which.Should().Be("flood");
        store.RecentReports(Now.AddDays(-1), 1, offset: 1).Should().ContainSingle();
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
            .And.Contain("4 outlets · 4 reports · 3 languages · 1 country · first seen 20-10-26 11:00, latest 20-10-26 14:00")
            .And.Contain("## The story\n\n_SentinelGPT hasn't analysed this coverage yet.")
            .And.Contain("Eight people died.\n\n_A short summary from the headlines alone._")
            .And.Contain("_The summary was written by AI (llama3.2) from the headlines and descriptions of 4 reports, 20-10-26 15:00 (version 2).")
            .And.Contain("### Der Spiegel: Somewhere, German, 1 report")
            .And.Contain("[Floods kill eight in Istanbul](https://example.com/hindu) · 20-10-26 12:00\n\n> " + wire)
            .And.Contain("Also carried by: Dawn").And.NotContain("Eight dead as floods hit Istanbul");
        // The wire copy also sits under the outlet that ran it second, saying who published it first.
        md.Should().Contain("### Dawn: Somewhere, English, 1 report\n\n[Floods kill eight in Istanbul](https://example.com/hindu) · 20-10-26 12:00 · first published by The Hindu");
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
        EventSummarizer.ParseComparison($"HEADLINE: x\nSUMMARY: {text}").Should().BeEmpty("an answer without a comparison has none");
    }

    [Fact]
    public void Summarizer_ShouldReadTheComparison_AndKeepItOutOfTheSummary()
    {
        var text = "Heavy rain flooded Istanbul on Tuesday and eight people died, officials said.";
        var answer = $"""
            **HEADLINE:** Floods kill eight in Istanbul
            **SUMMARY:** {text}
            **COMPARISON:**
            - Turkish outlets lead with the rescue effort, while European ones stress the climate.
            * Dawn and The Hindu give eight dead; Der Spiegel gives seven.
            2. Only NHK reports the airport closure.
            - ok
            """;

        EventSummarizer.Parse(answer).Should().Be(("Floods kill eight in Istanbul", text));
        EventSummarizer.ParseComparison(answer).Should().Equal(
            "Turkish outlets lead with the rescue effort, while European ones stress the climate.",
            "Dawn and The Hindu give eight dead; Der Spiegel gives seven.",
            "Only NHK reports the airport closure.");
        EventSummarizer.ParseComparison($"<think>COMPARISON: hidden thoughts here</think>COMPARISON: All outlets tell the same story.\nSUMMARY: {text}")
            .Should().Equal("All outlets tell the same story.");
    }

    [Fact]
    public void Store_ShouldKeepTheComparison_AndAskOnceMoreForSummariesFromBeforeIt()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "old", Reports("old", 4));
        SeedEvent(store, "new", Reports("new", 4));
        SeedEvent(store, "none", Reports("none", 4));
        store.SaveSummary("new", "New", "A summary with a comparison.", "gemma4", 4, Now, ["Outlets agree.", "Only one names the town."])
            .Comparison.Should().Equal("Outlets agree.", "Only one names the town.");
        store.SaveSummary("none", "None", "A summary the model gave no comparison with.", "gemma4", 4, Now);
        store.SaveSummary("old", "Old", "A summary from before comparisons.", "gemma4", 4, Now);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE summaries SET comparison = NULL WHERE event_id = 'old'";
            command.ExecuteNonQuery();
        }

        store.LatestSummary("new")!.Comparison.Should().Equal("Outlets agree.", "Only one names the town.");
        store.LatestSummary("none")!.Comparison.Should().BeEmpty();
        store.LatestSummary("old")!.Comparison.Should().BeNull();
        store.EventsDueSummary(Now.AddDays(-1), 3, 10).Select(e => e.Id).Should().Equal(["old"],
            "only the summary from before comparisons is asked again; the others haven't grown");
    }

    [Fact]
    // Grant's six (06-11-26): the claim, what the outlets report, who they are, what each leaves out,
    // what they agree on, and every outlet's links. Since 07-11-26 in three parts: the story,
    // agreement (agreed facts, figures, where they differ), then coverage (each outlet once, with its reports).
    public void Document_ShouldGiveTheStory_ThenAgreement_ThenEachOutletWithItsGapsAndReports()
    {
        var bbc = Article("bbc", "Istanbul floods leave 64 dead", "English", Now.AddHours(-4)) with { Outlet = "BBC", Country = "United Kingdom" };
        var reuters = Article("reuters", "Istanbul floods: 64 dead", "English", Now.AddHours(-3)) with { Outlet = "Reuters", Country = "United Kingdom" };
        var nhk = Article("nhk", "イスタンブールで洪水", "Japanese", Now.AddHours(-1)) with { Outlet = "NHK", Country = "Japan" };
        var e = new EventOverview("e1", "Istanbul floods leave 64 dead", 3, 3, ["Japan", "United Kingdom"], ["English", "Japanese"],
            Now.AddHours(-4), Now.AddHours(-1), "Istanbul floods kill 64");
        var summary = new EventSummary("e1", 1, "Istanbul floods kill 64", "Floods in Istanbul killed 64 people.", "gemma4:latest on the Mac", 3, Now,
            ["British outlets lead with the toll; NHK with the flooding itself."]);
        var analysis = new EventAnalysis("e1", "Heavy rain caused deadly floods in Istanbul; the toll of 64 is reported as confirmed by officials.",
            "Floods hit Istanbul on Tuesday. BBC and Reuters put the dead at 64, citing officials. NHK reports the flooding.",
            [new AgreedFact("Flooding hit Istanbul on Tuesday.", ["BBC", "Reuters", "NHK"]), new AgreedFact("64 people died.", ["BBC", "Reuters"])],
            [new OutletDifference("NHK", "shows the flooded metro."), new OutletDifference("", "No outlet names the districts worst hit.")],
            "SentinelGPT on the Mac", 3, 2, Now);
        string Format(DateTimeOffset at) => at.ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var md = EventDocument.ToMarkdown(e, [bbc, reuters, nhk], summary, Format, analysis);

        md.Should().Contain("## The story\n\n**The claim:** Heavy rain caused deadly floods in Istanbul")
            .And.Contain("Floods hit Istanbul on Tuesday. BBC and Reuters put the dead at 64")
            .And.Contain("## Agreement\n\n### What they agree on\n\n1. Flooding hit Istanbul on Tuesday. (3 outlets: BBC, Reuters, NHK)\n2. 64 people died. (2 outlets: BBC, Reuters)")
            .And.MatchRegex(@"### The figures\n\n_Read from each headline[^\n]*\n\n- [^\n]*64 \(BBC, Reuters\)")
            .And.Contain("### Where they differ\n\n- **NHK** adds: shows the flooded metro.\n- No outlet names the districts worst hit.")
            .And.Contain("written by SentinelGPT (SentinelGPT on the Mac), 20-10-26 15:00, from the headlines and descriptions of 3 reports and 2 articles read in full")
            .And.Contain("## Coverage\n\n3 outlets in 2 languages (English, Japanese), from 2 countries (Japan, United Kingdom). First report: BBC, 20-10-26 11:00. Latest: NHK, 20-10-26 14:00.")
            .And.Contain("| Outlet | 1 | 2 |\n| --- | :-: | :-: |\n| BBC | ● | ● |\n| Reuters | ● | ● |\n| NHK | ● | – |")
            .And.Contain("### NHK: Japan, Japanese, 1 report\n\nLeaves out: 64 people died. Adds: shows the flooded metro.")
            .And.Contain("[Istanbul floods: 64 dead](https://example.com/reuters) · 20-10-26 12:00");
        var order = new[] { "## The story", "## Agreement", "### What they agree on", "### Where they differ", "## Coverage", "### BBC:" }
            .Select(heading => md.IndexOf(heading, StringComparison.Ordinal)).ToList();
        order.Should().BeInAscendingOrder().And.NotContain(-1);
        md.Should().NotContain("British outlets lead", "SentinelGPT's comparison replaces the summary's older one");

        var bare = EventDocument.ToMarkdown(e, [bbc], null, Format);
        bare.Should().Contain("## The story\n\n_SentinelGPT hasn't analysed").And.Contain("_No summary yet._")
            .And.Contain("### Where they differ\n\n_SentinelGPT hasn't analysed").And.NotContain("written by");
    }

    [Fact]
    public void Story_ShouldSayWhatChangedSinceTheLastRead_AndCarryEnglishHeadlines()
    {
        var bbc = Article("bbc", "Istanbul floods leave 64 dead", "English", Now.AddHours(-4)) with { Outlet = "BBC", Country = "United Kingdom" };
        var nhk = Article("nhk", "イスタンブールで洪水", "Japanese", Now.AddHours(-1)) with { Outlet = "NHK", Country = "Japan" };
        var reuters = Article("reuters", "Istanbul floods: 70 dead", "English", Now.AddMinutes(-30)) with { Outlet = "Reuters", Country = "United Kingdom" };
        var e = new EventOverview("e1", "Istanbul floods", 3, 3, ["Japan", "United Kingdom"], ["English", "Japanese"], Now.AddHours(-4), Now, "Istanbul floods");
        var before = new EventAnalysis("e1", "Floods killed 64 in Istanbul.", "s", [new AgreedFact("Flooding hit Istanbul on Tuesday.", ["BBC"])], [], "m", 2, 0, Now.AddHours(-2));
        var now = before with
        {
            Claim = "A dam breach caused the deadly floods, officials now say.", CreatedAt = Now,
            Agreed = [new AgreedFact("Flooding hit Istanbul on Tuesday.", ["BBC", "NHK"]), new AgreedFact("Officials blame a dam breach.", ["BBC", "Reuters"])]
        };

        var changes = StoryChangesFinder.Find(now, before, Now.AddHours(-2), [bbc, nhk, reuters]);

        changes.Should().NotBeNull();
        changes!.NewOutlets.Should().Equal("NHK", "Reuters");
        changes.NewFacts.Should().Equal("Officials blame a dam breach.");
        changes.DroppedFacts.Should().BeEmpty("the same fact, reworded or not, isn't new");
        changes.PreviousClaim.Should().Be("Floods killed 64 in Istanbul.");
        changes.FigureChanges.Should().ContainSingle().Which.Should().Be("Deaths: 70; 64 (new)", "one report wasn't enough to make it a figure before");
        StoryChangesFinder.Find(now, now, Now, [bbc]).Should().BeNull("nothing changed since the same analysis");

        var story = EventDocument.Build(e, [bbc, nhk, reuters], null, now, new Dictionary<string, string> { ["nhk"] = "Floods in Istanbul" },
            new HashSet<string> { "bbc" }, changes);
        story.Outlet("BBC")!.ReadInFull.Should().BeTrue();
        story.Outlet("NHK")!.Region.Should().Be(WorldRegions.Asia);
        story.Grid.Select(row => row.Outlet).Should().Equal("BBC", "NHK", "Reuters");
        var md = EventDocument.ToMarkdown(story, at => at.ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        md.Should().Contain("## What changed since you last read (20-10-26 13:00)\n\n- 2 new outlets: NHK, Reuters")
            .And.Contain("- The claim changed. It was: Floods killed 64 in Istanbul.")
            .And.Contain("- Now agreed: Officials blame a dam breach.")
            .And.Contain($"[Floods in Istanbul]({nhk.Url})")
            .And.Contain("Original headline (Japanese): イスタンブールで洪水");
        md.IndexOf("## What changed", StringComparison.Ordinal).Should().BeLessThan(md.IndexOf("## The story", StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_ShouldGiveEachOutletOnce_WithItsPlaceInTheComparison_AndItsReports()
    {
        var wire = "Heavy rain flooded streets and homes across Istanbul on Tuesday, officials said, as eight people died.";
        var bbc = Article("bbc", "Istanbul floods leave 64 dead", "English", Now.AddHours(-4)) with { Outlet = "BBC", Country = "United Kingdom", Snippet = wire };
        var bbc2 = Article("bbc2", "Istanbul: the clean-up begins", "English", Now.AddHours(-1)) with { Outlet = "BBC", Country = "United Kingdom" };
        var dawn = Article("dawn", "Eight dead as floods hit Istanbul", "English", Now.AddHours(-2)) with { Outlet = "Dawn", Country = "Pakistan", Snippet = wire };
        var nhk = Article("nhk", "イスタンブールで洪水", "Japanese", Now.AddHours(-3)) with { Outlet = "NHK", Country = "Japan" };
        var e = new EventOverview("e1", "Istanbul floods", 4, 3, ["Japan", "Pakistan", "United Kingdom"], ["English", "Japanese"], Now.AddHours(-4), Now.AddHours(-1));
        var analysis = new EventAnalysis("e1", "Floods hit Istanbul.", "s",
            [new AgreedFact("Flooding hit Istanbul on Tuesday.", ["BBC", "nhk"]), new AgreedFact("Officials gave the toll.", []), new AgreedFact("64 people died.", ["BBC"])],
            [new OutletDifference("NHK", "shows the flooded metro."), new OutletDifference("Spiegel Online", "blames a dam.")], "m", 4, 1, Now);
        var story = EventDocument.Build(e, [bbc, bbc2, dawn, nhk], null, analysis, readInFull: new HashSet<string> { "nhk" });

        StoryCoverage.GridNumbers(story).Should().Equal([1, 3], "the second fact names no outlets, so it isn't a column");
        var coverage = StoryCoverage.ByOutlet(story);
        coverage.Select(c => c.Outlet.Name).Should().Equal(["BBC", "Dawn", "NHK"], "most reports first, then by name");

        var bbcCoverage = coverage[0];
        bbcCoverage.States.Should().Equal(true, true);
        bbcCoverage.Reports.Select(r => r.Report.Id).Should().Equal(["bbc", "bbc2"], "oldest first");
        bbcCoverage.Reports[0].AlsoCarriedBy.Should().Equal("Dawn");

        var dawnCoverage = coverage[1];
        dawnCoverage.States.Should().BeNull("the comparison never named Dawn: it's shown as not compared, not as stating nothing");
        dawnCoverage.Reports.Should().ContainSingle().Which.Should().Match<OutletReport>(r => r.Report.Id == "bbc" && r.FirstPublishedBy == "BBC",
            "Dawn ran BBC's wire copy");

        var nhkCoverage = coverage[2];
        nhkCoverage.States.Should().Equal(new[] { true, false }, "names match whatever their case");
        nhkCoverage.LeavesOut.Should().BeEmpty("one outlet behind a fact isn't enough to say others leave it out");
        nhkCoverage.Adds.Should().Be("shows the flooded metro.");
        nhkCoverage.Outlet.ReadInFull.Should().BeTrue();

        StoryCoverage.Unmatched(story).Select(c => c.Outlet).Should().Equal(["Spiegel Online"], "what SentinelGPT said about it isn't dropped");
        var spread = StoryCoverage.Spread(story, 8);
        spread.Should().HaveCount(8).And.BeInAscendingOrder();
        spread[0].Should().Be(1, "BBC was first");
        spread[^1].Should().Be(3, "every outlet by the latest report");
    }

    [Fact]
    public void StoryFilter_ShouldOfferTheRegionsAndLanguagesPresent_AndKeepOnlyTheirOutlets()
    {
        var bbc = Article("bbc", "Istanbul floods leave 64 dead", "English", Now.AddHours(-4)) with { Outlet = "BBC", Country = "United Kingdom" };
        var nhk = Article("nhk", "イスタンブールで洪水", "Japanese", Now.AddHours(-1)) with { Outlet = "NHK", Country = "Japan" };
        var dawn = Article("dawn", "Floods in Istanbul", "English", Now.AddHours(-2)) with { Outlet = "Dawn", Country = "Pakistan" };
        var e = new EventOverview("e1", "Istanbul floods", 3, 3, ["Japan", "Pakistan", "United Kingdom"], ["English", "Japanese"], Now.AddHours(-4), Now);
        var analysis = new EventAnalysis("e1", "Floods hit Istanbul.", "s",
            [new AgreedFact("Flooding hit Istanbul on Tuesday.", ["BBC", "NHK", "Dawn"]), new AgreedFact("64 people died.", ["BBC", "Dawn"])],
            [new OutletDifference("Spiegel Online", "Adds a dam breach.")], "m", 3, 0, Now);
        var story = EventDocument.Build(e, [bbc, nhk, dawn], null, analysis);

        StoryFilter.Regions(story).Should().Equal(
            (StoryFilter.Western, 1), (StoryFilter.NonWestern, 2), (WorldRegions.Europe, 1), (WorldRegions.Asia, 2));
        StoryFilter.Languages(story).Should().Equal(("English", 2), ("Japanese", 1));

        var nonWestern = StoryFilter.Everything.WithRegion(StoryFilter.NonWestern);
        story.Outlets.Where(nonWestern.Matches).Select(outlet => outlet.Name).Should().BeEquivalentTo("NHK", "Dawn");
        story.Grid.Where(row => nonWestern.Matches(story, row.Outlet)).Select(row => row.Outlet).Should().BeEquivalentTo("NHK", "Dawn");
        story.Sources.Where(source => nonWestern.Matches(source.Report)).Select(source => source.Report.Id).Should().BeEquivalentTo("nhk", "dawn");

        var englishInAsia = nonWestern.WithRegion(WorldRegions.Asia).WithLanguage("english");
        story.Outlets.Where(englishInAsia.Matches).Select(outlet => outlet.Name).Should().Equal("Dawn");
        englishInAsia.WithRegion(null).WithLanguage(null).IsEverything.Should().BeTrue();

        // An outlet the analysis names that isn't among the story's outlets shows only unfiltered.
        StoryFilter.Everything.Matches(story, "Spiegel Online").Should().BeTrue();
        nonWestern.Matches(story, "Spiegel Online").Should().BeFalse();
        StoryFilter.Everything.WithRegion(StoryFilter.Western).Matches(story, "Spiegel Online").Should().BeFalse();

        // One region and one language: no chips to offer.
        var single = EventDocument.Build(e with { Countries = ["United Kingdom"], Languages = ["English"] }, [bbc], null, null);
        StoryFilter.Regions(single).Should().BeEmpty();
        StoryFilter.Languages(single).Should().BeEmpty();
    }

    [Fact]
    public void Translator_ShouldAskOncePerHeadline_AndReadNumberedAnswers()
    {
        var reports = new[]
        {
            Article("en", "English headline", "English", Now),
            Article("ja1", "イスタンブールで洪水", "Japanese", Now.AddMinutes(-5)) with { Outlet = "NHK" },
            Article("ja2", "洪水の続報", "Japanese", Now) with { Outlet = "NHK" },
            Article("es", "Inundaciones en Estambul", "Spanish", Now.AddMinutes(-9)) with { Outlet = "El País" },
            Article("de", "Flut in Istanbul", "German", Now.AddMinutes(-1)) with { Outlet = "Der Spiegel" }
        };
        var pending = HeadlineTranslator.Choose(reports, new Dictionary<string, string> { ["de"] = "Flood in Istanbul" });
        pending.Select(a => a.Id).Should().Equal("ja2", "es", "ja1");
        HeadlineTranslator.Prompt(pending).Should().Be("1. 洪水の続報\n2. Inundaciones en Estambul\n3. イスタンブールで洪水");

        HeadlineTranslator.Parse("<think>hm</think>1. Flood update\n2) \"Floods in Istanbul\"\n3. イスタンブールで洪水\n9. Out of range", pending)
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["ja2"] = "Flood update", ["es"] = "Floods in Istanbul" },
                "an untranslated line and a number out of range are left out");
    }

    [Fact]
    public void ReportTranslator_ShouldKeepReportIdentityAndOriginals_WithEnglishDescriptionsAndSummaries()
    {
        var foreign = Article("de", "Hochwasser in Berlin", "German", Now) with { Snippet = "Schulen bleiben geschlossen." };
        var native = Article("en", "Roads reopen", "English", Now);
        var parsed = HeadlineTranslator.ParseReports("""
            ```json
            [
              {"number":2,"headline":"Roads reopen","description":null,"summary":"The headline reports that roads have reopened."},
              {"number":1,"headline":"Flooding in Berlin","description":"Schools remain closed.","summary":"Flooding affects Berlin and schools remain closed."}
            ]
            ```
            """, [foreign, native]);

        parsed.Select(report => report.ArticleId).Should().Equal("de", "en");
        parsed[0].SourceTitle.Should().Be(foreign.Title);
        parsed[0].SourceSnippet.Should().Be(foreign.Snippet);
        parsed[0].Description.Should().Be("Schools remain closed.");
        (foreign with { English = parsed[0] }).DisplayTitle.Should().Be("Flooding in Berlin");
        foreign.Title.Should().Be("Hochwasser in Berlin");
        foreign.DisplayTitle.Should().Be("English translation pending");
        foreign.DisplayDescription.Should().BeNull();
        native.DisplayTitle.Should().Be("Roads reopen");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"number":"1","headline":"Flood","summary":"Flooding affects the city."}]""")]
    [InlineData("""[{"number":2,"headline":"Flood","summary":"Flooding affects the city."}]""")]
    [InlineData("""[{"number":1,"headline":"Flood","summary":"Flooding affects the city."}]""")]
    [InlineData("""[{"number":1,"headline":"Flood","description":"Schools close.","summary":"Flooding affects the city."},{"number":1,"headline":"Flood","description":"Schools close.","summary":"Flooding affects the city."}]""")]
    public void ReportTranslator_ShouldRejectMissingDescriptionsAndUnreliableNumbering(string response)
    {
        var report = Article("de", "Hochwasser in Berlin", "German", Now) with { Snippet = "Schulen bleiben geschlossen." };
        var parse = () => HeadlineTranslator.ParseReports(response, [report]);
        parse.Should().Throw<JsonException>();
    }

    // 21-11-26: one report without its description, or one broken object, used to fail the whole batch,
    // and the same batch failed on every retry, so the English backlog never moved.
    [Fact]
    public void ReportTranslator_ShouldKeepEveryUsableReport_WhenOthersInTheBatchAreMissingOrBroken()
    {
        var reports = new[]
        {
            Article("a", "Hochwasser in Berlin", "German", Now) with { Snippet = "Schulen bleiben geschlossen." },
            Article("b", "Inundaciones en Madrid", "Spanish", Now) with { Snippet = "Cierran las escuelas." },
            Article("c", "Incendie à Lyon", "French", Now),
            Article("d", "Sciopero a Roma", "Italian", Now),
        };
        var missingDescription = HeadlineTranslator.ParseReports("""
            [{"number":1,"headline":"Flooding in Berlin","description":"Schools remain closed.","summary":"Flooding hits Berlin and schools close."},
             {"number":2,"headline":"Floods in Madrid","description":null,"summary":"Floods hit Madrid and schools close."},
             {"number":3,"headline":"Fire in Lyon","summary":"A fire is reported in Lyon, France."}]
            """, reports);
        missingDescription.Select(report => report.ArticleId).Should().Equal(["a", "c"], "b left out its description, so only b retries");

        var broken = HeadlineTranslator.ParseReports("""
            [{"number":1,"headline":"Flooding in Berlin","description":"Schools remain closed.","summary":"Flooding hits Berlin and schools close."},
             {"number":3,"headline":"Fire in "Lyon" today","summary":"A fire is reported in Lyon, France."},
             {"number":4,"headline":"Strike in Rome","summary":"Workers strike in Rome, Italy, today."}]
            """, reports);
        broken.Select(report => report.ArticleId).Should().Equal(["a", "d"], "the object with the unescaped quotes is the only one lost");
    }

    [Fact]
    public void EnglishBacklog_ShouldIncludeUngroupedEnglishAndArchivedReports_AndPersistRetries()
    {
        var store = new WorldNewsStore(_dbPath);
        var old = Article("old", "Hochwasser in Berlin", "German", Now.AddDays(-20));
        var recent = Article("recent", "Roads reopen", "English", Now);
        store.AddArticles([old, recent]);
        store.ReportsDueEnglish(Now, 8).Select(report => report.Id).Should().Equal("recent", "old");
        store.ReportsDueEnglish(Now, 1, oldestFirst: true).Single().Id.Should().Be("old");
        store.DeferEnglishReports(["recent"], Now.AddMinutes(15));

        var reopened = new WorldNewsStore(_dbPath);
        reopened.ReportsDueEnglish(Now, 8).Single().Id.Should().Be("old");
        reopened.ReportsDueEnglish(Now.AddMinutes(15), 8).Select(report => report.Id).Should().Contain("recent");
        reopened.Translations(["recent"]).Should().BeEmpty("retry placeholders are not English headlines");
    }

    [Fact]
    public void EnglishCache_ShouldPreserveOriginalsAndLegacyWrites_AndSearchEnglishWithoutReplacingOriginalFts()
    {
        var store = new WorldNewsStore(_dbPath);
        var report = Article("de", "Hochwasser in Berlin", "German", Now) with { Snippet = "Schulen bleiben geschlossen." };
        var seed = Event("event", report.Title, [1f, 0f], 1);
        seed.TitleLanguage = "German";
        store.AddArticles([report]);
        store.SaveClustering(new ClusterResult([(report.Id, seed.Id)], [seed], 1));
        var english = new ReportTranslation(report.Id, report.Title, report.Snippet, "Flooding in Berlin",
            "Schools remain closed.", "Evacuation centres have opened as flooding affects Berlin.");
        store.SaveTranslations(new Dictionary<string, string> { [report.Id] = english.Headline }, Now);
        store.RecentReports(Now.AddDays(-1)).Single().DisplayTitle.Should().Be(english.Headline);
        store.ReportsDueEnglish(Now, 8).Single().Id.Should().Be(report.Id, "legacy headline-only translations still need descriptions and summaries");
        store.SaveEnglishReports([english], Now).Should().Be(1);
        store.SaveTranslations(new Dictionary<string, string> { [report.Id] = english.Headline }, Now.AddSeconds(1));

        var reopened = new WorldNewsStore(_dbPath);
        var saved = reopened.RecentReports(Now.AddDays(-1)).Single();
        saved.Title.Should().Be(report.Title);
        saved.Snippet.Should().Be(report.Snippet);
        saved.English.Should().Be(english);
        reopened.ReportsDueEnglish(Now, 8).Should().BeEmpty();
        reopened.Event(seed.Id)!.Headline.Should().Be(english.Headline);
        reopened.World(Now.AddDays(-1)).BiggestStories.Single().Event.Headline.Should().Be(english.Headline);
        reopened.RecentReports(Now.AddDays(-1), text: "evacuation").Single().Id.Should().Be(report.Id);
        reopened.RecentReports(Now.AddDays(-1), text: "Hochwasser").Single().Id.Should().Be(report.Id);
        reopened.EventsMatchingText("evacuation", Now.AddDays(-1), 10).Single().EventId.Should().Be(seed.Id);
    }

    [Fact]
    public void EnglishHeadlines_ShouldFollowLaterGrouping_IntoSituationsAndPublicLiveCoverage()
    {
        var store = new WorldNewsStore(_dbPath);
        var first = Article("first", "Hochwasser in Berlin", "German", Now);
        var second = Article("second", "Flusspegel steigen in Berlin", "German", Now);
        store.AddArticles([first, second]);
        store.SaveEnglishReports([
            new(first.Id, first.Title, null, "Flooding in Berlin", null, "The report describes flooding in Berlin."),
            new(second.Id, second.Title, null, "River levels rise in Berlin", null, "The report describes rising river levels in Berlin.")
        ], Now);
        var events = new[] { Event("first-event", first.Title, [1f, 0f], 1), Event("second-event", second.Title, [1f, 0f], 1) };
        foreach (var e in events) e.TitleLanguage = "German";
        store.SaveClustering(new ClusterResult([(first.Id, events[0].Id), (second.Id, events[1].Id)], events, 2));
        var situation = new NewsSituation { Id = "berlin", Sum = [2f, 0f], LastSeen = Now };
        store.SaveLinks(events.Select(e => new SituationLink(e.Id, situation)).ToList());
        store.AddLiveUpdate(events[0].Id, "editor", "Public coverage has started.", "unconfirmed", Now);

        store.Event(events[0].Id)!.Headline.Should().Be("Flooding in Berlin");
        store.Situation(situation.Id)!.Title.Should().BeOneOf("Flooding in Berlin", "River levels rise in Berlin");
        store.PublicLiveStories(Now).Single().Title.Should().Be("Flooding in Berlin");
    }

    [Fact]
    public void EnglishCache_ShouldRejectChangedSourceVersions_AndNeverSearchStaleEnglish()
    {
        var store = new WorldNewsStore(_dbPath);
        var report = Article("de", "Hochwasser in Berlin", "German", Now);
        store.AddArticles([report]);
        var english = new ReportTranslation(report.Id, report.Title, null, "Flooding in Berlin", null,
            "Evacuation centres have opened in Berlin.");
        store.SaveEnglishReports([english], Now).Should().Be(1);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE articles SET title = 'Wasserstand sinkt' WHERE id = $id";
            command.Parameters.AddWithValue("$id", report.Id);
            command.ExecuteNonQuery();
        }
        store.SaveEnglishReports([english], Now.AddMinutes(1)).Should().Be(0);
        store.RecentReports(Now.AddDays(-1)).Single().English.Should().BeNull();
        store.ReportsDueEnglish(Now, 8).Single().Id.Should().Be(report.Id);
        store.RecentReports(Now.AddDays(-1), text: "evacuation").Should().BeEmpty();
    }

    [Fact]
    public void Analyzer_ShouldReadItsFourParts_AndKeepDifferencesOnRealOutletsOnly()
    {
        var answer = """
            **CLAIM:** Officials say heavy rain caused the floods that killed 64 people; this is presented as confirmed.
            **SUMMARY:** Floods hit Istanbul on Tuesday after heavy rain. Most outlets put the dead at 64, citing officials. Rescue work continued overnight.
            **AGREED:**
            - Floods hit Istanbul on Tuesday [BBC, Reuters, Dawn, Der Spiegel, The Daily Planet].
            * Officials put the toll at 64 [BBC, Reuters, Dawn]
            - Rescue work went on overnight
            **DIFFERENCES:**
            - BBC: gives no cause for the flooding.
            - "Der Spiegel": reports 70 dead, more than the others.
            - The Daily Planet: says the dam broke.
            - Coverage in Japanese leaves out the toll.
            """;
        var parsed = EventAnalyzer.Parse(answer, ["BBC News", "Der Spiegel", "Reuters", "Dawn"]);

        parsed.Should().NotBeNull();
        parsed!.Value.Claim.Should().StartWith("Officials say heavy rain");
        parsed.Value.Summary.Should().StartWith("Floods hit Istanbul on Tuesday").And.EndWith("overnight.");
        parsed.Value.Agreed.Should().BeEquivalentTo(new[]
        {
            new AgreedFact("Floods hit Istanbul on Tuesday.", ["BBC News", "Reuters", "Dawn", "Der Spiegel"]),
            new AgreedFact("Officials put the toll at 64.", ["BBC News", "Reuters", "Dawn"]),
            new AgreedFact("Rescue work went on overnight", [])
        }, options => options.WithStrictOrdering(), "an outlet the event doesn't have is dropped from a fact");
        var analysis = new EventAnalysis("e1", parsed.Value.Claim, parsed.Value.Summary, parsed.Value.Agreed, parsed.Value.Differences, "m", 5, 2, Now);
        EventAnalyzer.Compare(analysis).Should().BeEquivalentTo(new[]
        {
            new OutletComparison("Der Spiegel", ["Officials put the toll at 64."], "reports 70 dead, more than the others."),
            new OutletComparison("BBC News", [], "gives no cause for the flooding.")
        }, options => options.WithStrictOrdering(), "what an outlet leaves out comes from the facts it isn't listed for, and more gaps come first");
        parsed.Value.Differences.Should().Equal(
            new OutletDifference("BBC News", "gives no cause for the flooding."),
            new OutletDifference("Der Spiegel", "reports 70 dead, more than the others."),
            new OutletDifference("", "The Daily Planet: says the dam broke."),
            new OutletDifference("", "Coverage in Japanese leaves out the toll."));
        EventAnalyzer.Parse("<think>CLAIM: hidden</think>No labels at all, just a sentence.", ["BBC"]).Should().BeNull();
        EventAnalyzer.Parse("Präsident Donald Trump hat angekündigt, dass das FBI in Ermittlungen über einen Cyberangriff verwickelt ist und weitere Details folgen bald.", ["BBC"])
            .Should().BeNull("an unlabelled paragraph without CLAIM/SUMMARY structure must not be saved as a partial analysis");
    }

    [Fact]
    public void Analyzer_ShouldPutTheArticlesItReadFirst_AndHearEveryOutletWithinTheBudget()
    {
        var reports = Enumerable.Range(0, 30).Select(i => Article($"a{i}", $"Report {i} on the floods", i % 3 == 0 ? "Spanish" : "English", Now.AddMinutes(-i))
            with { Outlet = $"Outlet {i}", Snippet = $"Description number {i}: " + new string((char)('a' + i % 26), 130) }).ToList();
        var e = new EventOverview("e1", "Floods", 30, 30, ["Somewhere"], ["English", "Spanish"], Now.AddHours(-1), Now, null);
        var excerpts = new Dictionary<string, string> { ["a4"] = "The river burst its banks at dawn. " + new string('y', 2000) };
        string Format(DateTimeOffset at) => at.ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var prompt = EventAnalyzer.BuildPrompt(e, reports, excerpts, Format, maxChars: 4000);

        prompt.Length.Should().BeLessThanOrEqualTo(4000);
        prompt.Should().StartWith("Event: Floods").And.Contain("### Outlet 4 (Somewhere, English").And.Contain("Article: The river burst its banks at dawn.");
        prompt.IndexOf("### Outlet 4", StringComparison.Ordinal).Should().BeLessThan(prompt.IndexOf("Other outlets' reports", StringComparison.Ordinal));
        prompt.Should().Contain("(Somewhere, Spanish)", "languages take turns, so Spanish is heard even when everything doesn't fit");
        prompt.Should().NotContain("- Outlet 4 (", "an outlet whose article was read isn't listed twice");

        var due = new EventAnalysis("e1", "c", "s", [], [], "m", 30, 1, Now);
        EventAnalyzer.IsDue(null, e).Should().BeTrue();
        EventAnalyzer.IsDue(due, e).Should().BeFalse();
        EventAnalyzer.IsDue(due with { Claim = "" }, e).Should().BeTrue("a broken analysis with no claim is re-run automatically");
        EventAnalyzer.IsDue(due with { Summary = "" }, e).Should().BeTrue("a broken analysis with no summary is re-run automatically");
        EventAnalyzer.IsDue(due with { ArticleCount = 20 }, e).Should().BeTrue("coverage grew by half");
    }

    [Fact]
    public void Store_ShouldKeepTheLatestAnalysis_AndTheArticlesRead_AndPruneThemWithTheirEvents()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "big", Reports("big", 4));
        var analysis = new EventAnalysis("big", "The claim.", "The summary\nwith a break.",
            [new AgreedFact("One point.", ["Outlet big0", "Outlet big1"]), new AgreedFact("Another\tpoint.", [])],
            [new OutletDifference("Outlet big0", "leaves out the toll."), new OutletDifference("", "No one names the town.")], "SentinelGPT on the Mac", 4, 1,
            DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()));

        store.SaveAnalysis(analysis with { Claim = "An older claim." });
        var saved = store.SaveAnalysis(analysis);

        saved.Should().BeEquivalentTo(analysis with
        {
            Summary = "The summary with a break.",
            Agreed = [new AgreedFact("One point.", ["Outlet big0", "Outlet big1"]), new AgreedFact("Another point.", [])]
        });
        store.LatestAnalysis("missing").Should().BeNull();
        store.SaveArticleText("big0", "The river burst its banks.", null, Now);
        store.SaveArticleText("big1", null, "answered 403", Now);
        store.ArticleTexts(["big0", "big1", "nope"]).Should().BeEquivalentTo(new Dictionary<string, (string?, DateTimeOffset)>
        {
            ["big0"] = ("The river burst its banks.", DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds())),
            ["big1"] = (null, DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()))
        });

        store.Prune(Now.AddDays(1));
        store.LatestAnalysis("big").Should().BeNull("an analysis goes with its event");
        store.ArticleTexts(["big0"]).Should().BeEmpty("and what was read goes with its article");
    }

    [Fact]
    public void ArticleReader_ShouldReadOneArticlePerOutlet_FromTheOutletsOwnSites_LanguagesTakingTurns()
    {
        var reports = new[]
        {
            Article("g1", "Via Google", "English", Now) with { Outlet = "Google-only", Url = "https://news.google.com/rss/articles/CBMiabc" },
            Article("b1", "BBC older", "English", Now.AddHours(-2)) with { Outlet = "BBC", Url = "https://www.bbc.co.uk/news/1" },
            Article("b2", "BBC newer", "English", Now.AddHours(-1)) with { Outlet = "BBC", Url = "https://www.bbc.co.uk/news/2" },
            Article("r1", "Reuters", "English", Now.AddHours(-3)) with { Outlet = "Reuters", Url = "https://www.reuters.com/1" },
            Article("e1", "El País", "Spanish", Now.AddHours(-4)) with { Outlet = "El País", Url = "https://elpais.com/1" },
            Article("x1", "Not the web", "English", Now) with { Outlet = "Odd", Url = "ftp://example.com/1" }
        };

        GwsBusinessSuite.Infrastructure.Services.WorldNewsArticleReader.Choose(reports, max: 2).Select(a => a.Id).Should().Equal("b2", "e1");
        GwsBusinessSuite.Infrastructure.Services.WorldNewsArticleReader.Choose(reports).Select(a => a.Id).Should().BeEquivalentTo(["b2", "r1", "e1"]);
        GwsBusinessSuite.Infrastructure.Services.WorldNewsArticleReader.Excerpt(new GwsBusinessSuite.Infrastructure.WebCrawl.CrawledPage(
            "t", "d", null, null, false, false, "Too short.", [])).Should().BeNull("a paywall leaves too little to go on");
        GwsBusinessSuite.Infrastructure.Services.WorldNewsArticleReader.Excerpt(new GwsBusinessSuite.Infrastructure.WebCrawl.CrawledPage(
            "t", "d", null, null, false, false, "The river   burst\n its banks. " + new string('z', 5000), []))!
            .Should().StartWith("The river burst its banks.").And.HaveLength(EventAnalyzer.MaxExcerptChars * 2);
    }
    [Fact]
    public void Summarizer_ShouldHandOutJobs_AndSaveAnotherMachinesAnswers()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "big", Reports("big", 5));
        SeedEvent(store, "small", Reports("small", 2));
        var summarizer = new EventSummarizer(store, (_, _, _) => throw new InvalidOperationException("the server's model isn't asked"),
            "test-model", new ManualTime(Now), at => at.ToString("dd-MM-yy HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            new SummaryOptions());

        var job = summarizer.Jobs(5).Should().ContainSingle("only events 3+ outlets covered are due").Subject;
        job.Should().Match<SummaryJob>(j => j.EventId == "big" && j.ArticleCount == 5 && j.SystemPrompt == EventSummarizer.SystemPrompt
            && j.Prompt.Contains("Outlet big0"));

        summarizer.Save("big", "no labels and too short", "gemma4:latest on the Mac", 5).Should().Be("That answer has no usable summary.");
        summarizer.Save("gone", "HEADLINE: x\nSUMMARY: y", "gemma4:latest on the Mac", 5).Should().Be("That event isn't there any more.");
        summarizer.Save("big", "HEADLINE: Big event in English\nSUMMARY: What happened, where and when, written on the Mac.",
            "gemma4:latest on the Mac", 5).Should().BeNull();

        store.LatestSummary("big")!.Should().Match<EventSummary>(s => s.Model == "gemma4:latest on the Mac" && s.Headline == "Big event in English");
        summarizer.Jobs(5).Should().BeEmpty("it's due again only once coverage grows");
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

    // 07-11-26: the US outlets' own feeds came in beside their Google News reports, and Google News
    // names some outlets by their site's title or a short form.
    [Fact]
    public void OutletNames_ShouldGiveOneNamePerOutlet_AndTellTheTwoAbcsApart()
    {
        OutletNames.Canonical("WSJ").Should().Be("The Wall Street Journal");
        OutletNames.Canonical("Bloomberg.com").Should().Be("Bloomberg");
        OutletNames.Canonical("ABC News - Breaking News, Latest News and Videos").Should().Be("ABC News (US)");
        OutletNames.Canonical("ABC News & Headlines – Australian Broadcasting Corporation").Should().Be("ABC News (Australia)");
        OutletNames.Canonical("Le Monde.fr").Should().Be("Le Monde.fr");

        const string feed = """
            <rss version="2.0"><channel><item>
              <title>Senate passes the budget - WSJ</title>
              <link>https://news.google.com/rss/articles/WSJ1?oc=5</link>
              <guid isPermaLink="false">wsj-1</guid>
              <pubDate>Tue, 20 Oct 2026 14:00:00 GMT</pubDate>
              <source url="https://www.wsj.com">WSJ</source>
            </item></channel></rss>
            """;
        var report = GoogleNewsFeed.Parse(feed, new NewsEdition("United States", "English", "en-US", "US", "US:en")).Should().ContainSingle().Subject;
        report.Outlet.Should().Be("The Wall Street Journal");
        report.Title.Should().Be("Senate passes the budget", "the short name still comes off the headline");
    }

    [Fact]
    public void OutletFeeds_ShouldListEachFeedOnce_UnderItsOneName_WithARegionForEachCountry()
    {
        OutletFeeds.Outlets.Select(o => o.Url).Should().OnlyHaveUniqueItems();
        OutletFeeds.Outlets.Should().OnlyContain(o => OutletNames.Canonical(o.Name) == o.Name, "an own feed uses the name its Google News reports get");
        OutletFeeds.Outlets.Where(o => o.Country != OutletFeeds.International)
            .Should().OnlyContain(o => WorldRegions.Of(o.Country) != WorldRegions.Other, "the region chips need each outlet's region");
        OutletFeeds.Outlets.Should().Contain(o => o.Name == "ABC News (US)").And.Contain(o => o.Name == "ABC News (Australia)");
    }

    [Fact]
    public void OutletNames_ShouldMarkStateRunOutlets_NotPublicBroadcasters()
    {
        OutletNames.IsStateMedia("TASS").Should().BeTrue();
        OutletNames.IsStateMedia("新华网").Should().BeTrue();
        OutletNames.IsStateMedia("CGTN").Should().BeTrue();
        OutletNames.IsStateMedia("BBC News").Should().BeFalse();
        OutletNames.IsStateMedia("NHK").Should().BeFalse();
        OutletNames.IsStateMedia(null).Should().BeFalse();

        var tass = Article("tass", "Floods in Istanbul", "English", Now.AddHours(-2)) with { Outlet = "TASS", Country = "Russia" };
        var e = new EventOverview("e1", "Floods in Istanbul", 1, 1, ["Russia"], ["English"], Now.AddHours(-2), Now.AddHours(-2), "Floods in Istanbul");
        EventDocument.ToMarkdown(EventDocument.Build(e, [tass], null, null), at => at.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Contain("### TASS: Russia, English, state media, 1 report");
    }

    [Fact]
    public void WhoOutbreakNews_ShouldReadTheNewestItems_AsTheAgencysOwnReports()
    {
        const string json = """
            {"value":[
              {"UrlName":"2026-DON625","Title":"Ebola disease caused by Bundibugyo virus, Democratic Republic of the Congo, Kenya and Nigeria",
               "UseOverrideTitle":false,"OverrideTitle":"","PublicationDateAndTime":"2026-10-20T10:42:12Z",
               "Summary":"<p>Since the previous Disease Outbreak News, the outbreak has <b>expanded</b> in the Democratic Republic of the Congo.</p>"},
              {"UrlName":"2026-DON624","Title":"Old title","UseOverrideTitle":true,"OverrideTitle":"Yellow fever - WHO African Region",
               "PublicationDateAndTime":"2026-10-21T19:00:00Z","Summary":""},
              {"UrlName":"","Title":"No page","PublicationDateAndTime":"2026-10-19T19:00:00Z"}
            ]}
            """;
        var reports = WhoOutbreakNews.Parse(json, Now);

        reports.Should().HaveCount(2, "an item without a page is skipped");
        var ebola = reports[0];
        ebola.Outlet.Should().Be("World Health Organization");
        ebola.Country.Should().Be(OutletFeeds.International);
        ebola.Url.Should().Be("https://www.who.int/emergencies/disease-outbreak-news/item/2026-DON625");
        ebola.Snippet.Should().Be("Since the previous Disease Outbreak News, the outbreak has expanded in the Democratic Republic of the Congo.");
        ebola.SeenAt.Should().Be(DateTimeOffset.Parse("2026-10-20T10:42:12Z", System.Globalization.CultureInfo.InvariantCulture));
        reports[1].Title.Should().Be("Yellow fever - WHO African Region");
        reports[1].SeenAt.Should().Be(Now, "a date after now is taken as now");
        reports[1].Snippet.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_ShouldReadWhoOutbreakNews_OnlyWhenTheHostTurnsItOn()
    {
        var store = new WorldNewsStore(_dbPath);
        var whoUp = true;
        var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host switch
        {
            "www.who.int" when whoUp => (HttpStatusCode.OK,
                """{"value":[{"UrlName":"2026-DON625","Title":"Ebola - Kenya","PublicationDateAndTime":"2026-10-20T10:00:00Z","Summary":""}]}"""),
            "www.who.int" => (HttpStatusCode.ServiceUnavailable, ""),
            _ => (HttpStatusCode.InternalServerError, "no model")
        }));
        WorldNewsPipeline Pipeline(bool on) => new(store, http, new GdeltDocClient(http, new ManualTime(Now)),
            new OllamaClient(new Uri("http://localhost:11434/"), http), new ManualTime(Now),
            new WorldNewsOptions { Editions = [], Outlets = [], ReadWhoOutbreakNews = on });

        (await Pipeline(false).RunCycleAsync(CancellationToken.None)).Added.Should().Be(0, "it's off unless the host turns it on");
        var report = await Pipeline(true).RunCycleAsync(CancellationToken.None);
        report.Added.Should().Be(1);
        report.SourcesAttempted.Should().Be(1);
        report.SourcesSucceeded.Should().Be(1);
        report.Deduplicated.Should().Be(0);
        report.EmbeddingBacklog.Should().Be(1, "the test model is unavailable so the report remains queued");
        report.LatestReportAt.Should().Be(Now.AddHours(-5));
        store.ArticlesToEmbed(10).Should().ContainSingle(a => a.Outlet == WhoOutbreakNews.Outlet && a.Feed == WhoOutbreakNews.Feed);

        whoUp = false;
        (await Pipeline(true).RunCycleAsync(CancellationToken.None)).Notes.Should().Contain(n => n.StartsWith("WHO outbreak news:"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
    }

    // 19-11-26: crawled fixture pages dated weeks ahead stayed on Today (newest report: 22-02-27).
    [Fact]
    public void Prune_ShouldDropFutureDatedReports_AndGiveTheirStoriesATrueLastSeen()
    {
        var store = new WorldNewsStore(_dbPath);
        var fixture = Article("fixture", "Play-off: Netherlands - Slovenia", "English", Now.AddDays(20));
        var real = Article("real", "Floods in Valencia", "English", Now.AddHours(-1));
        var misdated = Article("misdated", "Floods in Valencia: rescue", "English", Now.AddDays(30));
        var early = Article("early", "Summit opens", "English", Now.AddMinutes(30));
        store.AddArticles([fixture, real, misdated, early]);
        var future = Event("future", fixture.Title, [1f, 0f], 1);
        future.FirstSeen = Now.AddDays(20);
        future.LastSeen = Now.AddDays(20);
        var mixed = Event("mixed", real.Title, [0f, 1f], 2);
        mixed.LastSeen = Now.AddDays(30);
        var soon = Event("soon", early.Title, [1f, 1f], 1);
        soon.LastSeen = Now.AddMinutes(30);
        store.SaveClustering(new ClusterResult(
            [(fixture.Id, "future"), (real.Id, "mixed"), (misdated.Id, "mixed"), (early.Id, "soon")], [future, mixed, soon], 3));

        store.Prune(Now.AddDays(-7), Now);

        store.RecentReports(Now.AddDays(-1)).Select(r => r.Id).Should().BeEquivalentTo(["real", "early"],
            "a report up to an hour ahead is a clock difference; weeks ahead is a fixture date");
        var events = store.ActiveEvents(Now.AddDays(-1)).ToDictionary(e => e.Id);
        events.Should().NotContainKey("future", "a story with only future-dated reports goes");
        events["mixed"].LastSeen.Should().Be(Now.AddHours(-1));
        events["soon"].LastSeen.Should().Be(Now.AddMinutes(30));
    }

    private static WorldArticle Article(string id, string title, string language, DateTimeOffset seen, string? hint = null) =>
        new(id, $"https://example.com/{id}", title, "Outlet " + id, language, "Somewhere", seen, "test", hint);

    [Fact]
    public void Store_ShouldOfferTheNewestReportsToEmbed_AndNoneTooOldToJoinAnEvent()
    {
        var store = new WorldNewsStore(_dbPath);
        store.AddArticles([
            Article("stale", "Old news", "English", Now.AddDays(-3)),
            Article("older", "Yesterday's news", "English", Now.AddHours(-20)),
            Article("newer", "This morning's news", "English", Now.AddHours(-1))
        ]);

        store.RecentArticlesToEmbed(10, Now.AddHours(-48)).Select(a => a.Id).Should().Equal("newer", "older");
        store.ArticlesToEmbed(10).Should().HaveCount(3, "the full list still has the stale one");
    }

    [Fact]
    public void Store_ShouldFindOneSituationDirectly()
    {
        var store = new WorldNewsStore(_dbPath);
        SeedEvent(store, "rally", Reports("rally", 3), [1f, 0f]);
        SeedEvent(store, "results", Reports("results", 3), [1f, 0f]);
        var situation = new NewsSituation { Id = "election", Sum = [6f, 0f], LastSeen = Now };
        store.SaveLinks([new SituationLink("rally", situation), new SituationLink("results", situation)]);

        store.Situation("election")!.Should().Match<SituationOverview>(s => s.EventCount == 2 && s.ArticleCount == 6);
        store.Situation("nope").Should().BeNull();
    }

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

    [Fact]
    public void WorldNewsMapService_AdaptiveThreshold_AllowsOperationalStoriesWithTwoOutlets()
    {
        var operational = new SituationOverview("sit-1", "Train derailment halts line", "Train derails near station", 
            1, 2, 2, ["FR"], ["fr"],
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            Kind: SituationKinds.Transport);

        Assert.True(WorldNewsMapService.QualifiesForMap(operational));

        var entertainment = new SituationOverview("sit-2", "Pop star releases single", "Album tops streaming charts", 
            1, 2, 2, ["FR"], ["fr"],
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            Kind: SituationKinds.Other);

        Assert.False(WorldNewsMapService.QualifiesForMap(entertainment));
    }

    [Fact]
    public void OutletFeeds_ContainsExpandedRegionalNewsrooms()
    {
        Assert.Contains(OutletFeeds.Outlets, o => o.Name == "The National" && o.Country == "United Arab Emirates");
        Assert.Contains(OutletFeeds.Outlets, o => o.Name == "Focus Taiwan" && o.Country == "Taiwan");
        Assert.Contains(OutletFeeds.Outlets, o => o.Name == "Civil.ge" && o.Country == "Georgia");
        Assert.Contains(OutletFeeds.Outlets, o => o.Name == "El Tiempo" && o.Country == "Colombia");
        // 19-11-26 (expansion Unit 1.1): places the roster barely covered.
        foreach (var country in new[] { "Nigeria", "Ghana", "Kenya", "Saudi Arabia", "Israel", "Thailand", "Myanmar",
                     "Philippines", "Kazakhstan", "Peru", "Cuba" })
            OutletFeeds.Outlets.Should().Contain(o => o.Country == country, $"{country} has a newsroom of its own now");
        OutletFeeds.Outlets.Count(o => o.Country == "Nigeria").Should().BeGreaterThanOrEqualTo(2);
        OutletNames.IsStateMedia("VnExpress International").Should().BeTrue();
        OutletFeeds.Outlets.Should().NotContain(o => o.Name == "AllAfrica", "it republishes other outlets' reports");
    }

    // 19-11-26 (expansion Unit 1.2): one local outlet is enough for a conflict or disaster where few
    // of the outlets World News reads are based, and nowhere else.
    [Fact]
    public void WorldNewsMapService_ThinCoverage_AllowsOneLocalOutletForConflictOrDisaster()
    {
        var places = WorldNewsPlaces.Instance;
        var myanmar = places.At(places.FindCountry("Myanmar")!, null, WorldNewsPrecision.Country);
        var unitedStates = places.At(places.FindCountry("United States")!, null, WorldNewsPrecision.Country);
        SituationOverview Story(string kind, int outlets, params string[] sourceCountries) => new("sit", "Fighting near Lashio", "Fighting near Lashio",
            1, outlets, outlets, sourceCountries, ["English"], DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, Kind: kind);

        WorldNewsMapService.IsThinlyCovered(myanmar.Country).Should().BeTrue("one newsroom is based there");
        WorldNewsMapService.IsThinlyCovered(unitedStates.Country).Should().BeFalse();

        WorldNewsMapService.QualifiesForMap(Story(SituationKinds.Conflict, 1, myanmar.Country), myanmar).Should().BeTrue();
        WorldNewsMapService.QualifiesForMap(Story(SituationKinds.Disaster, 1, myanmar.Country), myanmar).Should().BeTrue();
        WorldNewsMapService.QualifiesForMap(Story(SituationKinds.Conflict, 1, "United Kingdom"), myanmar)
            .Should().BeFalse("the one outlet isn't local");
        WorldNewsMapService.QualifiesForMap(Story(SituationKinds.Other, 1, myanmar.Country), myanmar)
            .Should().BeFalse("only conflicts and disasters");
        WorldNewsMapService.QualifiesForMap(Story(SituationKinds.Conflict, 1, unitedStates.Country), unitedStates)
            .Should().BeFalse("a well-covered country keeps the usual bar");
        WorldNewsMapService.QualifiesForMap(Story(SituationKinds.Conflict, 3, "United Kingdom"), unitedStates)
            .Should().BeTrue("the usual rule still applies everywhere");
    }

    [Fact]
    public void WorldNewsMapService_SignalsInBounds_FiltersCorrectly()
    {
        var service = new WorldNewsMapService(new WorldNewsStore(_dbPath));
        var bounds = new BoundingBox(40, 30, -80, -90);
        var signals = service.SignalsInBounds(bounds, DateTimeOffset.UtcNow);
        Assert.NotNull(signals);
    }

    [Fact]
    public void OverwatchBriefingService_SummarizeViewport_ProducesKeyPoints()
    {
        var service = new OverwatchBriefingService(null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, null!);
        var signal = new WorldNewsMapSignal("sit-1", "Conflict flares in capital", "Shelling reported near port",
            new WorldNewsLocation("Tripoli", "Libya", "LY", 32.8872, 13.1913, WorldNewsPrecision.City),
            1, 6, 12, DateTimeOffset.UtcNow, "Port shelling continues", [], Surge: "Surging +5 outlets in 2h");

        var summary = service.SummarizeViewport([signal], [], 8);
        Assert.Contains(summary, s => s.Contains("SURGING"));
        Assert.Contains(summary, s => s.Contains("8 public traffic/infrastructure camera(s)"));
    }

    [Fact]
    public void SignalConvergenceService_DetectReportingBlackouts_FindsUncoveredHazards()
    {
        var bounds = new BoundingBox(45, 30, 20, 10);
        var hazard = new HazardFeature(
            Id: "hz-1",
            Kind: "earthquake",
            Title: "Major 7.1 earthquake strikes remote basin",
            Detail: "Felt across region",
            Latitude: 35.0,
            Longitude: 15.0,
            Severity: "major",
            Url: "https://usgs.gov",
            ObservedAt: DateTimeOffset.UtcNow);
        
        var blackouts = SignalConvergenceService.DetectReportingBlackouts([hazard], [], [], bounds);
        Assert.Single(blackouts);
        Assert.Contains("Major earthquake hazard", blackouts[0].Reason);
    }
}
