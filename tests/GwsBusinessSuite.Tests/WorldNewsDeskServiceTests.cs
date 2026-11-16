using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class WorldNewsDeskServiceTests : IAsyncLifetime
{
    private readonly WorldNewsTestStore _store = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _db = null!;
    private WorldNewsService _news = null!;
    private WorldNewsDeskService _desk = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.AppUsers.AddRange(
            new AppUser { Username = "ana", Role = AppRoles.Admin },
            new AppUser { Username = "ben", Role = AppRoles.Admin },
            new AppUser { Username = "cat", Role = AppRoles.Author },
            new AppUser { Username = "off", Role = AppRoles.Admin, IsActive = false });
        await _db.SaveChangesAsync();
        _store.SeedEvent("s1", "e1", "Quake hits Tokyo", ["A", "B"], WorldNewsTestStore.Now);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WorldNews:DatabasePath"] = _store.Path }).Build();
        _news = new WorldNewsService(configuration, Options.Create(new ContentStudioOptions()), new UnusedHttp(),
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorldNewsService>.Instance);
        _desk = new WorldNewsDeskService(_db, _news);
    }

    [Fact]
    public async Task Notes_ShouldNotifyOnlyActiveAdmins_AndKeepTheSavedNote()
    {
        var notified = new List<string>();
        _news.AlertCreated += (username, _) => notified.Add(username);

        var saved = await _desk.AddNoteAsync("e1", "  Checked with @BEN and @ben; not @cat, @off, @ghost or @ana  ", "ana");

        Assert.Equal("Checked with @BEN and @ben; not @cat, @off, @ghost or @ana", saved.Text);
        Assert.Equal(saved.Id, Assert.Single(await _desk.NotesAsync("e1", "ben")).Id);
        Assert.Equal("ben", Assert.Single(notified));
        Assert.Single(_news.Store.Alerts("ben"));
        Assert.Empty(_news.Store.Alerts("cat"));
        Assert.Empty(_news.Store.Alerts("off"));
        Assert.Empty(_news.Store.Alerts("ghost"));
        Assert.Empty(_news.Store.Alerts("ana"));
    }

    [Theory]
    [InlineData("cat")]
    [InlineData("off")]
    [InlineData("unknown")]
    public async Task NewsDesk_ShouldDenyNotesAndHandoversToPeopleWithoutDeskAccess(string username)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.NotesAsync("e1", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.AddNoteAsync("e1", "Private desk note", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.HandoverAsync(8, username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.ContactsAsync(username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.SaveContactAsync(new SourceContact("", "ana", "Source", "", "", 3, "", DateTimeOffset.UtcNow), username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.RemoveContactAsync("source", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.SourceHistoryAsync("source", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.RecordSourceUseAsync("source", "e1", "held", "", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.ApprovalsAsync("e1", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.SignAsync("e1", SignOffs.Verdict, username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.LiveUpdatesAsync("e1", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.PublishLiveUpdateAsync("e1", "Public update", LiveUpdates.Unconfirmed, username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.CorrectLiveUpdateAsync("e1", "update", "Correction", LiveUpdates.Confirmed, username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.SetHoldAsync("e1", DateTimeOffset.UtcNow.AddHours(1), "Private embargo", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.ReleaseHoldAsync("e1", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.CorrectionsAsync("e1", username));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _desk.PublishCorrectionAsync("e1", "Correction", "Corrected fact", null, username));

        Assert.Empty(_news.Store.DeskNotes("e1"));
    }

    [Fact]
    public async Task Holds_ShouldUseTheDeskIdentity_AndBlockLiveCoverageUntilAnotherAdminReleases()
    {
        var update = await _desk.PublishLiveUpdateAsync("e1", "Public update", LiveUpdates.Unconfirmed, "ana");
        var before = DateTimeOffset.UtcNow;
        await _desk.SetHoldAsync("e1", before.AddHours(1), "  Await briefing  ", "ana");
        await _desk.SetHoldAsync("e1", before.AddHours(2), "  Revised release  ", "ben");

        var state = await _desk.LiveUpdatesAsync("e1", "ana");
        var hold = Assert.IsType<StoryHold>(state.Hold);
        Assert.Equal(("ben", "Revised release"), (hold.SetBy, hold.Reason));
        Assert.InRange(hold.SetAt, before.AddSeconds(-1), DateTimeOffset.UtcNow);
        Assert.True(_news.Store.IsSituationHeld("s1", DateTimeOffset.UtcNow));
        Assert.Null(_news.PublicLiveBlog("e1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.PublishLiveUpdateAsync("e1", "Blocked update", LiveUpdates.Confirmed, "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.CorrectLiveUpdateAsync("e1", update.Id, "Blocked correction", LiveUpdates.Confirmed, "ana"));

        await _desk.ReleaseHoldAsync("e1", "ana");

        Assert.Null((await _desk.LiveUpdatesAsync("e1", "ben")).Hold);
        Assert.NotNull(_news.PublicLiveBlog("e1"));
        await _desk.PublishLiveUpdateAsync("e1", "Released update", LiveUpdates.Confirmed, "ben");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.ReleaseHoldAsync("e1", "ana"));
    }

    [Fact]
    public async Task Holds_ShouldRejectPastTimesBlankReasonsAndMissingStories()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _desk.SetHoldAsync("e1", DateTimeOffset.UtcNow.AddMinutes(-1), "Too late", "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => _desk.SetHoldAsync("e1", DateTimeOffset.UtcNow.AddHours(1), " ", "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SetHoldAsync("missing", DateTimeOffset.UtcNow.AddHours(1), "Missing story", "ana"));
        Assert.Null(_news.Store.Hold("e1"));
    }

    [Fact]
    public async Task Corrections_ShouldAssociateOnlyAPublicArticle_WithoutRewritingIt()
    {
        var article = new Article { Slug = "public-report", Title = "Public report", BodyMarkdown = "Original body", Status = ArticleStatuses.Published, PublishedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var draft = new Article { Slug = "private-draft", Title = "Private draft" };
        var future = new Article { Slug = "scheduled-report", Title = "Scheduled report", Status = ArticleStatuses.Published, PublishedAt = DateTimeOffset.UtcNow.AddDays(1) };
        var trashed = new Article { Slug = "trashed-report", Title = "Trashed report", Status = ArticleStatuses.Published, TrashedAt = DateTimeOffset.UtcNow };
        _db.Articles.AddRange(article, draft, future, trashed);
        await _db.SaveChangesAsync();

        foreach (var slug in new[] { "missing", draft.Slug, future.Slug, trashed.Slug })
            await Assert.ThrowsAsync<ArgumentException>(() => _desk.PublishCorrectionAsync("e1", "Correction", "The corrected fact.", slug, "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.PublishCorrectionAsync("missing", "Correction", "The corrected fact.", article.Slug, "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.CorrectionsAsync("missing", "ana"));
        Assert.Empty(_news.PublicCorrections());

        var saved = await _desk.PublishCorrectionAsync("e1", " Correction ", " The corrected fact. ", $" {article.Slug} ", "ben");
        var publicNotice = Assert.Single(_news.PublicCorrections(articleId: article.Id));
        Assert.Equal(article.Id, saved.ArticleId);
        Assert.Equal(("ben", "Correction", "The corrected fact."), (saved.Username, publicNotice.Title, publicNotice.Text));
        Assert.Equal(saved.Id, Assert.Single(await _desk.CorrectionsAsync("e1", "ana")).Id);
        Assert.Equal("Original body", article.BodyMarkdown);
        Assert.Equal(ArticleStatuses.Published, article.Status);
    }

    [Fact]
    public async Task Corrections_ShouldBlockNewPublicationDuringAHold_WithoutRecallingAnExistingNotice()
    {
        var saved = await _desk.PublishCorrectionAsync("e1", "Correction", "Earlier report was incorrect.", null, "ana");
        await _desk.SetHoldAsync("e1", DateTimeOffset.UtcNow.AddHours(1), "Private embargo details", "ben");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.PublishCorrectionAsync("e1", "Another correction", "Updated facts.", null, "ana"));

        Assert.Equal(saved.Id, Assert.Single(_news.PublicCorrections(id: saved.Id)).Id);
        await _desk.ReleaseHoldAsync("e1", "ana");
        await _desk.PublishCorrectionAsync("e1", "Follow-up correction", "The corrected account.", null, "ana");
        Assert.Equal(2, (await _desk.CorrectionsAsync("e1", "ana")).Count);
    }

    [Fact]
    public async Task Notes_ShouldRejectAStoryThatNoLongerExists()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.NotesAsync("missing", "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.AddNoteAsync("missing", "Please check this @ben", "ana"));

        Assert.Empty(_news.Store.DeskNotes());
        Assert.Empty(_news.Store.Alerts("ben"));
    }

    [Fact]
    public async Task Handover_ShouldIncludeAnUnchangedSingleSourceStoryFromBeforeTheShift()
    {
        var now = DateTimeOffset.UtcNow;
        _news.Store.SetBudgetItem(new BudgetItem("e1", "Quake hits Tokyo", "ana", "verifying", "Find a second source", now.AddDays(-2), "ana"));
        _news.Store.AppendLabelSnapshots([("e1", "SingleSource", "One source so far", 1, 1)], "v", now.AddDays(-2));
        _news.Store.AppendLabelSnapshots([("not-on-budget", "SingleSource", "Another single source story", 1, 1)], "v", now.AddDays(-2));

        var handover = await _desk.HandoverAsync(8, "ana");

        Assert.Contains("## Still on one source", handover);
        Assert.Contains("- Quake hits Tokyo (ana)", handover);
        Assert.DoesNotContain("## Labels that changed", handover);
        Assert.DoesNotContain("not-on-budget", handover);
    }

    [Fact]
    public async Task SourceBook_ShouldDeriveOwnershipFromTheCaller_AndKeepAdminContactsPrivate()
    {
        var contact = await _desk.SaveContactAsync(new SourceContact("", "ben", "Private source", "Private organisation",
            "Private contact", 4, "Private notes", DateTimeOffset.UtcNow), "ana");

        Assert.Equal("ana", contact.Owner);
        Assert.Single(await _desk.ContactsAsync("ana"));
        Assert.Empty(await _desk.ContactsAsync("ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SourceHistoryAsync(contact.Id, "ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SaveContactAsync(contact with { Name = "Stolen" }, "ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.RecordSourceUseAsync(contact.Id, "e1", "held", "Other person's note", "ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.RemoveContactAsync(contact.Id, "ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.RecordSourceUseAsync(contact.Id, "missing", "held", "", "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => _desk.RecordSourceUseAsync(contact.Id, "e1", "maybe", "", "ana"));
        await _desk.RecordSourceUseAsync(contact.Id, "e1", "unknown", "Private use note", "ana");
        await _desk.RecordSourceUseAsync(contact.Id, "e1", "held", "Checked privately", "ana");

        var history = await _desk.SourceHistoryAsync(contact.Id, "ana");
        Assert.Equal((1, 1, 0), history.Counts);
        Assert.Equal("held", Assert.Single(history.Uses).Outcome);
        Assert.Empty(_news.Store.DeskNotes());
        var handover = await _desk.HandoverAsync(8, "ana");
        Assert.DoesNotContain(contact.Name, handover);
        Assert.DoesNotContain(contact.Contact, handover);
        Assert.DoesNotContain(contact.Notes, handover);
        Assert.DoesNotContain("Checked privately", handover);
    }

    [Fact]
    public async Task Approvals_ShouldRequireTwoPeople_AndKeepTheGlobalChainAcrossStories()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SignAsync("missing", SignOffs.Verdict, "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SignAsync("e1", SignOffs.Verdict, "ana"));
        _store.SeedEvent("s2", "e2", "Another story", ["A", "B"], WorldNewsTestStore.Now);
        foreach (var eventId in new[] { "e1", "e2" })
            foreach (var (key, _, _) in DeskChecklist.Items)
                _news.Store.SetDeskCheck(eventId, key, true, "Checked the record", "ana", DateTimeOffset.UtcNow);

        var first = await _desk.SignAsync("e1", SignOffs.Verdict, "ana");
        await _desk.SignAsync("e2", SignOffs.Verdict, "ana");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SignAsync("e1", SignOffs.Verdict, "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.SignAsync("e1", SignOffs.Publish, "ben"));
        await _desk.SignAsync("e1", SignOffs.Verdict, "ben");
        await _desk.SignAsync("e1", SignOffs.Publish, "ana");
        await _desk.SignAsync("e1", SignOffs.Publish, "ben");

        var state = await _desk.ApprovalsAsync("e1", "ana");
        Assert.True(state.ChecklistComplete);
        Assert.Equal(state.LabelLevel, first.LabelLevel);
        Assert.True(SignOffs.IsComplete(state.Records, SignOffs.Verdict));
        Assert.True(SignOffs.IsComplete(state.Records, SignOffs.Publish));
        Assert.Equal(4, state.Records.Count);
        Assert.Null(SignOffs.FirstBroken(_news.Store.SignOffs()));
        Assert.Empty(_db.Articles);
    }

    [Fact]
    public async Task LiveUpdates_ShouldAllowTheActiveDeskToCorrect_OnlyTheSpecifiedStory()
    {
        var update = await _desk.PublishLiveUpdateAsync("e1", " Public update ", LiveUpdates.Unconfirmed, "ana");
        _store.SeedEvent("s2", "e2", "Another story", ["A", "B"], WorldNewsTestStore.Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.CorrectLiveUpdateAsync("e2", update.Id, "Wrong story", LiveUpdates.Confirmed, "ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.CorrectLiveUpdateAsync("e1", "missing", "Unknown update", LiveUpdates.Confirmed, "ben"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.PublishLiveUpdateAsync("missing", "Unknown story", LiveUpdates.Unconfirmed, "ana"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _desk.LiveUpdatesAsync("missing", "ana"));
        await _desk.CorrectLiveUpdateAsync("e1", update.Id, " Corrected publicly ", LiveUpdates.Confirmed, "ben");

        var page = await _desk.LiveUpdatesAsync("e1", "ben");
        var saved = Assert.Single(page.Updates);
        Assert.Equal("Public update", saved.Text);
        Assert.Equal("Corrected publicly", saved.CorrectedText);
        var blog = _news.PublicLiveBlog("e1") ?? throw new InvalidOperationException("The published story must have public live coverage.");
        Assert.Equal("Corrected publicly", Assert.Single(blog.Updates).CorrectedText);
        Assert.Null(_news.PublicLiveBlog("missing"));
        Assert.Null(_news.PublicLiveBlog("e2"));
        Assert.Empty(_db.Articles);
    }

    [Fact]
    public async Task Handover_ShouldIncludeAnOldLivePostCorrectedDuringTheShift()
    {
        var now = DateTimeOffset.UtcNow;
        var update = _news.Store.AddLiveUpdate("e1", "ana", "Superseded wording", LiveUpdates.Unconfirmed, now.AddDays(-2));
        _news.Store.CorrectLiveUpdate(update.Id, "Corrected during this shift", LiveUpdates.Confirmed, now);

        var handover = await _desk.HandoverAsync(8, "ana");

        Assert.Contains("## Posted live", handover);
        Assert.Contains("Corrected during this shift", handover);
        Assert.Contains("; corrected ", handover);
        Assert.DoesNotContain("Superseded wording", handover);
    }

    public async Task DisposeAsync()
    {
        _news?.Dispose();
        await _services.DisposeAsync();
        if (_db is not null) await _db.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _store.Dispose();
    }

    private sealed class UnusedHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
