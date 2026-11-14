using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News trust plan, Phase 1 (13-11-26): noise, independence, confidence, the alert gate and
// headline edits.
public sealed class WorldNewsTrustTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static WorldArticle Report(string outlet, string title, string? snippet = null, string url = "", int minutes = 0) =>
        new(Guid.NewGuid().ToString("N"), url.Length > 0 ? url : $"https://{outlet.Replace(" ", "").ToLowerInvariant()}.example/{Guid.NewGuid():N}",
            title, outlet, "English", "United States", Now.AddMinutes(minutes), "test", null, snippet);

    [Theory]
    [InlineData("https://www.prnewswire.com/news/acme-1.html", "Acme launches widget", "Some Site", "press release")]
    [InlineData("https://news.example/a", "Acme launches widget", "GlobeNewswire", "press release")]
    [InlineData("https://news.example/a", "Top 10 beaches to visit this winter", "Travel Daily", "listicle")]
    [InlineData("https://news.example/a", "The 15 best laptops of 2026", "Gadgets", "listicle")]
    [InlineData("https://news.example/a", "Your horoscope for Thursday", "Stars", "service page")]
    [InlineData("https://news.example/a", "How to watch the final: live stream free", "Sport", "service page")]
    public void Noise_ShouldKeepPressReleasesListiclesAndServicePagesOut(string url, string title, string outlet, string reason) =>
        NewsNoise.Reason(Report(outlet, title, url: url)).Should().Be(reason);

    [Theory]
    [InlineData("Earthquake of magnitude 6.1 strikes off Japan")]
    [InlineData("10 dead as floods hit southern Brazil")]
    [InlineData("Top officials meet in Geneva over ceasefire")]
    public void Noise_ShouldLeaveRealNewsAlone(string title) => NewsNoise.IsNoise(Report("Reuters", title)).Should().BeFalse();

    [Fact]
    public void Independence_ShouldCountWireCopiesAndSameOwnerOutletsOnce()
    {
        var wire = "Ten people were killed when floods swept through the region on Wednesday, officials said, as rescuers searched for survivors.";
        var reports = new[]
        {
            Report("Reuters", "Floods kill 10 in southern Brazil", wire),
            Report("Yahoo News", "Floods kill 10 in southern Brazil", wire, minutes: 5),
            Report("MSN", "Floods kill 10 in southern Brazil", wire, minutes: 6),
            Report("Local Paper", "Floods kill 10 (Reuters)", minutes: 9),
            Report("NBC News", "Brazil floods: rescuers search for survivors", minutes: 10),
            Report("MSNBC", "Deadly floods in Brazil", minutes: 11),
            Report("The Guardian", "Brazil floods leave at least 10 dead", minutes: 12),
            Report("PR Newswire", "Acme donates to flood relief", minutes: 13),
        };

        var independence = SourceIndependence.Assess(reports);

        independence.Outlets.Should().Be(7, "the press release doesn't count");
        independence.Independent.Should().Be(3);
        independence.Origins.Select(o => o.Name).Should().Equal("Reuters", "Comcast (NBCUniversal)", "Guardian Media Group");
        independence.Origins[0].Outlets.Should().BeEquivalentTo(["Reuters", "Yahoo News", "MSN", "Local Paper"]);
        independence.Origins[0].Kind.Should().Be("outlet", "Reuters' own report names no credit; the copies follow it");
    }

    [Theory]
    [InlineData("Quake hits Turkey (AP)", "Associated Press")]
    [InlineData("PARIS (AFP) - Strikes halt trains", "AFP")]
    [InlineData("Berlin (dpa) - Parliament votes", "dpa")]
    [InlineData("Markets fall | Reuters", "Reuters")]
    [InlineData("Apple unveils new iPad", "Some Outlet")]
    public void Origin_ShouldReadWireCredits(string title, string origin) =>
        SourceIndependence.OriginOf(Report("Some Outlet", title)).Name.Should().Be(origin);

    [Fact]
    public void Confidence_ShouldBeHonest()
    {
        StoryIndependence Of(int independent, int outlets) =>
            new(outlets, Enumerable.Range(0, independent).Select(i => new SourceOrigin($"o{i}", "outlet", [$"o{i}"])).ToList());

        StoryConfidenceRules.Assess(Of(1, 12)).Should().Be(new StoryConfidence(StoryConfidenceLevel.SingleSource, "One source, repeated by 12 outlets."));
        StoryConfidenceRules.Assess(Of(1, 1)).Reason.Should().Be("One outlet so far.");
        StoryConfidenceRules.Assess(Of(2, 5)).Label.Should().Be("Reported");
        StoryConfidenceRules.Assess(Of(3, 5)).Label.Should().Be("Confirmed");
        StoryConfidenceRules.Assess(Of(1, 1), primarySource: "USGS").Reason.Should().Be("Confirmed by USGS.");
        StoryConfidenceRules.Assess(Of(5, 9), disputedFigures: 2).Should().Be(new StoryConfidence(StoryConfidenceLevel.Disputed, "Outlets give conflicting figures on 2 points."));
        StoryConfidenceRules.Assess(Of(5, 9), primarySource: "USGS", factCheckVerdict: "False").Label.Should().Be("Disputed", "a fact-check outranks everything");

        StoryConfidenceRules.MayAlert(Of(1, 30)).Should().BeFalse("one source repeated 30 times isn't confirmation");
        StoryConfidenceRules.MayAlert(Of(2, 2)).Should().BeTrue();
        StoryConfidenceRules.MayAlert(Of(1, 1), "WHO").Should().BeTrue();
    }

    [Fact]
    public void Store_ShouldDropNoise_KeepTheFirstHeadline_AndRecordEdits()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        var original = Report("Reuters", "Floods kill 10 in southern Brazil", url: "https://reuters.example/floods");
        store.AddArticles([original, Report("PR Newswire", "Acme donates", url: "https://prnewswire.com/x")]).Should().Be(1);

        store.AddArticles([original with { Title = "Floods kill 10 in southern Brazil!" }]).Should().Be(0);
        store.Edits([original.Id]).Should().BeEmpty("punctuation isn't an edit");

        store.AddArticles([original with { Title = "Correction: Floods kill 12 in southern Brazil", SeenAt = Now.AddHours(1) }]);
        store.AddArticles([original with { Title = "Correction: Floods kill 12 in southern Brazil", SeenAt = Now.AddHours(2) }]);
        store.AddArticles([original with { Title = "Floods kill 14 in southern Brazil", SeenAt = Now.AddHours(3) }]);

        var edits = store.Edits([original.Id]);
        edits.Select(e => (e.Before, e.After, e.Kind)).Should().Equal(
            ("Floods kill 10 in southern Brazil", "Correction: Floods kill 12 in southern Brazil", "correction"),
            ("Correction: Floods kill 12 in southern Brazil", "Floods kill 14 in southern Brazil", "edit"));
        store.RecentReports(Now.AddDays(-1)).Single().Title.Should().Be("Floods kill 10 in southern Brazil", "the first-seen headline is kept");
    }

    [Fact]
    public void WatchAlerts_ShouldWaitForTheGate_ThenAlertOnceItOpens()
    {
        using var test = new WorldNewsTestStore();
        test.Store.Watch("ann", "sit-1", WorldNewsTestStore.Now.AddDays(-1));
        test.SeedSituation("sit-1", "Floods in Brazil", "Rescue under way in Brazil");

        test.Store.CreateWatchAlerts(WorldNewsTestStore.Now, _ => false).Should().BeEmpty("still one source");
        var alerts = test.Store.CreateWatchAlerts(WorldNewsTestStore.Now, _ => true);
        alerts.Should().ContainSingle().Which.Message.Should().StartWith("2 new events");
        test.Store.CreateWatchAlerts(WorldNewsTestStore.Now, _ => true).Should().BeEmpty("already alerted");
    }

    [Fact]
    public void FactChecks_ShouldBeShownWithTheirVerdict_AndNotCountAsSources()
    {
        var reports = new[]
        {
            Report("Reuters", "Video shows flooded airport"),
            Report("The Guardian", "Airport flooded, officials say"),
            Report("Snopes", "Fact Check: Viral video of flooded airport is from 2019 - Miscaptioned"),
            Report("FactCheck.org", "What we know about the airport video"),
        };
        var checks = FactChecks.In(reports);
        checks.Select(c => (c.Outlet, c.Verdict)).Should().Equal(("Snopes", "Miscaptioned"), ("FactCheck.org", (string?)null));
        FactChecks.DisputingVerdict(checks).Should().Be("Miscaptioned");
        SourceIndependence.Assess(reports).Independent.Should().Be(2, "fact-checkers rate claims, they don't report them");
        FactChecks.VerdictOf("PolitiFact: Says the moon is cheese. Pants on Fire!").Should().Be("Pants On Fire");
    }

    [Theory]
    [InlineData("floods brazil", "\"floods\" \"brazil\"")]
    [InlineData("\"rescue under way\" OR evacuat*", "\"rescue under way\" OR \"evacuat\"*")]
    [InlineData("quake NOT tsunami", "\"quake\" NOT \"tsunami\"")]
    [InlineData("title:hack ^boom \"unclosed", "\"titlehack\" \"boom\" \"unclosed\"")]
    [InlineData("Ivanti CVE-2025-0282", "\"Ivanti\" \"CVE 2025 0282\"")]
    [InlineData("AND OR", null)]
    [InlineData("   ", null)]
    public void FullTextQuery_ShouldReadWhatPeopleType_AndNeverError(string text, string? expected) =>
        FullTextQuery.From(text).Should().Be(expected);

    [Fact]
    public void Store_ShouldSearchHeadlinesAndDescriptions_WithPhrasesAndBooleans()
    {
        using var test = new WorldNewsTestStore();
        test.Store.AddArticles(
        [
            Report("Reuters", "Floods kill 10 in southern Brazil", "Rescue teams are under way in Porto Alegre.", minutes: -10),
            Report("BBC", "Brazil election runoff", minutes: -20),
            Report("DW", "Flooding in Germany", "Evacuations ordered along the Rhine.", minutes: -30),
        ]);
        test.Store.FullTextReady().Should().BeTrue("a small database is indexed straight away");

        string[] Titles(string q) => test.Store.RecentReports(Now.AddDays(-1), text: q).Select(r => r.Title).ToArray();
        Titles("brazil").Should().HaveCount(2);
        Titles("brazil NOT election").Should().Equal("Floods kill 10 in southern Brazil");
        Titles("\"teams are under way\"").Should().Equal("Floods kill 10 in southern Brazil");
        Titles("flood*").Should().HaveCount(2);
        Titles("evacuations OR runoff").Should().HaveCount(2);
        Titles("\"").Should().HaveCount(3, "nothing to search for means no filter");
    }

    [Fact]
    public void Watchlist_ShouldMatchConfirmedStoriesOnce_AndRefuseEmptyQueries()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        store.AddKeywordWatch("ann", "  \"  ", WorldNewsTestStore.Now).Should().BeNull("nothing to search for");
        var watch = store.AddKeywordWatch("ann", "Brazil NOT election", WorldNewsTestStore.Now.AddDays(-1))!;
        store.AddKeywordWatch("ann", "brazil not election", WorldNewsTestStore.Now)!.Id.Should().Be(watch.Id, "the same watch isn't added twice");
        store.AddKeywordWatch("ben", "Japan", WorldNewsTestStore.Now.AddDays(-1));
        test.SeedSituation("sit-floods", "Floods in Brazil", "Rescue under way in Brazil");
        test.SeedSituation("sit-vote", "Brazil election runoff", "Brazil election result");

        store.CreateKeywordAlerts(WorldNewsTestStore.Now, _ => false).Should().BeEmpty("held back until confirmed");
        var alerts = store.CreateKeywordAlerts(WorldNewsTestStore.Now, _ => true);
        alerts.Should().ContainSingle();
        alerts[0].Username.Should().Be("ann");
        alerts[0].SituationId.Should().Be("sit-floods");
        alerts[0].Message.Should().Be("Matches your watchlist: Brazil NOT election");
        store.CreateKeywordAlerts(WorldNewsTestStore.Now, _ => true).Should().BeEmpty("one alert per story");

        store.RemoveKeywordWatch("ben", watch.Id);
        store.KeywordWatches("ann").Should().ContainSingle("only the owner can remove a watch");
        store.RemoveKeywordWatch("ann", watch.Id);
        store.KeywordWatches("ann").Should().BeEmpty();
    }

    [Fact]
    public void Silence_ShouldFlagACountryFarBelowItsUsualCoverage_AndIgnoreSmallOnes()
    {
        var hours = new List<(string, DateTimeOffset, int)>();
        for (var h = 1; h <= 7 * 24 + 6; h++)
        {
            var hour = Now.AddHours(-h);
            var recent = h <= 6;
            hours.Add(("Iran", hour, recent ? 0 : 10));      // usual 60 per 6 h, now nothing
            hours.Add(("France", hour, 10));                  // steady
            hours.Add(("Bhutan", hour, recent ? 0 : 1));      // too little to judge
        }
        var silences = CoverageSilenceRules.Find(hours, Now);
        silences.Should().ContainSingle().Which.Should().Be(new CoverageSilence("Iran", 0, 60));

        using var test = new WorldNewsTestStore();
        test.Store.AddArticles([Report("Reuters", "Floods in Brazil", minutes: -30), Report("BBC", "Election in Brazil", minutes: -40)]);
        test.Store.CountryHours(Now.AddDays(-1)).Sum(row => row.Reports).Should().Be(2, "each new report is counted once");
        var once = Report("Reuters", "Floods in Brazil", url: "https://dup.example/1");
        test.Store.AddArticles([once, once]);
        test.Store.CountryHours(Now.AddDays(-1)).Sum(row => row.Reports).Should().Be(3, "a report already stored isn't counted again");
    }

    [Fact]
    public void PrimarySources_ShouldMatchTheSameKindOfEventNearbyAndInTime()
    {
        var quake = new PrimarySignal("USGS", "earthquake", "M 6.1 - 20 km SW of Kushiro, Japan", 42.9, 144.2, Now.AddHours(-1), "https://earthquake.usgs.gov/x");
        var far = quake with { Latitude = 35.6, Longitude = -120.0, Title = "M 4.0 - California" };
        var old = quake with { ObservedAt = Now.AddDays(-5) };
        var storm = new PrimarySignal("GDACS", "cyclone", "Typhoon PENA", 20, 125, Now, "https://gdacs.org/y");

        PrimarySources.Match("Strong earthquake shakes northern Japan", 36.2, 138.25, Now, [far, old, quake], PrimarySources.CountryKilometres)!
            .Title.Should().Contain("Kushiro", "a story placed by country reaches Hokkaido");
        PrimarySources.Match("Strong earthquake shakes northern Japan", 36.2, 138.25, Now, [quake]).Should().BeNull("a city-placed story must be close");
        PrimarySources.Match("Strong earthquake shakes northern Japan", 36.2, 138.25, Now, [far, old], PrimarySources.CountryKilometres).Should().BeNull();
        PrimarySources.Match("Typhoon nears the Philippines", 12.9, 121.8, Now, [storm, quake], PrimarySources.CountryKilometres)!.Source.Should().Be("GDACS");
        PrimarySources.Match("Election results in Japan", 36.2, 138.25, Now, [quake]).Should().BeNull("not a hazard story");
        PrimarySources.InReports([Report(WhoOutbreakNews.Outlet, "Marburg virus disease - Rwanda")]).Should().Be("the World Health Organization");
        PrimarySources.KindOf("Flash flooding hits Valencia").Should().Be("flood");
        PrimarySources.KindOf("Volcano erupts in Iceland").Should().Be("volcano");
    }

    [Theory]
    [InlineData("Floods kill 10 in southern Brazil as rescuers search", "Floods kill southern Brazil rescuers")]
    [InlineData("Live updates: Typhoon Pena makes landfall in the Philippines", "Typhoon Pena makes landfall Philippines")]
    [InlineData("Breaking news", null)]
    public void FollowUp_ShouldSearchForAHeadlinesDistinctiveWords(string headline, string? query) =>
        FollowUpQueries.From(headline).Should().Be(query);

    [Fact]
    public void Discovery_ShouldFindAdvertisedFeeds_ThenTheUsualPlaces()
    {
        var html = """
            <html><head>
              <link rel="alternate" type="application/rss+xml" title="Top stories" href="/rss/top.xml">
              <link type="application/atom+xml" rel="alternate" href="https://cdn.outlet.example/atom?x=1&amp;y=2">
              <link rel="stylesheet" href="/site.css">
            </head></html>
            """;
        var candidates = FeedDiscovery.Candidates(html, new Uri("https://www.outlet.example/news/"));
        candidates.Take(2).Should().Equal("https://www.outlet.example/rss/top.xml", "https://cdn.outlet.example/atom?x=1&y=2");
        candidates.Should().Contain("https://www.outlet.example/feed").And.Contain("https://www.outlet.example/news-sitemap.xml");
        candidates.Should().NotContain(url => url.EndsWith(".css"));
    }

    [Fact]
    public void FeedState_ShouldReadAsItLooks()
    {
        var outlet = new NewsOutlet("X", "English", "Kenya", "https://x.example/rss");
        new CrawlFeed(outlet, null, false, false).State.Should().Be("not read yet");
        new CrawlFeed(outlet, new FeedHealth(outlet.Url, LastStatus: 200), false, false).State.Should().Be("ok");
        new CrawlFeed(outlet, new FeedHealth(outlet.Url, LastStatus: 403, Failures: 1), false, false).State.Should().Be("refused");
        new CrawlFeed(outlet, new FeedHealth(outlet.Url, LastStatus: 500, Failures: 2), false, false).State.Should().Be("failing");
        new CrawlFeed(outlet, new FeedHealth(outlet.Url, LastStatus: 304, SkipUntil: Now), false, false).State.Should().Be("quiet");
        new CrawlFeed(outlet, new FeedHealth(outlet.Url, LastStatus: 200), true, false).State.Should().Be("paused");
    }

    [Fact]
    public void Store_ShouldKeepAddedAndPausedFeeds_AndOutletRecords()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        var feed = new NewsOutlet("Daily Nation", "English", "Kenya", "https://nation.example/rss");
        store.AddCustomFeed(feed, "grant", Now);
        store.CustomFeeds().Should().ContainSingle().Which.Should().Be(feed);
        store.SetFeedPaused(feed.Url, true);
        store.PausedFeeds().Should().Contain(feed.Url);
        store.RemoveCustomFeed(feed.Url);
        store.CustomFeeds().Should().BeEmpty();
        store.PausedFeeds().Should().BeEmpty("removing a feed clears its pause");

        var first = Report("Reuters", "Floods kill 10 in southern Brazil", url: "https://reuters.example/a", minutes: -50);
        store.AddArticles([first, Report("Reuters", "Brazil runoff", minutes: -40), Report("BBC", "Brazil votes", minutes: -30)]);
        store.AddArticles([first with { Title = "Correction: Floods kill 12 in southern Brazil", SeenAt = Now }]);
        var records = store.OutletRecords(Now.AddDays(-7));
        records.First().Should().Be(new OutletRecord("Reuters", 2, 0, 1, 1));
        records.Should().Contain(new OutletRecord("BBC", 1, 0, 0, 0));
    }

    [Fact]
    public void WikipediaCurrentEvents_ShouldReadEachCitedEntry_AsASignalNotASource()
    {
        var html = """
            <div class="current-events-main vevent"><span class="bday dtstart published updated itvstart">2026-11-12</span>
            <div class="current-events-content description"><p><b>Armed conflicts and attacks</b></p>
            <ul><li><a rel="mw:WikiLink" href="./Haitian_conflict">Haitian conflict</a>
              <ul><li>Soldiers take control of the <a rel="mw:WikiLink" href="./Stade">Stade Sylvio Cator</a> in Port-au-Prince , Haiti , after two years.
                <a rel="mw:ExtLink nofollow" href="https://www.france24.com/en/americas/stadium" class="external text">(France 24)</a></li></ul></li>
            <li>An entry with only Wikipedia links <a rel="mw:ExtLink" href="https://en.wikipedia.org/w/index.php?title=x">edit</a></li>
            <li>Short <a rel="mw:ExtLink nofollow" href="https://example.com/a">(X)</a></li></ul></div></div>
            """;
        var entries = WikipediaCurrentEvents.Parse(html, Now);
        entries.Should().ContainSingle();
        entries[0].Title.Should().Be("Soldiers take control of the Stade Sylvio Cator in Port-au-Prince, Haiti, after two years.");
        entries[0].Url.Should().Be("https://www.france24.com/en/americas/stadium");
        entries[0].SeenAt.Should().Be(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        SourceIndependence.Assess([entries[0], Report("Reuters", "Haiti stadium retaken")]).Independent.Should().Be(1, "Wikipedia summarises others");
    }

    [Fact]
    public void Independence_ShouldNeverExceedTheOutletCount()
    {
        var reports = new[]
        {
            Report("Local Paper", "Mayor opens new bridge"),
            Report("Local Paper", "Floods kill 10 (AP)"),
            Report("Other Paper", "Floods kill 10 (AP)", minutes: 2),
        };
        var independence = SourceIndependence.Assess(reports);
        independence.Outlets.Should().Be(2);
        independence.Independent.Should().Be(2, "Local Paper's own report makes it its own source; Other Paper only repeated AP");
        independence.Origins.Select(o => o.Name).Should().BeEquivalentTo(["Local Paper", "Associated Press"]);
    }

    [Theory]
    [InlineData("The minister told Reuters the talks had failed", "Reuters")]
    [InlineData("Citing AP, the paper said ten died", "Associated Press")]
    [InlineData("Officials told AFP the border was shut", "AFP")]
    [InlineData("Reuters reported the deal on Tuesday", "Reuters")]
    public void Origin_ShouldFollowWireAttributions(string snippet, string origin) =>
        SourceIndependence.OriginOf(Report("Some Outlet", "Talks collapse", snippet)).Name.Should().Be(origin);

    [Fact]
    public void CitedSources_ShouldShowWhoTheCoverageReliesOn()
    {
        var reports = new[]
        {
            Report("Reuters", "Floods kill 10", "Ten people died, according to the Health Ministry of Brazil."),
            Report("BBC", "Brazil floods", "The death toll rose to 12, according to the Health Ministry of Brazil.", minutes: 5),
            Report("DW", "Brazil floods", "Local police said in a statement that roads were closed.", minutes: 9),
            Report("CNN", "Floods", "According to them, it was bad.", minutes: 12),
        };
        var cited = CitedSources.Find(reports);
        cited.Select(c => (c.Name, c.Outlets, c.FirstOutlet)).Should().Equal(
            ("health ministry of brazil", 2, "Reuters"),
            ("police", 1, "DW"));
    }

    [Fact]
    public void RumourClock_ShouldCallAStoryUnconfirmedAfterTwelveHoursOnOneSource()
    {
        var single = new StoryIndependence(3, [new SourceOrigin("Reuters", "wire", ["Reuters", "Yahoo", "MSN"])]);
        StoryConfidenceRules.Assess(single, firstSeen: Now.AddHours(-13), now: Now).Reason.Should().Be("Still one source after 13 hours: unconfirmed.");
        StoryConfidenceRules.Assess(single, firstSeen: Now.AddHours(-2), now: Now).Reason.Should().Be("One source, repeated by 3 outlets.");
        StoryConfidenceRules.RulesVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Ledger_ShouldChainLabelChanges_AndCatchARewrite()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        store.AppendLabelSnapshots([("e1", "SingleSource", "One outlet so far.", 1, 1), ("e2", "Reported", "2 independent sources.", 2, 4)], "v1", Now).Should().Be(2);
        store.AppendLabelSnapshots([("e1", "SingleSource", "One outlet so far.", 1, 1)], "v1", Now.AddHours(1)).Should().Be(0, "unchanged labels aren't recorded again");
        store.AppendLabelSnapshots([("e1", "Confirmed", "3 independent sources.", 3, 5)], "v1", Now.AddHours(2)).Should().Be(1);

        var snapshots = store.LabelSnapshots();
        snapshots.Should().HaveCount(3);
        snapshots[0].PreviousHash.Should().Be(LabelLedger.Genesis);
        LabelLedger.FirstBroken(snapshots).Should().BeNull();
        LabelLedger.FirstBroken([snapshots[0], snapshots[1] with { Level = "Confirmed" }, snapshots[2]]).Should().Be(snapshots[1].Id, "a rewritten label breaks the chain");
        LabelLedger.FirstBroken([snapshots[0], snapshots[2]]).Should().Be(snapshots[2].Id, "a removed entry breaks it too");
    }

    [Fact]
    public void Calibration_ShouldScoreHowFirstLabelsHeldUp()
    {
        var rows = Calibration.Score(
        [
            ("Confirmed", "Confirmed", false), ("Confirmed", "Disputed", true), ("Confirmed", "Confirmed", false), ("Confirmed", "Confirmed", false),
            ("SingleSource", "Reported", false), ("SingleSource", "SingleSource", false), ("SingleSource", "Disputed", false),
        ]);
        rows.Select(r => (r.Level, r.Stories, r.Held, r.BecameDisputed, r.Corrected, r.HeldPercent)).Should().Equal(
            ("Confirmed", 4, 3, 1, 1, 75), ("SingleSource", 3, 2, 1, 0, 67));
    }

    [Fact]
    public void Challenges_ShouldBeRecordedAndResolvedOnce()
    {
        using var test = new WorldNewsTestStore();
        var challenge = test.Store.AddChallenge("e1", "ann", " The toll was revised to 12. ", "https://ministry.example/statement", Now);
        test.Store.Challenges("e1").Should().ContainSingle().Which.Note.Should().Be("The toll was revised to 12.");
        test.Store.ResolveChallenge(challenge.Id, true, "grant", "Confirmed by the ministry's statement.", Now.AddHours(1)).Should().BeTrue();
        test.Store.ResolveChallenge(challenge.Id, false, "grant", "Changed my mind", Now.AddHours(2)).Should().BeFalse("a resolved challenge stays resolved");
        var resolved = test.Store.Challenges().Single();
        resolved.Status.Should().Be(LabelChallengeStatuses.Upheld);
        resolved.ResolvedBy.Should().Be("grant");
    }

    [Fact]
    public void Balance_AndRecall_ShouldMeasureWhoLeadsAndWhatWasMissed()
    {
        var balance = CoverageBalance.Compute(
            [("Sub-Saharan Africa", "Europe"), ("Sub-Saharan Africa", "Sub-Saharan Africa"), ("Sub-Saharan Africa", "North America"), ("Europe", "Europe")],
            new Dictionary<string, int> { ["Europe"] = 70, ["North America"] = 25, ["Sub-Saharan Africa"] = 5 });
        var africa = balance.Single(r => r.Region == "Sub-Saharan Africa");
        africa.StoryShare.Should().Be(0.75);
        africa.ReportShare.Should().Be(0.05);
        africa.LocalLeadPercent.Should().Be(33);
        balance[0].Region.Should().Be("Sub-Saharan Africa", "ordered by share of stories");

        var quake = new PrimarySignal("USGS", "earthquake", "M6.2 Kermadec Islands", -29.5, -177.9, Now.AddHours(-30), "u1");
        var missedQuake = new PrimarySignal("USGS", "earthquake", "M5.6 Mid-Atlantic Ridge", 0.5, -25.0, Now.AddHours(-20), "u2");
        var recall = Recall.Score("USGS", "earthquake", [quake, missedQuake],
            [("Strong earthquake strikes near New Zealand's Kermadec Islands", -29.0, -177.0, Now.AddHours(-29)), ("Election in Brazil", -15, -47, Now)]);
        recall.Found.Should().Be(1);
        recall.FoundPercent.Should().Be(50);
        recall.MedianDelay.Should().Be(TimeSpan.FromHours(1));
        recall.Missed.Should().Equal("M5.6 Mid-Atlantic Ridge");
    }

    [Fact]
    public void Ownership_ShouldGroupOutletsSharingAnOwner_AndCountThemOnce()
    {
        var groups = OwnershipGraph.Build(
        [
            ("Paper Alpha", "Acme Media"), ("Paper Beta", "Acme Media"), ("Paper Beta", "Acme Holdings"), ("Paper Gamma", "Acme Holdings"),
            ("Paper Delta", "Delta Trust"),
        ]);
        groups["Paper Alpha"].Should().Be(groups["Paper Gamma"], "Alpha and Gamma are linked through Beta's two owners");
        groups["Paper Delta"].Should().Be("Delta Trust");

        OwnershipGraph.RootDomain("https://rss.nytimes.com/services/xml/rss/nyt/World.xml").Should().Be("nytimes.com");
        OwnershipGraph.RootDomain("https://feeds.bbci.co.uk/news/world/rss.xml").Should().Be("bbci.co.uk");
        OwnershipGraph.RootDomain("http://www.guardian.bz").Should().Be("guardian.bz");

        var before = SourceIndependence.ImportedOwners;
        try
        {
            SourceIndependence.ImportedOwners = groups;
            SourceIndependence.Assess([Report("Paper Alpha", "Port strike"), Report("Paper Gamma", "Dock workers walk out"), Report("Paper Delta", "Strike at the port")])
                .Independent.Should().Be(2, "Alpha and Gamma share an owner");
        }
        finally
        {
            SourceIndependence.ImportedOwners = before;
        }
    }
}
