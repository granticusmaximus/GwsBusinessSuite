using FluentAssertions;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelPageLockTests
{
    private const string Secret = "zebra-quartz-7731";
    private const string Session = "session-a";

    private static WikiBlock Text(string text) =>
        new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());

    [Fact]
    public async Task Locking_ShouldLeaveNoReadableCopyOfTheTextAnywhereInTheDatabase()
    {
        var f = await CreateAsync();
        var page = await f.Wiki.SavePageAsync(new WikiPageEditorModel { Title = "Diary", BlocksJson = WikiBlockJson.Serialize([Text($"Notes {Secret} #private-tag")]) }, "grant");
        // A version, a suggested edit and a search-index entry that all hold the text.
        await f.Wiki.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id, ExpectedContentVersion = page.ContentVersion, Title = "Diary",
            BlocksJson = WikiBlockJson.Serialize([Text($"Notes {Secret} #private-tag, more")])
        }, "grant", createRevisionCheckpoint: true);
        f.Db.PageSuggestions.Add(new SentinelPageSuggestion { WikiPageId = page.Id, ChangesJson = $"[\"{Secret}\"]", CreatedBy = "grant" });
        f.Db.SemanticSearchDocuments.Add(new SemanticSearchDocument
        {
            SourceType = SemanticSourceTypes.WikiPage, SourceId = page.Id, Title = "Diary", Content = Secret, ContentHash = "x", EmbeddingModel = "m"
        });
        await f.Db.SaveChangesAsync();
        (await ContainsAnywhereAsync(f.Connection, Secret)).Should().BeTrue("the setup must actually store the text");

        await f.Locks.SetPinAsync("grant", Session, null, "246810");
        await f.Locks.LockAsync(page.Id, "grant");

        (await ContainsAnywhereAsync(f.Connection, Secret)).Should().BeFalse();
        (await ContainsAnywhereAsync(f.Connection, "private-tag")).Should().BeFalse();
        var stored = await f.Db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == page.Id);
        stored.Title.Should().Be("Diary");
        stored.BlocksJson.Should().Be("[]");
        stored.LockedBy.Should().Be("grant");
    }

    [Fact]
    public async Task OnlyTheOwner_WithALiveUnlockInTheSameSession_CanReadIt()
    {
        var f = await CreateAsync();
        var page = await LockedPageAsync(f);

        (await f.Locks.ReadBlocksAsync(page.Id, "grant", Session)).Should().BeNull("not unlocked yet");
        await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null);

        (await f.Locks.ReadBlocksAsync(page.Id, "grant", Session)).Should().Contain(Secret);
        (await f.Locks.ReadBlocksAsync(page.Id, "grant", "another-session")).Should().BeNull();
        (await f.Locks.ReadBlocksAsync(page.Id, "sam", Session)).Should().BeNull("only the person who locked it");
        (await f.Locks.GetStatusAsync(page.Id, "grant", Session)).IsUnlocked.Should().BeTrue();

        f.Time.Advance(SentinelPageLock.UnlockWindow + TimeSpan.FromSeconds(1));
        (await f.Locks.ReadBlocksAsync(page.Id, "grant", Session)).Should().BeNull("the 15 minutes are over");

        await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null);
        await f.Locks.LockAllNowAsync("grant");
        (await f.Locks.ReadBlocksAsync(page.Id, "grant", Session)).Should().BeNull("Lock all now");
    }

    [Fact]
    public async Task Saving_ShouldNeedTheUnlock_AndKeepTheTextEncrypted()
    {
        var f = await CreateAsync();
        var page = await LockedPageAsync(f);
        var stored = await f.Db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == page.Id);
        WikiPageEditorModel Edit(string text, string? session) => new()
        {
            WikiPageId = page.Id, ExpectedContentVersion = stored.ContentVersion, Title = "Diary",
            BlocksJson = WikiBlockJson.Serialize([Text(text)]), LockSessionId = session
        };

        await f.Wiki.Invoking(w => w.SavePageAsync(Edit("overwrite", null), "grant")).Should().ThrowAsync<SentinelPageLockedException>();
        await f.Wiki.Invoking(w => w.SavePageAsync(Edit("overwrite", Session), "grant")).Should().ThrowAsync<SentinelPageLockedException>("not unlocked");

        await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null);
        await f.Wiki.Invoking(w => w.SavePageAsync(Edit("overwrite", Session), "sam")).Should().ThrowAsync<SentinelPageLockedException>("sam isn't the owner");
        var saved = await f.Wiki.SavePageAsync(Edit($"edited {Secret}", Session), "grant", createRevisionCheckpoint: true);

        saved.BlocksJson.Should().Contain("edited", "the editor gets the real content back");
        (await ContainsAnywhereAsync(f.Connection, Secret)).Should().BeFalse();
        (await f.Locks.ReadBlocksAsync(page.Id, "grant", Session)).Should().Contain($"edited {Secret}");
    }

    [Fact]
    public async Task RemovingTheLock_ShouldRestoreEverything_AndAnAdminRemovalTellsTheOwner()
    {
        var f = await CreateAsync();
        var page = await LockedPageAsync(f);

        await f.Locks.Invoking(l => l.RemoveLockAsync(page.Id, "grant", Session)).Should().ThrowAsync<SentinelLockException>("unlock first");
        await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null);
        await f.Locks.RemoveLockAsync(page.Id, "grant", Session);

        var restored = await f.Db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == page.Id);
        restored.LockedBy.Should().BeNull();
        restored.BlocksJson.Should().Contain(Secret);
        (await f.Db.WikiPageRevisions.AsNoTracking().Where(r => r.WikiPageId == page.Id).ToListAsync()).Should().OnlyContain(r => r.ContentProtected == null);
        (await f.Db.PageTags.AsNoTracking().AnyAsync(t => t.WikiPageId == page.Id && t.Tag == "private-tag")).Should().BeTrue();

        await f.Locks.LockAsync(page.Id, "grant");
        await f.Locks.Invoking(l => l.AdminRemoveLockAsync(page.Id, "sam")).Should().ThrowAsync<SentinelLockException>();
        f.Db.AppUsers.Add(new AppUser { Username = "kim", Role = AppRoles.Admin });
        await f.Db.SaveChangesAsync();
        await f.Locks.AdminRemoveLockAsync(page.Id, "kim");

        (await f.Db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == page.Id)).BlocksJson.Should().Contain(Secret);
        (await f.Db.SentinelNotifications.AsNoTracking().SingleAsync(n => n.Kind == "lock-removed")).Username.Should().Be("grant");
    }

    [Fact]
    public async Task Pin_ShouldStopAfterFiveWrongTries_AndChangingItNeedsTheCurrentOne()
    {
        var f = await CreateAsync();
        await f.Locks.Invoking(l => l.SetPinAsync("grant", Session, null, "12ab56")).Should().ThrowAsync<SentinelLockException>();
        await f.Locks.SetPinAsync("grant", Session, null, "246810");
        await f.Locks.Invoking(l => l.SetPinAsync("grant", Session, "000000", "135791")).Should().ThrowAsync<SentinelLockException>();

        // The wrong current PIN above counts too: three more wrong tries make five.
        for (var i = 0; i < 3; i++)
            await f.Locks.Invoking(l => l.UnlockWithPinAsync("grant", Session, "999999", null)).Should().ThrowAsync<SentinelLockException>().WithMessage("Wrong PIN*");
        await f.Locks.Invoking(l => l.UnlockWithPinAsync("grant", Session, "999999", null)).Should().ThrowAsync<SentinelLockException>().WithMessage("*paused*");
        // Even the right PIN is refused while paused.
        await f.Locks.Invoking(l => l.UnlockWithPinAsync("grant", Session, "246810", null)).Should().ThrowAsync<SentinelLockException>().WithMessage("Too many*");

        f.Time.Advance(TimeSpan.FromMinutes(16));
        (await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null)).Should().BeGreaterThan(0);
        await f.Locks.Invoking(l => l.UnlockWithPinAsync("grant", null, "246810", null)).Should().ThrowAsync<SentinelLockException>().WithMessage("Sign out*");
        await f.Locks.SetPinAsync("grant", Session, "246810", "135791");
        (await f.Locks.UnlockWithPinAsync("grant", Session, "135791", null)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Locking_ShouldNeedAnUnlockMethodAndEditAccess_AndCoverSubPages()
    {
        var f = await CreateAsync();
        var parent = await f.Wiki.SavePageAsync(new WikiPageEditorModel { Title = "Parent", BlocksJson = WikiBlockJson.Serialize([Text("p")]) }, "grant");
        var child = await f.Wiki.SavePageAsync(new WikiPageEditorModel { Title = "Child", ParentWikiPageId = parent.Id, BlocksJson = WikiBlockJson.Serialize([Text(Secret)]) }, "grant");

        await f.Locks.Invoking(l => l.LockAsync(parent.Id, "grant")).Should().ThrowAsync<SentinelLockException>().WithMessage("Set up*");
        await f.Locks.SetPinAsync("sam", Session, null, "112233");
        await f.Locks.Invoking(l => l.LockAsync(parent.Id, "sam")).Should().ThrowAsync<SentinelLockException>().WithMessage("*edit access*");
        await f.Locks.SetPinAsync("grant", Session, null, "246810");

        (await f.Locks.LockAsync(parent.Id, "grant")).Should().Be(2);
        (await f.Db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == child.Id)).LockedBy.Should().Be("grant");
        (await ContainsAnywhereAsync(f.Connection, Secret)).Should().BeFalse();
    }

    [Fact]
    public async Task OtherWriters_ShouldBeRefused_RatherThanOverwriteALockedPage()
    {
        var f = await CreateAsync();
        var daily = new DailyNoteService(f.Db, f.Wiki, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var day = await daily.AppendAsync(today, "Morning", Secret, "grant");
        await f.Locks.SetPinAsync("grant", Session, null, "246810");
        await f.Locks.LockAsync(day.Id, "grant");

        await daily.Invoking(d => d.AppendAsync(today, "Later", "more", "grant")).Should().ThrowAsync<SentinelPageLockedException>();
        await f.Wiki.Invoking(w => w.DuplicatePageAsync(day.Id, "grant")).Should().ThrowAsync<SentinelPageLockedException>();
        var revision = await f.Db.WikiPageRevisions.AsNoTracking().FirstAsync(r => r.WikiPageId == day.Id);
        await f.Wiki.Invoking(w => w.RevertToRevisionAsync(day.Id, revision.Id, "grant")).Should().ThrowAsync<SentinelPageLockedException>();
        (await f.Locks.ReadBlocksAsync(day.Id, "grant", Session)).Should().BeNull();
        await f.Locks.UnlockWithPinAsync("grant", Session, "246810", null);
        (await f.Locks.ReadBlocksAsync(day.Id, "grant", Session)).Should().Contain(Secret, "nothing was overwritten");
    }

    private static async Task<WikiPage> LockedPageAsync(Fixture f)
    {
        var page = await f.Wiki.SavePageAsync(new WikiPageEditorModel { Title = "Diary", BlocksJson = WikiBlockJson.Serialize([Text($"Notes {Secret} #private-tag")]) }, "grant", createRevisionCheckpoint: true);
        await f.Locks.SetPinAsync("grant", Session, null, "246810");
        await f.Locks.LockAsync(page.Id, "grant");
        return page;
    }

    // Every TEXT value in every table.
    private static async Task<bool> ContainsAnywhereAsync(SqliteConnection connection, string needle)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "select name from sqlite_master where type = 'table' and name not like 'sqlite_%'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"select * from \"{table}\"";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (!reader.IsDBNull(i) && reader.GetValue(i) is string value && value.Contains(needle, StringComparison.Ordinal)) return true;
                }
            }
        }
        return false;
    }

    private sealed record Fixture(ApplicationDbContext Db, SqliteConnection Connection, WikiService Wiki, SentinelPageLockService Locks, ManualTime Time);

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var crypto = new DataProtectionPageLockCrypto(new EphemeralDataProtectionProvider());
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var wiki = new WikiService(db, lockCrypto: crypto);
        return new Fixture(db, connection, wiki, new SentinelPageLockService(db, new SentinelAccessService(db), crypto, time), time);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
