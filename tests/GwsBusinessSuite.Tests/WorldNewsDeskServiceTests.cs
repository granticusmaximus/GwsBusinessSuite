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

        Assert.Empty(_news.Store.DeskNotes("e1"));
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
