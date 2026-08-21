using FluentAssertions;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelWorkspaceServiceTests
{
    [Fact]
    public async Task SearchAsync_ShouldFindPageBlockContentAndDatabaseRowValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var databases = new WikiDatabaseService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Operations",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("The launch sequence uses the blue switch.")],
                    new Dictionary<string, string>()),
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Embed, 0, [],
                    new Dictionary<string, string>
                    {
                        ["url"] = "/admin/sentinel/files/file-id",
                        ["fileName"] = "Quarterly plan.pdf"
                    })])
        }, "u");

        var database = await databases.CreateDatabaseAsync("Projects", null, "u");
        var titleProperty = database.Properties.Single(property => property.Type == GwsBusinessSuite.Domain.Entities.WikiDatabasePropertyTypes.Title);
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, "Northstar migration");
        await databases.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Decision log for the launch window")],
                    new Dictionary<string, string>())]),
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value)
        }, "u");

        var pageResults = await sentinel.SearchAsync("blue switch", "u");
        var databaseResults = await sentinel.SearchAsync("Northstar", "u");
        var databasePageResults = await sentinel.SearchAsync("Decision log", "u");
        var attachmentResults = await sentinel.SearchAsync("Quarterly plan", "u");

        pageResults.Should().ContainSingle(result => !result.IsDatabase && result.Title == "Operations" && result.MatchKind == "Page content");
        databaseResults.Should().ContainSingle(result => result.IsDatabase && result.Title == "Projects" && result.MatchKind == "Database content");
        databasePageResults.Should().ContainSingle(result => result.IsDatabase && result.Title == "Projects" && result.MatchKind == "Database content");
        attachmentResults.Should().ContainSingle(result => !result.IsDatabase && result.Title == "Operations" && result.MatchKind == "Page content");
    }

    [Fact]
    public async Task SearchAsync_ShouldFindCommentsAndAiRunsAndRespectPageAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var access = new SentinelAccessService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System, access);

        var allowedPage = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Allowed page" }, "u");
        var deniedPage = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Denied page" }, "u");
        await access.SetPermissionAsync(allowedPage.Id, false, "member", SentinelAccessLevels.View, "owner");

        var allowedDiscussion = new SentinelDiscussion { WikiPageId = allowedPage.Id, CreatedBy = "u" };
        var deniedDiscussion = new SentinelDiscussion { WikiPageId = deniedPage.Id, CreatedBy = "u" };
        db.SentinelDiscussions.AddRange(allowedDiscussion, deniedDiscussion);
        db.SentinelDiscussionComments.AddRange(
            new SentinelDiscussionComment
            {
                SentinelDiscussionId = allowedDiscussion.Id,
                Body = "Remember to check the flux capacitor wiring.",
                CreatedBy = "u"
            },
            new SentinelDiscussionComment
            {
                SentinelDiscussionId = deniedDiscussion.Id,
                Body = "Flux capacitor secrets nobody else should see.",
                CreatedBy = "u"
            });
        db.SentinelAiRuns.AddRange(
            new SentinelAiRun
            {
                WikiPageId = allowedPage.Id,
                Action = "summarize",
                Instruction = "Summarize the flux capacitor rollout plan",
                Output = "The rollout is on track.",
                CreatedBy = "u"
            },
            new SentinelAiRun
            {
                WikiPageId = deniedPage.Id,
                Action = "summarize",
                Instruction = "Summarize the flux capacitor security review",
                Output = "Restricted findings.",
                CreatedBy = "u"
            },
            new SentinelAiRun
            {
                WikiPageId = null,
                Action = "summarize",
                Instruction = "Summarize flux capacitor status with no page",
                Output = "n/a",
                CreatedBy = "u"
            });
        await db.SaveChangesAsync();

        var results = await sentinel.SearchAsync("flux capacitor", "member");

        results.Should().ContainSingle(result =>
            result.Id == allowedPage.Id && !result.IsDatabase && result.MatchKind == "Comment");
        results.Should().ContainSingle(result =>
            result.Id == allowedPage.Id && !result.IsDatabase && result.MatchKind == "AI run");
        results.Should().NotContain(result => result.Id == deniedPage.Id);
    }

    [Fact]
    public async Task GetBacklinksAsync_ShouldFindStructuredAndLegacyLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        var target = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook" }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Structured source",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Open the runbook", Link: $"wikilink:{target.Id}")],
                    new Dictionary<string, string>())])
        }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Legacy source",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Markdown, 0, [],
                    new Dictionary<string, string> { ["content"] = "See [[Runbook]] before deploying." })])
        }, "u");

        var backlinks = await sentinel.GetBacklinksAsync(target.Id, "u");

        backlinks.Select(link => link.SourcePageTitle).Should().BeEquivalentTo("Structured source", "Legacy source");
    }

    [Fact]
    public async Task SearchAsync_ShouldRequireEveryTokenAndReturnMatchedTerms()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Deployment Runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Blue production checklist")], new Dictionary<string, string>())])
        }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Deployment Notes" }, "u");

        var results = await sentinel.SearchAsync("deployment blue", "u");

        results.Should().ContainSingle(result => result.Title == "Deployment Runbook");
        results[0].MatchedTerms.Should().Equal("deployment", "blue");
    }

    [Fact]
    public async Task SearchAsync_ShouldNotThrowWhenAPageMatchesOnlyByTitleWithNoTermInItsContent()
    {
        // Regression guard for a real bug: BuildPreview relied on FirstOrDefault() over an
        // empty sequence of (string Term, int Index) value tuples to signal "no match in
        // content" via a negative Index - but the tuple's default Index is 0, not negative, so
        // the guard never fired and it crashed with a NullReferenceException on
        // firstMatch.Term.Length whenever a result matched only through its title (the term
        // never appears anywhere in the body text) - a completely ordinary search outcome.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Deploy runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Flip the blue switch before liftoff.")], new Dictionary<string, string>())])
        }, "u");

        var act = async () => await sentinel.SearchAsync("deploy", "u");

        var results = await act.Should().NotThrowAsync();
        results.Subject.Should().ContainSingle(result => result.Title == "Deploy runbook");
    }

    [Fact]
    public async Task NavigationAsync_ShouldTrackPerUserFavoritesAndRecents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var wiki = new WikiService(db);
        var databases = new WikiDatabaseService(db);
        var sentinel = new SentinelWorkspaceService(db, time);
        var page = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook" }, "u");
        var database = await databases.CreateDatabaseAsync("Projects", null, "u");

        await sentinel.RecordOpenedAsync("Grant", page.Id, false);
        time.Advance(TimeSpan.FromMinutes(1));
        await sentinel.RecordOpenedAsync("Grant", database.Id, true);
        (await sentinel.ToggleFavoriteAsync("Grant", page.Id, false)).Should().BeTrue();

        var state = await sentinel.GetNavigationAsync("GRANT");

        state.Favorites.Should().ContainSingle(item => item.Id == page.Id && !item.IsDatabase);
        state.Recents.Select(item => item.Id).Should().Equal(database.Id, page.Id);
        (await sentinel.GetNavigationAsync("another-user")).Recents.Should().BeEmpty();
    }

    [Fact]
    public async Task ToggleFavoriteAsync_ShouldAllowAnAdminUserWithNoExplicitResourcePermission()
    {
        // Regression guard for a real bug: an Admin (AppUsers.Role == Admin) with no
        // SentinelWorkspaceMembers Owner row and no explicit SentinelResourcePermission grant
        // on this specific page got "Unable to update favorite: you don't have access to this
        // Sentinel item" - even though every other admin-gated surface (e.g. Wiki.razor's
        // `_isAdmin ||` check) already treats an Admin as having full access everywhere.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        db.AppUsers.Add(new GwsBusinessSuite.Domain.Entities.AppUser
        {
            Username = "grant", Role = GwsBusinessSuite.Domain.Entities.AppRoles.Admin, IsActive = true
        });
        await db.SaveChangesAsync();
        var wiki = new WikiService(db);
        var accessService = new SentinelAccessService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System, accessService);
        var page = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook" }, "u");

        var isFavorite = await sentinel.ToggleFavoriteAsync("grant", page.Id, false);

        isFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task MentionsAsync_ShouldSuggestPeopleAndDatesAndFindStructuredUserMentions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        db.AppUsers.Add(new GwsBusinessSuite.Domain.Entities.AppUser { Username = "Grant", IsActive = true });
        await db.SaveChangesAsync();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, time);
        var source = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Launch plan",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("@Grant", Link: "usermention:grant")], new Dictionary<string, string>())])
        }, "u");

        var people = await sentinel.SearchMentionSuggestionsAsync("gra", "u");
        var dates = await sentinel.SearchMentionSuggestionsAsync("tom", "u");
        var mentions = await sentinel.GetMentionsAsync("GRANT");

        people.Should().ContainSingle(item => item.Kind == "user" && item.Value == "Grant");
        dates.Should().ContainSingle(item => item.Kind == "date" && item.Value == "2026-07-21");
        mentions.Should().ContainSingle(item => item.SourcePageId == source.Id);
    }

    [Fact]
    public void RenderRichText_ShouldRenderMentionsAsNonNavigatingStyledLinks()
    {
        var html = WikiBlockHtmlRenderer.RenderRichText([
            new WikiRichTextSpan("@Grant", Link: "usermention:grant"),
            new WikiRichTextSpan(" "),
            new WikiRichTextSpan("@tomorrow", Link: "datemention:2026-07-21")
        ]);

        html.Should().Contain("class=\"wiki-mention\"");
        html.Should().Contain("href=\"usermention:grant\"");
        html.Should().Contain("href=\"datemention:2026-07-21\"");
        html.Should().NotContain("target=\"_blank\"");
    }

    [Fact]
    public void RenderRichText_ShouldRenderRowMentionsLikeOtherMentionKinds()
    {
        var databaseId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var html = WikiBlockHtmlRenderer.RenderRichText([
            new WikiRichTextSpan("Northstar migration", Link: $"rowmention:{databaseId}:{rowId}")
        ]);

        html.Should().Contain("class=\"wiki-mention\"");
        html.Should().Contain($"href=\"rowmention:{databaseId}:{rowId}\"");
        html.Should().NotContain("target=\"_blank\"");
    }

    [Fact]
    public async Task SearchMentionSuggestionsAsync_ShouldSuggestMatchingDatabaseRowsByTitle()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var databases = new WikiDatabaseService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);
        var database = await databases.CreateDatabaseAsync("Projects", null, "u");
        var titleProperty = database.Properties.Single(property => property.Type == GwsBusinessSuite.Domain.Entities.WikiDatabasePropertyTypes.Title);
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, "Northstar migration");
        var row = await databases.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value)
        }, "u");

        var suggestions = await sentinel.SearchMentionSuggestionsAsync("northstar", "u");

        suggestions.Should().ContainSingle(item =>
            item.Kind == "row" && item.Value == $"{database.Id}:{row.Id}" && item.Label == "Northstar migration");
    }

    [Fact]
    public async Task GetRowMentionsAsync_ShouldFindPagesThatReferenceARow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var databases = new WikiDatabaseService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);
        var database = await databases.CreateDatabaseAsync("Projects", null, "u");
        var row = await databases.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Status update",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("See the row", Link: $"rowmention:{database.Id}:{row.Id}")],
                    new Dictionary<string, string>())])
        }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Unrelated page" }, "u");

        var mentions = await sentinel.GetRowMentionsAsync(database.Id, row.Id, "u");

        mentions.Should().ContainSingle(mention => mention.SourcePageTitle == "Status update");
    }

    [Fact]
    public async Task GetBacklinksAsync_ShouldFindTheTargetPage_EvenWhenItsRowIsBeyondTheScanCap()
    {
        // Regression guard for the search/backlinks unbounded-table-scan fix: GetBacklinksAsync
        // now caps how many WikiPages rows it scans for links, but must still resolve the
        // *target* page itself via a direct lookup rather than by finding it inside that capped
        // scan - otherwise a workspace with more pages than the cap would see backlinks silently
        // stop resolving for its older pages.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);
        var targetId = Guid.NewGuid();

        var source = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Source page",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Open the target", Link: $"wikilink:{targetId}")],
                    new Dictionary<string, string>())])
        }, "u");

        db.WikiPages.AddRange(Enumerable.Range(0, 2000).Select(i => new WikiPage
        {
            Id = Guid.NewGuid(),
            Title = $"Filler {i}",
            Slug = $"filler-{i}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "u"
        }));
        await db.SaveChangesAsync();
        db.WikiPages.Add(new WikiPage
        {
            Id = targetId,
            Title = "Target page",
            Slug = "target-page",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "u"
        });
        await db.SaveChangesAsync();

        var backlinks = await sentinel.GetBacklinksAsync(targetId, "u");

        backlinks.Should().ContainSingle(link => link.SourcePageTitle == source.Title);
    }

    [Fact]
    public async Task WorkspaceDiscovery_ShouldExcludePagesAndDatabasesTheRequesterCannotView()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var databases = new WikiDatabaseService(db);
        var access = new SentinelAccessService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System, access);

        var target = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Target" }, "u");
        var allowedPage = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Allowed classified source",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [
                        new WikiRichTextSpan("classified @member", Link: "usermention:member"),
                        new WikiRichTextSpan(" target", Link: $"wikilink:{target.Id}")
                    ], new Dictionary<string, string>())])
        }, "u");
        var deniedPage = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Denied classified source",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [
                        new WikiRichTextSpan("classified @member", Link: "usermention:member"),
                        new WikiRichTextSpan(" target", Link: $"wikilink:{target.Id}")
                    ], new Dictionary<string, string>())])
        }, "u");
        var allowedDatabase = await databases.CreateDatabaseAsync("Allowed projects", null, "u");
        var deniedDatabase = await databases.CreateDatabaseAsync("Denied projects", null, "u");
        foreach (var database in new[] { allowedDatabase, deniedDatabase })
        {
            var title = database.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
            var values = new System.Text.Json.Nodes.JsonObject();
            WikiPropertyValues.SetText(values, title.Id, "Classified milestone");
            await databases.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
            {
                Values = values.ToDictionary(item => item.Key, item => item.Value)
            }, "u");
        }

        await access.SetPermissionAsync(target.Id, false, "member", SentinelAccessLevels.View, "owner");
        await access.SetPermissionAsync(allowedPage.Id, false, "member", SentinelAccessLevels.View, "owner");
        await access.SetPermissionAsync(allowedDatabase.Id, true, "member", SentinelAccessLevels.View, "owner");
        db.SentinelNavigationEntries.AddRange(
            new SentinelNavigationEntry
            {
                Username = "member", TargetId = allowedPage.Id, IsDatabase = false,
                LastOpenedAt = DateTimeOffset.UtcNow, CreatedBy = "member"
            },
            new SentinelNavigationEntry
            {
                Username = "member", TargetId = deniedPage.Id, IsDatabase = false,
                LastOpenedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedBy = "member"
            });
        await db.SaveChangesAsync();

        var search = await sentinel.SearchAsync("classified", "member");
        var backlinks = await sentinel.GetBacklinksAsync(target.Id, "member");
        var mentions = await sentinel.GetMentionsAsync("member");
        var rowSuggestions = await sentinel.SearchMentionSuggestionsAsync("classified", "member");
        var navigation = await sentinel.GetNavigationAsync("member");

        search.Should().Contain(result => result.Id == allowedPage.Id && !result.IsDatabase);
        search.Should().Contain(result => result.Id == allowedDatabase.Id && result.IsDatabase);
        search.Should().NotContain(result => result.Id == deniedPage.Id || result.Id == deniedDatabase.Id);
        backlinks.Should().ContainSingle(link => link.SourcePageId == allowedPage.Id);
        mentions.Should().ContainSingle(mention => mention.SourcePageId == allowedPage.Id);
        rowSuggestions.Should().ContainSingle(suggestion =>
            suggestion.Kind == "row" && suggestion.Description.Contains(allowedDatabase.Title));
        navigation.Recents.Should().ContainSingle(item => item.Id == allowedPage.Id);
    }

    [Fact]
    public async Task SavedSearches_ShouldPersistPerUserDedupeAndSupportDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        var first = await sentinel.SaveSearchAsync("Grant", "launch checklist");
        var duplicate = await sentinel.SaveSearchAsync("GRANT", "launch checklist");
        await sentinel.SaveSearchAsync("Grant", "onboarding");

        duplicate.Id.Should().Be(first.Id, "saving the same query twice for the same user must not create a duplicate row");
        (await sentinel.ListSavedSearchesAsync("grant")).Should().HaveCount(2);
        (await sentinel.ListSavedSearchesAsync("someone-else")).Should().BeEmpty();

        await sentinel.DeleteSavedSearchAsync("Grant", first.Id);
        (await sentinel.ListSavedSearchesAsync("Grant")).Should().ContainSingle(saved => saved.Query == "onboarding");
    }

    [Fact]
    public async Task SemanticSearch_ShouldFailClosedForInaccessibleWorkspaceResults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var access = new SentinelAccessService(db);
        var allowed = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Allowed recovery notes" }, "owner");
        var denied = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Denied recovery notes" }, "owner");
        await access.SetPermissionAsync(allowed.Id, false, "member", SentinelAccessLevels.View, "owner");
        var hybrid = new FixedHybridSearchService([
            Hit(allowed.Id, allowed.Title, 0.94),
            Hit(denied.Id, denied.Title, 0.99)
        ]);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System, access, hybrid);

        var results = await sentinel.SearchAsync("oblique constellation", "member");

        results.Should().ContainSingle(result => result.Id == allowed.Id);
        results.Should().NotContain(result => result.Id == denied.Id);
    }

    private static async Task<ApplicationDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan by) => _utcNow += by;
    }

    private static SemanticSearchHit Hit(Guid sourceId, string title, double score) =>
        new(Guid.NewGuid(), SemanticSourceTypes.WikiPage, sourceId, null, title, "Semantic preview", score, 0, score);

    private sealed class FixedHybridSearchService(IReadOnlyList<SemanticSearchHit> hits) : IHybridSearchService
    {
        public Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(string query, IReadOnlyCollection<string>? sourceTypes = null, int take = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SemanticSearchHit>>(hits.Take(take).ToList());
        public Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SemanticIndexStatus(true, "test", hits.Count, DateTimeOffset.UtcNow, 0));
        public Task RebuildAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
