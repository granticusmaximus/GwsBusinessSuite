using FluentAssertions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Boards K9g, sharing a board with the team (31-10-26): a Sentinel View or Edit grant on the
// board's database. Every change checks it in the service, whichever page, panel or API asked.
public sealed class KanbanBoardSharingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _db = null!;
    private KanbanBoardService _boards = null!;
    private SentinelAccessService _access = null!;
    private Guid _id;
    private Guid _other;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true },
            new AppUser { Username = "ann", Role = AppRoles.Author, IsActive = true });
        await _db.SaveChangesAsync();
        _access = new SentinelAccessService(_db);
        _boards = new KanbanBoardService(_db, new WikiDatabaseService(_db), _access);
        _id = await _boards.CreateBoardAsync("Website relaunch", "personal", "grant");
        _other = await _boards.CreateBoardAsync("Private plans", "personal", "grant");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ABoardNotSharedWithSomeone_ShouldBeInvisibleAndUnchangeableToThem()
    {
        (await _boards.GetAccessAsync(_id, "ann")).Should().Be(new KanbanBoardAccess(false, false, false));
        (await _boards.ListBoardsForAsync("ann")).Should().BeEmpty();
        await _boards.Invoking(b => b.AddCardAsync(_id, null, "Sneaky", "ann")).Should().ThrowAsync<InvalidOperationException>();

        (await _boards.GetAccessAsync(_id, "grant")).IsAdmin.Should().BeTrue();
        (await _boards.ListBoardsForAsync("grant")).Should().HaveCount(2);
    }

    [Fact]
    public async Task AViewGrant_ShouldShowOnlyThatBoard_ButChangeNothing()
    {
        var board = (await _boards.GetBoardAsync(_id))!;
        var card = await _boards.AddCardAsync(_id, board.Columns[0].OptionId, "Write the FAQ", "grant");
        await _access.SetPermissionAsync(_id, true, "ann", SentinelAccessLevels.View, "grant");

        (await _boards.GetAccessAsync(_id, "ann")).Should().Be(new KanbanBoardAccess(true, false, false));
        (await _boards.ListBoardsForAsync("ann")).Select(b => b.DatabaseId).Should().Equal(_id);
        await _boards.Invoking(b => b.MoveCardAsync(_id, card, board.Columns[1].OptionId, null, "ann"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*look at only*");
        await _boards.Invoking(b => b.RenameBoardAsync(_id, "Mine now", null, "ann")).Should().ThrowAsync<InvalidOperationException>();
        await _boards.Invoking(b => b.AddColumnAsync(_id, "Extra", null, "ann")).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AnEditGrant_ShouldChangeCards_ButNotWhatReachesPastTheBoard()
    {
        var board = (await _boards.GetBoardAsync(_id))!;
        await _access.SetPermissionAsync(_id, true, "ann", SentinelAccessLevels.Edit, "grant");

        (await _boards.GetAccessAsync(_id, "ann")).Should().Be(new KanbanBoardAccess(true, true, false));
        var card = await _boards.AddCardAsync(_id, board.Columns[0].OptionId, "Write the FAQ", "ann");
        await _boards.MoveCardAsync(_id, card, board.Columns[1].OptionId, null, "ann");
        await _boards.CreateEpicAsync(_id, "Launch", "ann");

        await _boards.Invoking(b => b.DeleteBoardAsync(_id, "ann")).Should().ThrowAsync<InvalidOperationException>().WithMessage("Only an admin*");
        await _boards.Invoking(b => b.SetEmailTokenAsync(_id, true, "ann")).Should().ThrowAsync<InvalidOperationException>();
        await _boards.Invoking(b => b.LinkAsync(_id, card, KanbanLinkKinds.Contact, Guid.NewGuid(), "ann")).Should().ThrowAsync<InvalidOperationException>();
        await _boards.Invoking(b => b.CreateBoardAsync("Ann's", "personal", "ann")).Should().ThrowAsync<InvalidOperationException>();
        await _boards.Invoking(b => b.AddCardAsync(_other, null, "Elsewhere", "ann")).Should().ThrowAsync<InvalidOperationException>("only the board shared");
    }

    [Fact]
    public async Task TheAppsOwnActors_ShouldStillChangeBoards()
    {
        var board = (await _boards.GetBoardAsync(_id))!;
        var card = await _boards.AddCardAsync(_id, board.Columns[0].OptionId, "From a rule", "automation-engine");
        await _boards.MoveCardAsync(_id, card, board.Columns[1].OptionId, null, "automation-engine:chained");
    }
}
