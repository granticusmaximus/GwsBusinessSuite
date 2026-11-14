using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News search: merging meaning and words, CVEs, cyber queries, and the store's word matches.
public sealed class WorldNewsFinderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wn-find-").FullName;

    [Fact]
    public void Merge_ShouldRankBySimilarity_LiftedByMatchingReports_ThenWordOnlyStories()
    {
        var merged = WorldNewsFinder.Merge(
            [("close", 0.80), ("near", 0.70), ("loose", 0.50)],
            [("near", 30), ("words-only", 12), ("loose", 1)]);

        Assert.Equal(["near", "close", "loose", "words-only"], merged.Select(m => m.EventId));
        Assert.Equal(30, merged[0].Reports);
        Assert.Null(merged[^1].Similarity);
    }

    [Fact]
    public void Cves_ShouldFindEachCveOnce_InOrder()
    {
        Assert.Equal(["CVE-2025-0282", "CVE-2026-12345"],
            WorldNewsFinder.Cves(["Ivanti flaw cve-2025-0282 exploited", null, "Patch CVE-2026-12345 and CVE-2025-0282 now"]));
    }

    [Theory]
    [InlineData("hacked", "hacked*")]
    [InlineData("ransomware at a hospital", "ransomware* at a hospital*")]
    [InlineData("Ivanti cve-2025-0282", "Ivanti* \"CVE-2025-0282\"")]
    [InlineData("\"port strike\" OR walkout", "\"port strike\" OR walkout")]
    public void WordQuery_ShouldMatchLongerFormsAndKeepCvesWhole(string query, string expected) =>
        Assert.Equal(expected, WorldNewsFinder.WordQuery(query));

    [Fact]
    public void EventsMatchingText_ShouldFindHackersForHack_AndACveById()
    {
        var store = new WorldNewsStore(Path.Combine(_folder, "worldnews.db"));
        var now = DateTimeOffset.UtcNow;
        WorldArticle A(string id, string title) => new(id, $"https://x.example/{id}", title, "Outlet " + id, "English", "United States", now, "test");
        store.AddArticles([A("1", "Hackers release party emails"), A("2", "Ivanti flaw CVE-2025-0282 exploited in the wild")]);
        store.EnsureFullTextIndex();
        Assign("e-hack", "1");
        Assign("e-cve", "2");

        Assert.Equal("e-hack", Assert.Single(store.EventsMatchingText(WorldNewsFinder.WordQuery("hack"), now.AddDays(-1), 10)).EventId);
        Assert.Equal("e-cve", Assert.Single(store.EventsMatchingText(WorldNewsFinder.WordQuery("CVE-2025-0282"), now.AddDays(-1), 10)).EventId);
    }

    [Theory]
    [InlineData("ransomware at a hospital", "ransomware hospital")]
    [InlineData("\"port strike\" OR walkout", "(\"port strike\" OR walkout)")]
    [InlineData("Ivanti cve-2025-0282", "Ivanti \"CVE-2025-0282\"")]
    [InlineData("a an to", null)]
    [InlineData("OR hack OR", "hack")]
    public void GdeltQuery_ShouldDropShortWordsAndBracketOrs(string query, string? expected) =>
        Assert.Equal(expected, WorldNewsFinder.GdeltQuery(query));

    [Theory]
    [InlineData("ransomware hospital", true)]
    [InlineData("data breach at airline", true)]
    [InlineData("CVE-2026-1234", true)]
    [InlineData("port strike", false)]
    [InlineData("election results", false)]
    public void IsCyberQuery_ShouldSpotCyberWords(string query, bool expected) =>
        Assert.Equal(expected, WorldNewsFinder.IsCyberQuery(query));

    [Fact]
    public void CyberStatusOf_ShouldGiveCyberStoriesAStatus_ClassifiedOrPlainFromTheHeadline()
    {
        Assert.Equal(CyberStatus.Claimed, WorldNewsFinder.CyberStatusOf(SituationKinds.Cyber, ["Gang claims attack on city council, threatens to leak data"]));
        Assert.Equal(CyberStatus.Reported, WorldNewsFinder.CyberStatusOf(null, ["Germany news: Hackers release AfD party emails"]));
        Assert.Null(WorldNewsFinder.CyberStatusOf("disaster", ["Gang claims attack"]));
        Assert.Null(WorldNewsFinder.CyberStatusOf(null, ["Houthis claim attacks on Saudi oil pipeline"]));
    }

    [Theory]
    [InlineData("Ransomware gang hits hospital chain", true)]
    [InlineData("Cyberattack disrupts airport check-in", true)]
    [InlineData("Patch now: CVE-2026-1234 under attack", true)]
    [InlineData("Phone outage hits millions", false)]
    [InlineData("Stock market rally continues", false)]
    public void IsCyberHeadline_ShouldBeStricterThanAQuery(string headline, bool expected) =>
        Assert.Equal(expected, WorldNewsFinder.IsCyberHeadline(headline));

    [Fact]
    public void IsStrong_ShouldNeedAWordMatchOrACloseMeaning()
    {
        var e = new EventOverview("e", "t", 1, 1, [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(new FoundStory(e, 0.66, 0, null, null, []).IsStrong);
        Assert.True(new FoundStory(e, null, 2, null, null, []).IsStrong);
        Assert.False(new FoundStory(e, 0.48, 0, null, null, []).IsStrong);
    }

    [Fact]
    public void EventsMatchingText_ShouldGroupMatchingReportsByStory()
    {
        var store = new WorldNewsStore(Path.Combine(_folder, "worldnews.db"));
        var now = DateTimeOffset.UtcNow;
        WorldArticle A(string id, string title) => new(id, $"https://x.example/{id}", title, "Outlet " + id, "English", "United States", now, "test");
        store.AddArticles([A("1", "Ransomware hits regional hospital chain"), A("2", "Hospital ransomware attack disrupts surgeries"), A("3", "Port strike enters third day")]);
        store.EnsureFullTextIndex();
        Assign("e-ransom", "1", "2");
        Assign("e-port", "3");

        var hits = store.EventsMatchingText("ransomware hospital", now.AddDays(-1), 10);

        var hit = Assert.Single(hits);
        Assert.Equal(("e-ransom", 2), hit);
        Assert.DoesNotContain(store.EventsMatchingText("\"unclosed", now.AddDays(-1), 10), h => h.EventId == "e-port");
    }

    private void Assign(string eventId, params string[] articleIds)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder, "worldnews.db")}");
        connection.Open();
        foreach (var id in articleIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE articles SET event_id = $e WHERE id = $id";
            command.Parameters.AddWithValue("$e", eventId);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
