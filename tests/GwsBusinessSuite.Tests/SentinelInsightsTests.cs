using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel items 21 (tags), 22 (page analytics) and 23 (knowledge gaps).
public sealed class SentinelInsightsTests
{
    private static WikiBlock Block(string text, string type = WikiBlockTypes.Paragraph, string? link = null) =>
        new(Guid.NewGuid(), type, 0, [new WikiRichTextSpan(text, Link: link)], new Dictionary<string, string>());

    [Fact]
    public void Tags_ShouldBeFoundInText_Nested_AndUsableInSearch()
    {
        SentinelTags.FromBlocks([
            Block("Kickoff for #Client/Acme and #urgent. Not C# or #1 or a#b."),
            Block("#ignored", WikiBlockTypes.Code),
            Block("#linked", link: "https://x.example"),
            Block("Repeat #urgent")
        ]).Should().Equal("client/acme", "urgent");
        SentinelTags.Normalize(" #Team//Ops/ ").Should().Be("team/ops");
        SentinelTags.Normalize("bad tag").Should().BeNull();
        SentinelTags.IsUnder("client/acme", "client").Should().BeTrue();
        SentinelTags.IsUnder("clientele", "client").Should().BeFalse();
        var (tags, remainder) = SentinelTags.SplitQuery("#client invoice  due");
        tags.Should().Equal("client");
        remainder.Should().Be("invoice due");
    }

    [Fact]
    public void Gaps_ShouldGroupQuestionsSharingAKeyWord()
    {
        var now = DateTimeOffset.UtcNow;
        var groups = SentinelGaps.Group([
            new(Guid.NewGuid(), "How do refunds work?", ["refunds", "work"], now.AddHours(-3)),
            new(Guid.NewGuid(), "Refunds for annual plans?", ["refunds", "annual", "plans"], now.AddHours(-1)),
            new(Guid.NewGuid(), "Where is the VPN guide?", ["vpn", "guide"], now.AddHours(-2))
        ]);

        groups.Should().HaveCount(2);
        groups[0].Topic.Should().StartWith("Refunds");
        groups[0].Asks.Should().Be(2);
        groups[0].Questions.Should().Equal("Refunds for annual plans?", "How do refunds work?");
    }

    [Fact]
    public async Task Insights_ShouldSyncTagsOnSave_CountViews_FindNeverRead_AndTurnGapsIntoPages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var wiki = new WikiService(db);
        var access = new SentinelAccessService(db);
        var insights = new SentinelInsightsService(db, wiki, TimeProvider.System, access);

        var kickoff = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Kickoff", BlocksJson = WikiBlockJson.Serialize([Block("Plan for #client/acme invoice")]) }, "grant");
        var other = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Other", BlocksJson = WikiBlockJson.Serialize([Block("invoice notes")]) }, "grant");
        (await insights.GetPageTagsAsync(kickoff.Id)).TextTags.Should().Equal("client/acme");
        await insights.AddManualTagAsync(other.Id, "#Client", "grant");
        await insights.Invoking(i => i.AddManualTagAsync(other.Id, "x", "sam")).Should().ThrowAsync<UnauthorizedAccessException>();

        (await insights.ListTagsAsync("grant")).Should().Equal(new SentinelTagCount("client", 2), new SentinelTagCount("client/acme", 1));
        (await insights.ListTaggedPagesAsync("client/acme", "grant")).Select(p => p.Title).Should().Equal("Kickoff");
        (await insights.ListTagsAsync("sam")).Should().BeEmpty("sam can't see either page");
        var workspace = new SentinelWorkspaceService(db, TimeProvider.System, access);
        (await workspace.SearchAsync("#client", "grant")).Select(r => r.Title).Should().BeEquivalentTo("Kickoff", "Other");
        (await workspace.SearchAsync("#client/acme invoice", "grant")).Select(r => r.Title).Should().Equal("Kickoff");

        // Re-saving without the #tag drops it.
        var saved = await db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == kickoff.Id);
        await wiki.SavePageAsync(new WikiPageEditorModel { WikiPageId = kickoff.Id, ExpectedContentVersion = saved.ContentVersion, Title = "Kickoff", Slug = saved.Slug,
            BlocksJson = WikiBlockJson.Serialize([Block("Plan, no tags")]), BaseBlocksJson = saved.BlocksJson }, "grant");
        (await insights.GetPageTagsAsync(kickoff.Id)).TextTags.Should().BeEmpty();

        await insights.RecordViewAsync(kickoff.Id, "grant");
        await insights.RecordViewAsync(kickoff.Id, "grant");
        await insights.RecordViewAsync(kickoff.Id, "sam");
        (await insights.GetViewStatsAsync(kickoff.Id)).Should().Be(new SentinelPageViewStats(2, 2, 30), "a repeat within half an hour isn't counted");
        (await insights.ListMostReadAsync("grant")).Select(p => p.Title).Should().Equal("Kickoff");

        var old = await db.WikiPages.SingleAsync(p => p.Id == other.Id);
        old.CreatedAt = DateTimeOffset.UtcNow.AddDays(-120);
        old.OwnerUsername = "grant";
        await db.SaveChangesAsync();
        (await insights.ListNeverReadAsync("grant")).Select(p => p.Title).Should().Equal("Other");
        var review = await new SentinelPageVerificationService(db, TimeProvider.System, access).ListNeedsReviewAsync("grant");
        review.Should().Contain(i => i.PageId == other.Id && i.Reason == SentinelNeedsReviewReasons.NeverRead);

        db.SentinelQuestions.AddRange(
            new SentinelQuestion { Username = "sam", Question = "How do refunds work?", Terms = "refunds work", NotFound = true, AskedUnix = 100, CreatedBy = "sam" },
            new SentinelQuestion { Username = "grant", Question = "Refund timing?", Terms = "refunds timing", NotFound = true, AskedUnix = 200, CreatedBy = "grant" },
            new SentinelQuestion { Username = "grant", Question = "Answered one", Terms = "answered", NotFound = false, AskedUnix = 300, CreatedBy = "grant" });
        await db.SaveChangesAsync();
        (await insights.ListKnowledgeGapsAsync("sam")).Single().Questions.Should().Equal("How do refunds work?");
        var gap = (await insights.ListKnowledgeGapsAsync("grant")).Single();
        gap.Asks.Should().Be(2);

        var pageId = await insights.WriteGapPageAsync(gap, "grant");
        var written = await db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == pageId);
        written.Title.Should().Be(gap.Topic);
        WikiBlockJson.ParseBlocks(written.BlocksJson).Select(b => b.PlainText).Should().Contain(["Refund timing?", "How do refunds work?"]);
        (await insights.ListKnowledgeGapsAsync("grant")).Should().BeEmpty("those questions are resolved now");
    }
}
