using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Boards over Sentinel databases (kanban-boards-2026-10.md, K1).
public sealed class KanbanBoardServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ApplicationDbContext _db = null!;
    private WikiDatabaseService _databases = null!;
    private KanbanBoardService _boards = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _databases = new WikiDatabaseService(_db);
        _boards = new KanbanBoardService(_db, _databases);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static IReadOnlyList<string> Titles(KanbanBoard board, string column) =>
        board.Columns.Single(c => c.Label == column).Cards.Select(card => card.Title).ToList();

    private string OptionId(KanbanBoard board, string column) => board.Columns.Single(c => c.Label == column).OptionId!;

    [Fact]
    public async Task CreateBoard_ShouldLayOutTheTemplate_AsASentinelDatabase()
    {
        var id = await _boards.CreateBoardAsync("Website relaunch", "personal", "grant");

        var board = (await _boards.GetBoardAsync(id))!;
        board.Title.Should().Be("Website relaunch");
        board.Columns.Select(c => c.Label).Should().Equal("Backlog", "To do", "Doing", "Review", "Done");
        board.Columns[^1].Group.Should().Be(WikiDatabaseStatusGroups.Complete);
        board.Fields.PriorityPropertyId.Should().NotBeNull();
        board.Fields.LabelsPropertyId.Should().NotBeNull();
        board.Fields.DuePropertyId.Should().NotBeNull();
        board.Fields.AssigneePropertyId.Should().NotBeNull();
        board.Fields.EstimatePropertyId.Should().NotBeNull();
        board.Fields.KeyPropertyId.Should().NotBeNull();

        var database = (await _databases.GetDatabaseAsync(id))!;
        var views = database.Views.OrderBy(view => view.SortOrder).ToList();
        views.Select(view => view.Type).Should().Equal([WikiDatabaseViewTypes.Board, WikiDatabaseViewTypes.Table], "Sentinel opens the first view, the same board");
        WikiDatabaseViewConfigJson.Parse(views[0].ConfigJson).GroupByPropertyId.Should().Be(board.Fields.StatusPropertyId.ToString());
        (await _boards.ListBoardsAsync()).Should().ContainSingle(summary => summary.DatabaseId == id && summary.CardCount == 0);
    }

    [Fact]
    public async Task AddCard_ShouldGoToTheTopOfItsColumn_WithTheBoardsId()
    {
        var id = await _boards.CreateBoardAsync("Website relaunch", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;

        await _boards.AddCardAsync(id, OptionId(board, "To do"), "First", "grant");
        await _boards.AddCardAsync(id, OptionId(board, "To do"), "Second", "grant");

        board = (await _boards.GetBoardAsync(id))!;
        Titles(board, "To do").Should().Equal("Second", "First");
        board.Columns.Single(c => c.Label == "To do").Cards.Select(card => card.Key).Should().Equal("WEB-2", "WEB-1");
        await _boards.Invoking(service => service.AddCardAsync(id, null, "  ", "grant")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MoveCard_ShouldLandBeforeItsAnchor_WhateverSubItemsShareTheColumn()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var doing = OptionId(board, "Doing");
        var a = await _boards.AddCardAsync(id, doing, "A", "grant");
        var b = await _boards.AddCardAsync(id, doing, "B", "grant");
        var c = await _boards.AddCardAsync(id, doing, "C", "grant");
        // A sub-item in the same column: Sentinel orders it with the cards, the board doesn't show it.
        var values = new JsonObject();
        WikiPropertyValues.SetText(values, board.Fields.StatusPropertyId, doing);
        await _databases.SaveRowAsync(id, new WikiDatabaseRowEditor { ParentRowId = a, Values = values.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone()) }, "grant");
        Titles((await _boards.GetBoardAsync(id))!, "Doing").Should().Equal("C", "B", "A");

        await _boards.MoveCardAsync(id, c, doing, a, "grant");
        board = (await _boards.GetBoardAsync(id))!;
        Titles(board, "Doing").Should().Equal("B", "C", "A");
        board.Columns.Single(col => col.Label == "Doing").Cards.Single(card => card.Title == "A").SubItemCount.Should().Be(1);

        await _boards.MoveCardAsync(id, b, OptionId(board, "Done"), null, "grant");
        board = (await _boards.GetBoardAsync(id))!;
        Titles(board, "Doing").Should().Equal("C", "A");
        Titles(board, "Done").Should().Equal("B");
        (await _boards.ListBoardsAsync()).Single().OpenCount.Should().Be(2, "Done is the complete group");
    }

    [Fact]
    public async Task Card_ShouldShowLabelsPriorityAndChecklist_FromItsRowAndPage()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var row = await _boards.AddCardAsync(id, OptionId(board, "Doing"), "Ship it", "grant");
        var database = (await _databases.GetDatabaseAsync(id))!;
        var labels = database.Properties.Single(p => p.Id == board.Fields.LabelsPropertyId);
        var priority = database.Properties.Single(p => p.Id == board.Fields.PriorityPropertyId);
        var saved = database.Rows.Single(r => r.Id == row);
        var values = WikiPropertyValues.ParseObject(saved.PropertyValuesJson);
        WikiPropertyValues.SetMultiSelect(values, labels.Id, [WikiDatabasePropertyConfig.GetOptions(labels).First(o => o.Label == "Bug").Id]);
        WikiPropertyValues.SetText(values, priority.Id, WikiDatabasePropertyConfig.GetOptions(priority).First(o => o.Label == "High").Id);
        WikiBlock Todo(string text, bool done) => new(Guid.NewGuid(), WikiBlockTypes.ToDo, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string> { ["checked"] = done ? "true" : "false" });
        await _databases.SaveRowAsync(id, new WikiDatabaseRowEditor
        {
            Id = row,
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone()),
            BlocksJson = WikiBlockJson.Serialize([Todo("Write it", true), Todo("Test it", false), Todo("", false)])
        }, "grant");

        var card = (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Single();
        card.Labels.Should().ContainSingle(label => label.Label == "Bug");
        card.Priority!.Label.Should().Be("High");
        (card.ChecklistDone, card.ChecklistTotal).Should().Be((1, 2), "an empty to-do line isn't an item");
    }

    // K2: the drawer edits fields while Sentinel's page panel autosaves the body beside them.
    [Fact]
    public async Task APageOnlySave_ShouldKeepPropertiesChangedSinceThePageOpened()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var row = await _boards.AddCardAsync(id, OptionId(board, "Doing"), "Ship it", "grant");
        var openedWith = (await _databases.GetDatabaseAsync(id))!.Rows.Single(r => r.Id == row);

        await _boards.SetCardsValueAsync(id, [row], board.Fields.PriorityPropertyId!.Value, _ => JsonValue.Create(board.PriorityOptions[0].Id), "grant");
        await _databases.SaveRowAsync(id, new WikiDatabaseRowEditor
        {
            Id = row,
            BlocksJson = "[]",
            Values = WikiPropertyValues.ParseObject(openedWith.PropertyValuesJson).ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone()),
            KeepPropertyValues = true
        }, "grant");

        (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Single().Priority!.Label
            .Should().Be("Urgent", "the page save carried stale values but kept the stored ones");
    }

    [Fact]
    public async Task SetCardsValue_ShouldReturnOnlyWhatChanged_AndRestoreShouldPutEachBack()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var a = await _boards.AddCardAsync(id, OptionId(board, "To do"), "A", "grant");
        var b = await _boards.AddCardAsync(id, OptionId(board, "To do"), "B", "grant");
        var labels = board.Fields.LabelsPropertyId!.Value;
        var bug = board.LabelOptions.Single(o => o.Label == "Bug").Id;
        var feature = board.LabelOptions.Single(o => o.Label == "Feature").Id;
        await _boards.SetCardsValueAsync(id, [a], labels, _ => new JsonArray(JsonValue.Create(bug)), "grant");

        JsonNode? AddFeature(JsonNode? current)
        {
            var ids = current is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToList() : [];
            if (!ids.Contains(feature)) ids.Add(feature);
            return new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }
        var before = await _boards.SetCardsValueAsync(id, [a, b], labels, AddFeature, "grant");
        var unchanged = await _boards.SetCardsValueAsync(id, [a, b], labels, AddFeature, "grant");

        before.Keys.Should().BeEquivalentTo([a, b]);
        unchanged.Should().BeEmpty("both already had the label");
        var cards = (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "To do").Cards;
        cards.Single(c => c.RowId == a).Labels.Select(l => l.Label).Should().Equal("Bug", "Feature");

        await _boards.RestoreCardValuesAsync(id, labels, before, "grant");
        cards = (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "To do").Cards;
        cards.Single(c => c.RowId == a).Labels.Select(l => l.Label).Should().Equal("Bug");
        cards.Single(c => c.RowId == b).Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task SubItems_ShouldListAddAndTickDone_AndTrashShouldRestore()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var card = await _boards.AddCardAsync(id, OptionId(board, "Doing"), "Launch", "grant");

        var first = await _boards.AddSubItemAsync(id, card, "Write copy", "grant");
        await _boards.AddSubItemAsync(id, card, "Test forms", "grant");
        await _boards.SetSubItemDoneAsync(id, first, true, "grant");

        var items = await _boards.ListSubItemsAsync(id, card);
        items.Select(i => (i.Title, i.Done)).Should().Equal(("Write copy", true), ("Test forms", false));
        (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Single().SubItemCount.Should().Be(2);

        await _boards.TrashCardsAsync(id, [card], "grant");
        (await _boards.GetBoardAsync(id))!.Columns.SelectMany(c => c.Cards).Should().BeEmpty();
        await _boards.RestoreCardsAsync(id, [card], "grant");
        (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Should().ContainSingle(c => c.Title == "Launch");
    }

    // K3/K5: the change log behind card age and flow metrics, written on save whatever the path.
    [Fact]
    public async Task EveryStatusOrSelectChange_ShouldBeLogged_ButNotAPageOnlySave()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var todo = OptionId(board, "To do");
        var doing = OptionId(board, "Doing");
        var card = await _boards.AddCardAsync(id, todo, "Ship it", "grant");
        await _boards.MoveCardAsync(id, card, doing, null, "grant");
        await _boards.SetCardsValueAsync(id, [card], board.Fields.PriorityPropertyId!.Value, _ => JsonValue.Create(board.PriorityOptions[1].Id), "grant");
        await _boards.SetCardsValueAsync(id, [card], board.Fields.EstimatePropertyId!.Value, _ => JsonValue.Create(3m), "grant");
        await _databases.SaveRowAsync(id, new WikiDatabaseRowEditor { Id = card, BlocksJson = "[]", KeepPropertyValues = true }, "grant");

        var log = await _db.WikiDatabaseRowStatusChanges.AsNoTracking().Where(change => change.RowId == card).ToListAsync();
        log.Where(change => change.PropertyId == board.Fields.StatusPropertyId).Select(change => (change.FromOptionId, change.ToOptionId))
            .Should().Equal((null, todo), (todo, doing));
        log.Should().ContainSingle(change => change.PropertyId == board.Fields.PriorityPropertyId && change.ToOptionId == board.PriorityOptions[1].Id);
        log.Should().HaveCount(3, "a number and a page-only save aren't column or select changes");
        log.Should().OnlyContain(change => change.WikiDatabaseId == id && change.AtUnixSeconds > 0 && change.CreatedBy == "grant");
    }

    [Fact]
    public async Task AnySentinelDatabaseWithAStatus_ShouldOpenAsABoard_AndOneWithoutShouldNot()
    {
        var plain = await _databases.CreateDatabaseAsync("Reading list", null, "grant");
        var tasks = await _databases.CreateDatabaseAsync("Tasks", null, "grant");
        await _databases.SavePropertyAsync(tasks.Id, new WikiDatabasePropertyEditor
        {
            Name = "State",
            Type = WikiDatabasePropertyTypes.Status,
            Options = [new("todo", "Open", "#2563eb", WikiDatabaseStatusGroups.ToDo), new("done", "Closed", "red;background:url(x)", WikiDatabaseStatusGroups.Complete)]
        }, "grant");

        (await _boards.ListBoardsAsync()).Select(board => board.DatabaseId).Should().Equal(tasks.Id);
        (await _boards.GetBoardAsync(plain.Id)).Should().BeNull();
        var board = (await _boards.GetBoardAsync(tasks.Id))!;
        board.Columns.Select(c => c.Color).Should().Equal(["#2563eb", "#78716c"], "a colour that isn't one never reaches a style attribute");
    }
}
