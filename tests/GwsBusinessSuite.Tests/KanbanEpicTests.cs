using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Epics (K9e): the board's Epic select, with progress per epic.
public sealed class KanbanEpicTests : IAsyncLifetime
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
        _id = await _boards.CreateBoardAsync("Website relaunch", "personal", "grant");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<KanbanBoard> BoardAsync() => (await _boards.GetBoardAsync(_id))!;

    private async Task JoinAsync(string epicId, params Guid[] cards)
    {
        var board = await BoardAsync();
        await _boards.SetCardsValueAsync(_id, cards, board.Fields.EpicPropertyId!.Value, _ => JsonValue.Create(epicId), "grant");
    }

    [Fact]
    public async Task Epics_ShouldGroupCardsAcrossColumns_AndShowHowFarAlongTheyAre()
    {
        (await BoardAsync()).Fields.EpicPropertyId.Should().BeNull("a board has no Epic field until its first epic");
        (await _boards.ListEpicsAsync(_id)).Should().BeEmpty();

        var checkout = await _boards.CreateEpicAsync(_id, "Checkout redesign", "grant");
        var search = await _boards.CreateEpicAsync(_id, "Search", "grant");
        var board = await BoardAsync();
        board.EpicOptions!.Select(option => option.Label).Should().Equal("Checkout redesign", "Search");
        board.EpicOptions![0].Color.Should().NotBe(board.EpicOptions![1].Color);

        var todo = board.Columns.Single(c => c.Label == "To do").OptionId;
        var done = board.Columns.Single(c => c.Label == "Done").OptionId;
        var a = await _boards.AddCardAsync(_id, todo, "Basket page", "grant");
        var b = await _boards.AddCardAsync(_id, todo, "Payment step", "grant");
        var c = await _boards.AddCardAsync(_id, done, "Address form", "grant");
        await JoinAsync(checkout, a, b, c);

        var cards = (await BoardAsync()).Columns.SelectMany(column => column.Cards).ToList();
        cards.Single(card => card.RowId == a).Epic!.Label.Should().Be("Checkout redesign");
        cards.Single(card => card.RowId == a).EpicOptionId.Should().Be(checkout);

        var epics = await _boards.ListEpicsAsync(_id);
        epics.Single(e => e.OptionId == checkout).Should().Match<KanbanEpicView>(e => e.Cards == 3 && e.Done == 1 && e.Open == 2 && e.Percent == 33);
        epics.Single(e => e.OptionId == search).Should().Match<KanbanEpicView>(e => e.Cards == 0 && e.Forecast == null);

        await FluentActions.Awaiting(() => _boards.CreateEpicAsync(_id, "search", "grant"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("There's already an epic called \"search\".*");
    }

    [Fact]
    public async Task UpdateAndDelete_ShouldRenameAnEpic_AndLeaveItsCardsWhereTheyAre()
    {
        var epic = await _boards.CreateEpicAsync(_id, "Checkout", "grant");
        var todo = (await BoardAsync()).Columns.Single(c => c.Label == "To do").OptionId;
        var card = await _boards.AddCardAsync(_id, todo, "Basket page", "grant");
        await JoinAsync(epic, card);

        await _boards.UpdateEpicAsync(_id, epic, "Checkout v2", "#be185d", "grant");
        var renamed = (await BoardAsync()).Columns.SelectMany(column => column.Cards).Single().Epic!;
        renamed.Should().Be(new KanbanLabel("Checkout v2", "#be185d"));

        await _boards.DeleteEpicAsync(_id, epic, "grant");
        var board = await BoardAsync();
        board.EpicOptions.Should().BeEmpty();
        board.Columns.Single(c => c.Label == "To do").Cards.Single().Should().Match<KanbanCard>(c => c.RowId == card && c.Epic == null && c.EpicOptionId == null);
    }

    [Fact]
    public void Swimlanes_ShouldOfferEpics()
    {
        KanbanSwimlanes.All.Should().Contain(KanbanSwimlanes.Epic);
    }
}
