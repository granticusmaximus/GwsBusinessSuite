using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel items 25 (fill an AI column), 26 (clean up a note), 27 (caretakers), 28 (meeting notes).
public sealed class SentinelPhase2CloseoutTests
{
    private static async Task<ApplicationDbContext> DbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin });
        await db.SaveChangesAsync();
        return db;
    }

    private static WikiBlock Block(string text, string type = WikiBlockTypes.Paragraph, string? link = null) =>
        new(Guid.NewGuid(), type, 0, [new WikiRichTextSpan(text, Link: link)], new Dictionary<string, string>());

    [Fact]
    public async Task AiColumn_ShouldPrepareEmptyRowsWithResolvedPrompts_AndWriteValuesInOneGo()
    {
        await using var db = await DbAsync();
        var service = new WikiDatabaseService(db, ollama: new FakeOllama("generated"));
        var database = await service.CreateDatabaseAsync("Tasks", null, "grant");
        var title = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var summary = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Summary", Type = WikiDatabasePropertyTypes.AiField, AiPromptTemplate = "Summarize [Name]", AiModel = "gemma4" }, "grant");
        async Task<WikiDatabaseRow> Row(string name, string? done)
        {
            var values = new System.Text.Json.Nodes.JsonObject();
            WikiPropertyValues.SetText(values, title.Id, name);
            if (done is not null) WikiPropertyValues.SetText(values, summary.Id, done);
            return await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { Values = values.ToDictionary(kv => kv.Key, kv => kv.Value) }, "grant");
        }
        var empty = await Row("Ship it", null);
        await Row("Already done", "kept");

        var job = await service.PrepareAiFieldJobAsync(database.Id, summary.Id, onlyEmpty: true);
        job.Model.Should().Be("gemma4");
        job.Rows.Should().ContainSingle().Which.Should().Be(new WikiAiFieldJobRow(empty.Id, "Ship it", "Summarize Ship it"));
        (await service.PrepareAiFieldJobAsync(database.Id, summary.Id, onlyEmpty: false)).Rows.Should().HaveCount(2);
        (await service.GenerateAiFieldTextAsync("gemma4", "x")).Should().Be("generated");

        (await service.WriteAiFieldValuesAsync(database.Id, summary.Id, new Dictionary<Guid, string> { [empty.Id] = " A summary " }, "grant")).Should().Be(1);
        (await service.PrepareAiFieldJobAsync(database.Id, summary.Id, onlyEmpty: true)).Rows.Should().BeEmpty();
    }

    [Fact]
    public void Cleanup_ShouldAcceptARestructure_RefuseAddedFacts_AndWarnWhenContentIsDropped()
    {
        var original = SentinelNoteCleanup.ToMarkdown([Block("met sam re budget 4500 due friday. todo send quote, book venue")]);
        original.Should().NotStartWith("#");

        var tidy = SentinelNoteCleanup.Check(original, "## Budget\n- Met Sam re budget 4500, due Friday.\n\n## Tasks\n- [ ] Send quote\n- [ ] Book venue");
        tidy.Warning.Should().BeNull();
        tidy.Blocks.Select(b => b.Type).Should().Contain([WikiBlockTypes.Heading2, WikiBlockTypes.ToDo]);

        var invented = SentinelNoteCleanup.Check(original, "## Budget\n- Met Sam and the board about the quarterly marketing budget of 9000 dollars in Atlanta.");
        invented.Blocks.Should().BeEmpty();
        invented.Warning.Should().Contain("refused");

        SentinelNoteCleanup.Check(original, "- Budget 4500").Warning.Should().Contain("missing");
        SentinelNoteCleanup.Check(original, "  ").Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task Caretakers_ShouldListChangesStalePagesAndOrphans_AndNudgeEachOwnerOnce()
    {
        await using var db = await DbAsync();
        var wiki = new WikiService(db);
        var hub = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Hub", BlocksJson = "[]" }, "grant");
        var linked = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Linked", BlocksJson = "[]" }, "grant");
        var lonely = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Lonely", BlocksJson = "[]" }, "grant");
        var hubRow = await db.WikiPages.SingleAsync(p => p.Id == hub.Id);
        hubRow.BlocksJson = WikiBlockJson.Serialize([Block("See Linked", link: $"wikilink:{linked.Id}")]);
        var old = await db.WikiPages.SingleAsync(p => p.Id == lonely.Id);
        old.OwnerUsername = "grant";
        old.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-400);
        await db.SaveChangesAsync();
        var caretaker = new SentinelCaretakerService(db, TimeProvider.System, new SentinelAccessService(db));

        (await caretaker.RecentChangesAsync("grant", 7)).Select(p => p.Title).Should().BeEquivalentTo("Hub", "Linked");
        var stale = await caretaker.StalePagesAsync("grant", 180);
        stale.Should().ContainSingle().Which.Title.Should().Be("Lonely");
        (await caretaker.NudgeOwnersAsync(stale, 180)).Should().Be(1);
        (await caretaker.NudgeOwnersAsync(stale, 180)).Should().Be(0, "an owner is nudged at most once per window");
        (await db.SentinelNotifications.SingleAsync()).Kind.Should().Be(SentinelCaretaker.NudgeKind);

        var orphans = await caretaker.OrphanPagesAsync("grant", 20);
        orphans.Select(o => o.Title).Should().BeEquivalentTo("Hub", "Lonely");
        SentinelCaretaker.OrphanBlocks(orphans).Should().HaveCount(3).And.Contain(b => b.Type == WikiBlockTypes.ToDo);
        SentinelCaretaker.ChangesBlocks(await caretaker.RecentChangesAsync("grant", 7), 7)[0].PlainText.Should().Be("2 pages changed in the last 7 days.");
        GwsBusinessSuite.Application.Automation.AutomationStarterWorkflows.Find("sentinel-what-changed")!.Graph.Nodes.Select(n => n.TypeKey)
            .Should().Equal("core.scheduleTrigger", "wiki.recentChanges", "wiki.createPage");
    }

    [Fact]
    public async Task MeetingNotes_ShouldLinkToABookingAndItsContact_AndShowInTheContactsHistory()
    {
        await using var db = await DbAsync();
        var contact = new Contact { FullName = "Sam Client", Email = "sam@example.com", CreatedBy = "grant" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var bookingType = new BookingType { Name = "Intro", Slug = "intro", CreatedBy = "grant" };
        db.BookingTypes.Add(bookingType);
        var booking = new Booking { BookingTypeId = bookingType.Id, ContactId = contact.Id, StartsAt = DateTimeOffset.UtcNow.AddHours(2), EndsAt = DateTimeOffset.UtcNow.AddHours(3),
            AttendeeName = "Sam Client", AttendeeEmail = "sam@example.com", ManageTokenHash = "x", CreatedBy = "grant" };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var page = await new WikiService(db).SavePageAsync(new WikiPageEditorModel { Title = "Intro call", BlocksJson = "[]" }, "grant");
        var notes = new SentinelMeetingNotesService(db, TimeProvider.System);

        var targets = await notes.ListTargetsAsync("sam");
        targets.Select(t => t.Kind).Should().Equal(SentinelMeetingTargetKinds.Booking, SentinelMeetingTargetKinds.Contact);
        await notes.LinkAsync(page.Id, null, booking.Id);
        var saved = await db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == page.Id);
        (saved.LinkedBookingId, saved.LinkedContactId).Should().Be((booking.Id, contact.Id), "a booking brings its contact");
        await notes.Invoking(n => n.LinkAsync(page.Id, Guid.NewGuid(), null)).Should().ThrowAsync<ArgumentException>();

        var timeline = await new ContactTimelineService(new Factory(db)).GetTimelineAsync(contact.Id);
        timeline.Should().Contain(e => e.Title == "Meeting notes: Intro call" && e.Link == $"/admin/sentinel?page={page.Id}");
    }

    private sealed class Factory(ApplicationDbContext db) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(db.Database.GetDbConnection()).Options));
    }

    private sealed class FakeOllama(string response) : IOllamaService
    {
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) => Task.FromResult(response);
        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }
}
