using FluentAssertions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Editing a board's columns (29-10-26): they're its Status property's options.
public sealed class KanbanColumnEditTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ApplicationDbContext _db = null!;
    private KanbanBoardService _boards = null!;
    private Guid _id;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _boards = new KanbanBoardService(_db, new WikiDatabaseService(_db));
        // Backlog, To do | Doing, Review | Done
        _id = await _boards.CreateBoardAsync("Website relaunch", "personal", "grant");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<KanbanBoard> BoardAsync() => (await _boards.GetBoardAsync(_id))!;

    private async Task<IReadOnlyList<string>> LabelsAsync() => (await BoardAsync()).Columns.Select(c => c.Label).ToList();

    private async Task<string> IdOf(string label) => (await BoardAsync()).Columns.Single(c => c.Label == label).OptionId!;

    [Fact]
    public async Task AddColumn_ShouldLandAtTheEndOfItsGroup_WithAnUnusedColour()
    {
        await _boards.AddColumnAsync(_id, "  QA  ", WikiDatabaseStatusGroups.InProgress, "grant");

        (await LabelsAsync()).Should().Equal("Backlog", "To do", "Doing", "Review", "QA", "Done");
        var qa = (await BoardAsync()).Columns.Single(c => c.Label == "QA");
        qa.Group.Should().Be(WikiDatabaseStatusGroups.InProgress);
        new[] { "#78716c", "#2563eb", "#d97706", "#7c3aed", "#15803d" }.Should().NotContain(qa.Color, "those are taken");

        await FluentActions.Awaiting(() => _boards.AddColumnAsync(_id, "qa", null, "grant"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("There's already a column called \"qa\".*");
        await FluentActions.Awaiting(() => _boards.AddColumnAsync(_id, " ", null, "grant")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpdateColumn_ShouldRenameRecolourAndRegroup_KeepingItsCards()
    {
        var doing = await IdOf("Doing");
        var card = await _boards.AddCardAsync(_id, doing, "Write the FAQ", "grant");

        await _boards.UpdateColumnAsync(_id, doing, "In flight", "#0e7490", WikiDatabaseStatusGroups.InProgress, "grant");
        var board = await BoardAsync();
        board.Columns.Single(c => c.OptionId == doing).Should().Match<KanbanColumn>(c => c.Label == "In flight" && c.Color == "#0e7490"
            && c.Cards.Single().RowId == card);
        // A rename keeps its place.
        (await LabelsAsync()).Should().Equal("Backlog", "To do", "In flight", "Review", "Done");

        await _boards.UpdateColumnAsync(_id, doing, "In flight", "#0e7490", WikiDatabaseStatusGroups.ToDo, "grant");
        // It's now the last To do column.
        (await LabelsAsync()).Should().Equal("Backlog", "To do", "In flight", "Review", "Done");
        (await BoardAsync()).Columns.Single(c => c.OptionId == doing).Group.Should().Be(WikiDatabaseStatusGroups.ToDo);

        var done = await IdOf("Done");
        await FluentActions.Awaiting(() => _boards.UpdateColumnAsync(_id, done, "Done", "#15803d", WikiDatabaseStatusGroups.ToDo, "grant"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Keep at least one Done column*");
        await FluentActions.Awaiting(() => _boards.UpdateColumnAsync(_id, doing, "In flight", "url(javascript:x)", null, "grant"))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MoveColumn_ShouldSwapWithItsNeighbour_OnlyWithinItsGroup()
    {
        await _boards.MoveColumnAsync(_id, await IdOf("Review"), -1, "grant");
        (await LabelsAsync()).Should().Equal("Backlog", "To do", "Review", "Doing", "Done");

        await _boards.MoveColumnAsync(_id, await IdOf("Review"), -1, "grant");
        // To do is another group, so Review stays first in its own.
        (await LabelsAsync()).Should().Equal("Backlog", "To do", "Review", "Doing", "Done");

        await _boards.MoveColumnAsync(_id, await IdOf("Backlog"), 1, "grant");
        (await LabelsAsync()).Should().Equal("To do", "Backlog", "Review", "Doing", "Done");
    }

    [Fact]
    public async Task DeleteColumn_ShouldMoveItsCardsFirst_AndKeepTheBoardWorkable()
    {
        var review = await IdOf("Review");
        var doing = await IdOf("Doing");
        var card = await _boards.AddCardAsync(_id, review, "Check the copy", "grant");
        var settings = await _boards.GetSettingsAsync(_id);
        await _boards.SaveSettingsAsync(_id, settings with { WipLimits = new() { [review] = 3 } }, "grant");

        await FluentActions.Awaiting(() => _boards.DeleteColumnAsync(_id, review, "not-a-column", "grant"))
            .Should().ThrowAsync<ArgumentException>();
        await _boards.DeleteColumnAsync(_id, review, doing, "grant");

        (await LabelsAsync()).Should().Equal("Backlog", "To do", "Doing", "Done");
        (await BoardAsync()).Columns.Single(c => c.OptionId == doing).Cards.Should().ContainSingle(c => c.RowId == card);
        (await _boards.GetSettingsAsync(_id)).WipLimits.Should().NotContainKey(review);

        var done = await IdOf("Done");
        await FluentActions.Awaiting(() => _boards.DeleteColumnAsync(_id, done, null, "grant"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Keep at least one Done column*");
    }

    [Fact]
    public async Task DeleteColumn_ShouldSendCardsItCantShowToANeighbour()
    {
        var todo = await IdOf("To do");
        var card = await _boards.AddCardAsync(_id, todo, "Old idea", "grant");
        await _boards.ArchiveCardsAsync(_id, [card], "grant");
        (await BoardAsync()).Columns.Single(c => c.OptionId == todo).Cards.Should().BeEmpty("the card is archived");

        await _boards.DeleteColumnAsync(_id, todo, null, "grant");

        (await _boards.ListArchivedAsync(_id)).Single(c => c.RowId == card).Column.Should().Be("Backlog", "the other To do column");
    }
}
