using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel item 24: suggested edits.
public sealed class SentinelSuggestionTests
{
    private static WikiBlock Block(string text, Guid? id = null, string type = WikiBlockTypes.Paragraph, IReadOnlyDictionary<string, string>? props = null) =>
        new(id ?? Guid.NewGuid(), type, 0, [new WikiRichTextSpan(text)], props ?? new Dictionary<string, string>());

    private static string Texts(IEnumerable<WikiBlock> blocks) => string.Join(" | ", blocks.Select(block => block.PlainText));

    [Fact]
    public void Diff_ShouldFindInsertsReplacesAndDeletes_ButNotMoves()
    {
        var (a, b, c, d) = (Block("Alpha"), Block("Bravo"), Block("Charlie"), Block("Delta"));
        var inserted = Block("New after bravo");
        var edited = new[] { b, a with { RichText = [new("Alpha, reworded")] }, inserted, d };

        var changes = SentinelSuggestions.Diff([a, b, c, d], edited, [a, b, c, d]);

        changes.Select(change => (change.Kind, change.BlockId)).Should().Equal(
            (SentinelSuggestionKinds.Replace, a.Id), (SentinelSuggestionKinds.Insert, inserted.Id), (SentinelSuggestionKinds.Delete, c.Id));
        changes[0].Before!.PlainText.Should().Be("Alpha");
        changes[0].Proposed!.PlainText.Should().Be("Alpha, reworded");
        changes[1].Anchors.Should().Equal(a.Id, b.Id);
        SentinelSuggestions.Diff([a, b], [b, a], [a, b]).Should().BeEmpty("moving a block isn't a suggested change");
    }

    [Fact]
    public void Diff_ShouldCompareWithTheEditorsOwnCopy_SoUntouchedBlocksAreNeverChanges()
    {
        // The stored page wrote a page link without the icon the editor fills in.
        var id = Guid.NewGuid();
        var stored = Block("Roadmap", id, WikiBlockTypes.PageLink, new Dictionary<string, string> { ["pageId"] = "x" });
        var editorCopy = stored with { Props = new Dictionary<string, string> { ["pageId"] = "x", ["pageIcon"] = "📄" } };

        SentinelSuggestions.Diff([editorCopy], [editorCopy], [stored]).Should().BeEmpty();
        var change = SentinelSuggestions.Diff([editorCopy], [], [stored]).Should().ContainSingle().Subject;
        change.Before.Should().Be(stored, "Before is the block as the page actually stores it");
    }

    [Fact]
    public void Apply_ShouldWorkOnThePageAsItIsNow_AndToleratePartialAcceptance()
    {
        var (a, b, c) = (Block("A"), Block("B"), Block("C"));
        var first = Block("first new");
        var second = Block("second new");
        var changes = SentinelSuggestions.Diff([a, b, c], [a, first, second, b with { RichText = [new("B2")] }], [a, b, c]);
        var insertFirst = changes.Single(change => change.BlockId == first.Id);
        var insertSecond = changes.Single(change => change.BlockId == second.Id);
        var replaceB = changes.Single(change => change.BlockId == b.Id);
        var deleteC = changes.Single(change => change.BlockId == c.Id);

        // Someone added X at the top since; only the second insert and the delete are accepted.
        var x = Block("X");
        var page = SentinelSuggestions.Apply([x, a, b, c], [insertSecond, deleteC]);
        Texts(page).Should().Be("X | A | second new | B", "with 'first new' rejected, the second goes after the next block back that's there");

        page = SentinelSuggestions.Apply(page, [replaceB, insertSecond, deleteC]);
        Texts(page).Should().Be("X | A | second new | B2", "accepting again doesn't add or remove twice");

        SentinelSuggestions.Apply([b], [insertFirst]).Select(block => block.Id).Should().Equal(first.Id, b.Id);
        SentinelSuggestions.IsOutdated(replaceB, [a, b with { RichText = [new("edited meanwhile")] }]).Should().BeTrue();
        SentinelSuggestions.IsOutdated(replaceB, [a, b]).Should().BeFalse();
    }

    [Fact]
    public void TextDiff_ShouldMarkAddedAndRemovedWords()
    {
        var parts = SentinelSuggestions.TextDiff("The quick fox jumps", "The slow brown fox jumps");
        string.Concat(parts.Select(p => p.Removed ? $"[-{p.Text}]" : p.Added ? $"[+{p.Text}]" : p.Text))
            .Should().Be("The [-quick ][+slow brown ]fox jumps");
        string.Concat(SentinelSuggestions.TextDiff("Welcome to the team.", "Welcome to the team. Glad you're here.")
                .Select(p => p.Removed ? $"[-{p.Text}]" : p.Added ? $"[+{p.Text}]" : p.Text))
            .Should().Be("Welcome to the team. [+Glad you're here.]", "a word that only gained a space after it is unchanged");
    }

    private sealed record Fixture(ApplicationDbContext Db, SentinelSuggestionService Service, WikiPage Page, WikiBlock Intro, WikiBlock Steps, WikiBlock Old);

    private static async Task<Fixture> FixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author },
            new AppUser { Username = "pat", Role = AppRoles.Author }, new AppUser { Username = "lee", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var wiki = new WikiService(db);
        var (intro, steps, old) = (Block("Welcome to the team."), Block("Step one: get a laptop."), Block("Outdated paragraph."));
        var page = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Onboarding", BlocksJson = WikiBlockJson.Serialize([intro, steps, old]) }, "lee");
        var access = new SentinelAccessService(db);
        await access.SetPermissionAsync(page.Id, false, "sam", SentinelAccessLevels.Comment, "grant");
        await access.SetPermissionAsync(page.Id, false, "lee", SentinelAccessLevels.Edit, "grant");
        return new Fixture(db, new SentinelSuggestionService(db, wiki, access, TimeProvider.System), page, intro, steps, old);
    }

    private static async Task<IReadOnlyList<WikiBlock>> BlocksAsync(Fixture f) =>
        WikiBlockJson.ParseBlocks((await f.Db.WikiPages.AsNoTracking().SingleAsync(page => page.Id == f.Page.Id)).BlocksJson);

    [Fact]
    public async Task Suggest_Review_AndAcceptPartially()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        var baseline = WikiBlockJson.Serialize([f.Intro, f.Steps, f.Old]);
        var added = Block("Step two: meet your buddy.");
        var edited = WikiBlockJson.Serialize([f.Intro with { RichText = [new("Welcome aboard!")] }, f.Steps, added]);

        var suggestion = (await f.Service.SubmitAsync(f.Page.Id, baseline, edited, "Tidied the intro", "sam"))!;
        suggestion.Changes.Select(change => change.Kind).Should().Equal(
            SentinelSuggestionKinds.Replace, SentinelSuggestionKinds.Insert, SentinelSuggestionKinds.Delete);
        (await f.Db.SentinelNotifications.SingleAsync()).Should().Match<SentinelNotification>(n =>
            n.Username == "lee" && n.Kind == SentinelSuggestions.NotificationKind && n.Message == "sam suggested 3 changes to \"Onboarding\".");
        (await BlocksAsync(f)).Select(block => block.PlainText).Should().Equal("Welcome to the team.", "Step one: get a laptop.", "Outdated paragraph.");

        // Lee accepts the new step and the removal, rejects the reworded intro.
        var ids = suggestion.Changes.ToDictionary(change => change.Kind, change => change.Id);
        var reviewed = await f.Service.ReviewAsync(suggestion.Id, [ids[SentinelSuggestionKinds.Insert], ids[SentinelSuggestionKinds.Delete]], accept: true, "lee");
        reviewed.Status.Should().Be(SentinelSuggestionStatuses.Open);
        reviewed.PendingCount.Should().Be(1);
        (await BlocksAsync(f)).Select(block => block.PlainText).Should().Equal("Welcome to the team.", "Step one: get a laptop.", "Step two: meet your buddy.");

        reviewed = await f.Service.ReviewAsync(suggestion.Id, [ids[SentinelSuggestionKinds.Replace]], accept: false, "lee");
        reviewed.Status.Should().Be(SentinelSuggestionStatuses.Closed);
        reviewed.Changes.Select(change => change.Status).Should().Equal(
            SentinelSuggestionStatuses.Rejected, SentinelSuggestionStatuses.Accepted, SentinelSuggestionStatuses.Accepted);
        (await f.Service.ListOpenAsync(f.Page.Id, "lee")).Should().BeEmpty();
        (await f.Db.WikiPageRevisions.CountAsync(revision => revision.WikiPageId == f.Page.Id)).Should().BeGreaterThan(1, "an accepted change is saved as a new version");
    }

    [Fact]
    public async Task Access_ShouldBeCommentToSuggest_EditToReview_AndOnlyTheAuthorWithdraws()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        var baseline = WikiBlockJson.Serialize([f.Intro, f.Steps, f.Old]);
        var edited = WikiBlockJson.Serialize([f.Intro, f.Steps]);

        var noAccess = () => f.Service.SubmitAsync(f.Page.Id, baseline, edited, null, "pat");
        await noAccess.Should().ThrowAsync<UnauthorizedAccessException>();
        (await f.Service.SubmitAsync(f.Page.Id, baseline, baseline, null, "sam")).Should().BeNull("nothing changed");

        var suggestion = (await f.Service.SubmitAsync(f.Page.Id, baseline, edited, null, "sam"))!;
        var commenterReviews = () => f.Service.ReviewAsync(suggestion.Id, [suggestion.Changes[0].Id], true, "sam");
        await commenterReviews.Should().ThrowAsync<UnauthorizedAccessException>();
        (await f.Service.ListOpenAsync(f.Page.Id, "pat")).Should().BeEmpty("pat can't see the page");
        (await f.Service.ListOpenAsync(f.Page.Id, "sam")).Should().ContainSingle();

        var otherWithdraws = () => f.Service.WithdrawAsync(suggestion.Id, "lee");
        await otherWithdraws.Should().ThrowAsync<InvalidOperationException>();
        await f.Service.WithdrawAsync(suggestion.Id, "sam");
        (await f.Service.ListOpenAsync(f.Page.Id, "lee")).Should().BeEmpty();
        var reviewWithdrawn = () => f.Service.ReviewAsync(suggestion.Id, [suggestion.Changes[0].Id], true, "lee");
        await reviewWithdrawn.Should().ThrowAsync<InvalidOperationException>();
    }
}
