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

    // K3: board settings, blocked cards, and when a card entered its column.
    [Fact]
    public async Task Settings_ShouldRoundTrip_PutLimitsOnColumns_AndRefuseNonsense()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        board.Settings.Should().Be(KanbanSettings.Default);

        await _boards.SaveSettingsAsync(id, KanbanSettings.Default with
        {
            WipLimits = new() { [OptionId(board, "Doing")] = 3 },
            SwimlaneBy = KanbanSwimlanes.Priority,
            AgingDays = 4,
            Accent = "red;background:url(x)"
        }, "grant");

        board = (await _boards.GetBoardAsync(id))!;
        board.Columns.Single(c => c.Label == "Doing").WipLimit.Should().Be(3);
        board.Columns.Single(c => c.Label == "To do").WipLimit.Should().BeNull();
        board.Settings!.SwimlaneBy.Should().Be(KanbanSwimlanes.Priority);
        board.Settings.AgingDays.Should().Be(4);
        board.Settings.Accent.Should().Be("#78716c", "a colour that isn't one is replaced");
        await _boards.Invoking(service => service.SaveSettingsAsync(id, KanbanSettings.Default with { SwimlaneBy = "mood" }, "grant")).Should().ThrowAsync<ArgumentException>();
        await _boards.Invoking(service => service.SaveSettingsAsync(id, KanbanSettings.Default with { WipLimits = new() { ["x"] = 0 } }, "grant")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Blocking_ShouldAddTheFieldsToABoardWithoutThem_AndCarryTheReason()
    {
        var tasks = await _databases.CreateDatabaseAsync("Tasks", null, "grant");
        await _databases.SavePropertyAsync(tasks.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Status,
            Options = [new("todo", "Open", "#2563eb", WikiDatabaseStatusGroups.ToDo), new("done", "Closed", "#15803d", WikiDatabaseStatusGroups.Complete)]
        }, "grant");
        var card = await _boards.AddCardAsync(tasks.Id, "todo", "Waiting on legal", "grant");

        await _boards.SetBlockedAsync(tasks.Id, card, true, "  Contract review  ", "grant");
        var blocked = (await _boards.GetBoardAsync(tasks.Id))!.Columns.SelectMany(c => c.Cards).Single();
        (blocked.Blocked, blocked.BlockedReason).Should().Be((true, "Contract review"));

        await _boards.SetBlockedAsync(tasks.Id, card, false, "ignored", "grant");
        var clear = (await _boards.GetBoardAsync(tasks.Id))!.Columns.SelectMany(c => c.Cards).Single();
        (clear.Blocked, clear.BlockedReason).Should().Be((false, null));
        (await _databases.GetDatabaseAsync(tasks.Id))!.Properties.Count(p => p.Name is "Blocked" or "Blocked reason").Should().Be(2, "added once, then reused");
    }

    [Fact]
    public async Task EnteredColumnAt_ShouldBeTheLatestMoveIntoTheColumn_OrCreationBeforeTheLog()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var card = await _boards.AddCardAsync(id, OptionId(board, "To do"), "Ship it", "grant");
        await _boards.MoveCardAsync(id, card, OptionId(board, "Doing"), null, "grant");
        var moved = await _db.WikiDatabaseRowStatusChanges.Where(change => change.RowId == card && change.ToOptionId == OptionId(board, "Doing")).SingleAsync();
        moved.AtUnixSeconds -= 3 * 86400; // as if it moved three days ago
        await _db.SaveChangesAsync();

        var entered = (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Single().EnteredColumnAt!.Value;
        (DateTimeOffset.UtcNow - entered).TotalDays.Should().BeApproximately(3, 0.01);

        _db.WikiDatabaseRowStatusChanges.RemoveRange(_db.WikiDatabaseRowStatusChanges);
        await _db.SaveChangesAsync();
        var row = (await _databases.GetDatabaseAsync(id))!.Rows.Single(r => r.Id == card);
        (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Single().EnteredColumnAt.Should().Be(row.CreatedAt, "no log: its creation");
    }

    // A drop in another swimlane: the move, then the lane's value, through one shared context.
    [Fact]
    public async Task AMoveThenALaneValue_ShouldBothStick()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var card = await _boards.AddCardAsync(id, OptionId(board, "To do"), "Ship it", "grant");
        await _boards.SetCardsValueAsync(id, [card], board.Fields.PriorityPropertyId!.Value, _ => JsonValue.Create(board.PriorityOptions[2].Id), "grant");

        await _boards.MoveCardAsync(id, card, OptionId(board, "Doing"), null, "grant");
        await _boards.SetCardsValueAsync(id, [card], board.Fields.PriorityPropertyId!.Value, _ => JsonValue.Create(board.PriorityOptions[0].Id), "grant");

        var moved = (await _boards.GetBoardAsync(id))!.Columns.Single(c => c.Label == "Doing").Cards.Single();
        moved.Priority!.Label.Should().Be("Urgent");
    }

    // K4: sprints.
    [Fact]
    public async Task CreateSprint_ShouldAddTheSprintField_ToABoardWithout_AndRefuseADuplicateName()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        (await _boards.ListSprintsAsync(id)).Should().BeEmpty();

        var first = await _boards.CreateSprintAsync(id, "", "grant");
        await _boards.CreateSprintAsync(id, "Launch week", "grant");

        var sprints = await _boards.ListSprintsAsync(id);
        sprints.Select(s => (s.Name, s.State)).Should().Equal(("Sprint 1", KanbanSprintStates.Planned), ("Launch week", KanbanSprintStates.Planned));
        sprints[0].OptionId.Should().Be(first);
        (await _boards.GetBoardAsync(id))!.Fields.SprintPropertyId.Should().NotBeNull();
        await _boards.Invoking(service => service.CreateSprintAsync(id, "sprint 1", "grant")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StartSprint_ShouldAllowOneRunningAtATime()
    {
        var id = await _boards.CreateBoardAsync("Ops", "sprint", "grant");
        var one = (await _boards.ListSprintsAsync(id)).Single().OptionId;
        var two = await _boards.CreateSprintAsync(id, "", "grant");
        var today = DateOnly.FromDateTime(DateTime.Today);

        await _boards.Invoking(service => service.StartSprintAsync(id, one, today, today.AddDays(-1), "grant")).Should().ThrowAsync<ArgumentException>();
        await _boards.StartSprintAsync(id, one, today, today.AddDays(13), "grant");
        await _boards.Invoking(service => service.StartSprintAsync(id, two, today, today.AddDays(13), "grant")).Should().ThrowAsync<InvalidOperationException>();

        var running = (await _boards.ListSprintsAsync(id)).Single(s => s.OptionId == one);
        (running.State, running.Start, running.End).Should().Be((KanbanSprintStates.Active, today, today.AddDays(13)));
    }

    [Fact]
    public async Task CompleteSprint_ShouldRecordWhatGotDone_AndMoveTheRestOn()
    {
        var id = await _boards.CreateBoardAsync("Ops", "sprint", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var sprint = (await _boards.ListSprintsAsync(id)).Single().OptionId;
        var sprintField = board.Fields.SprintPropertyId!.Value;
        var estimate = board.Fields.EstimatePropertyId!.Value;
        async Task<Guid> Card(string title, string column, decimal points)
        {
            var card = await _boards.AddCardAsync(id, OptionId(board, column), title, "grant");
            await _boards.SetCardsValueAsync(id, [card], sprintField, _ => JsonValue.Create(sprint), "grant");
            await _boards.SetCardsValueAsync(id, [card], estimate, _ => JsonValue.Create(points), "grant");
            return card;
        }
        await Card("Done one", "Done", 3);
        var open = await Card("Still open", "In progress", 5);
        var today = DateOnly.FromDateTime(DateTime.Today);
        await _boards.StartSprintAsync(id, sprint, today, today.AddDays(13), "grant");

        var view = (await _boards.ListSprintsAsync(id)).Single();
        (view.CardCount, view.DoneCount, view.Points, view.DonePoints, view.UsesEstimates).Should().Be((2, 1, 8m, 3m, true));
        view.Burndown.Should().ContainSingle().Which.Remaining.Should().Be(5, "the finished card's 3 points are off today");

        var summary = await _boards.CompleteSprintAsync(id, sprint, null, "grant");

        summary.Should().Be(new KanbanSprintSummary("Sprint 1", 1, 3m, 1, "Sprint 2"));
        var sprints = await _boards.ListSprintsAsync(id);
        sprints.Single(s => s.Name == "Sprint 1").State.Should().Be(KanbanSprintStates.Done);
        var next = sprints.Single(s => s.Name == "Sprint 2");
        (await _boards.GetBoardAsync(id))!.Columns.SelectMany(c => c.Cards).Single(c => c.RowId == open).SprintOptionId.Should().Be(next.OptionId);
        await _boards.Invoking(service => service.CompleteSprintAsync(id, sprint, "", "grant")).Should().ThrowAsync<InvalidOperationException>("it's already complete");
        (await _boards.GetSettingsAsync(id)).Sprints.Single(s => s.OptionId == sprint).DonePoints.Should().Be(3m);
    }

    [Fact]
    public async Task Burndown_ShouldDropOnTheDayEachCardFinished()
    {
        var id = await _boards.CreateBoardAsync("Ops", "sprint", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        var sprint = (await _boards.ListSprintsAsync(id)).Single().OptionId;
        var cards = new List<Guid>();
        foreach (var title in new[] { "A", "B", "C" })
        {
            var card = await _boards.AddCardAsync(id, OptionId(board, "In progress"), title, "grant");
            await _boards.SetCardsValueAsync(id, [card], board.Fields.SprintPropertyId!.Value, _ => JsonValue.Create(sprint), "grant");
            cards.Add(card);
        }
        await _boards.MoveCardAsync(id, cards[0], OptionId(board, "Done"), null, "grant");
        await _boards.MoveCardAsync(id, cards[1], OptionId(board, "Done"), null, "grant");
        // A finished two days ago, B yesterday; the sprint started three days ago.
        foreach (var (card, daysAgo) in new[] { (cards[0], 2), (cards[1], 1) })
        {
            var change = await _db.WikiDatabaseRowStatusChanges.SingleAsync(c => c.RowId == card && c.ToOptionId == OptionId(board, "Done"));
            change.AtUnixSeconds -= daysAgo * 86400;
        }
        await _db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await _boards.StartSprintAsync(id, sprint, today.AddDays(-3), today.AddDays(10), "grant");

        var burndown = (await _boards.ListSprintsAsync(id)).Single().Burndown;
        burndown.Select(point => point.Remaining).Should().Equal(3m, 2m, 1m, 1m);
        burndown[0].Day.Should().Be(today.AddDays(-3));
    }

    // K5: flow metrics from the change log.
    [Fact]
    public async Task Insights_ShouldMeasureCycleTimeThroughputFlowAndAge()
    {
        var id = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(id))!;
        string Column(string label) => OptionId(board, label);
        // Three cards started and finished at known times, one still in progress.
        var now = DateTimeOffset.UtcNow;
        async Task<Guid> Card(string title, int startedDaysAgo, int? finishedDaysAgo)
        {
            var card = await _boards.AddCardAsync(id, Column("To do"), title, "grant");
            await _boards.MoveCardAsync(id, card, Column("Doing"), null, "grant");
            if (finishedDaysAgo is not null) await _boards.MoveCardAsync(id, card, Column("Done"), null, "grant");
            foreach (var change in await _db.WikiDatabaseRowStatusChanges.Where(c => c.RowId == card).ToListAsync())
            {
                change.AtUnixSeconds = (change.ToOptionId == Column("Done") ? now.AddDays(-finishedDaysAgo!.Value) : now.AddDays(-startedDaysAgo)).ToUnixTimeSeconds()
                    - (change.ToOptionId == Column("To do") ? 3600 : 0);
            }
            await _db.SaveChangesAsync();
            return card;
        }
        await Card("Two days", 10, 8);
        await Card("Four days", 9, 5);
        await Card("Six days", 7, 1);
        await Card("Still going", 3, null);

        var insights = (await _boards.GetInsightsAsync(id, 30))!;

        insights.Cycles.Select(c => (c.Title, Math.Round(c.Days))).Should().Equal(("Two days", 2d), ("Four days", 4d), ("Six days", 6d));
        insights.MedianCycleDays.Should().BeApproximately(4, 0.01);
        insights.P85CycleDays.Should().BeApproximately(6, 0.01);
        insights.Throughput.Sum(w => w.Finished).Should().Be(3);
        insights.Throughput.Should().OnlyContain(w => w.Start.DayOfWeek == DayOfWeek.Monday);
        insights.WorkInProgress.Should().Be(1);
        insights.Aging.Should().ContainSingle().Which.Days.Should().BeApproximately(3, 0.05);

        var doneIndex = insights.FlowColumns.ToList().FindIndex(c => c.Label == "Done");
        var doingIndex = insights.FlowColumns.ToList().FindIndex(c => c.Label == "Doing");
        doneIndex.Should().Be(0, "complete columns are the bottom band");
        insights.Flow[^1].Counts[doneIndex].Should().Be(3);
        insights.Flow[^1].Counts[doingIndex].Should().Be(1);
        insights.Flow.Single(day => day.Day == DateOnly.FromDateTime(DateTime.Now.AddDays(-6))).Counts[doneIndex].Should().Be(1, "only the first had finished six days ago");
        await _boards.Invoking(service => service.GetInsightsAsync(id, 45)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void BoardFilterPreferences_ShouldTakeASmallJsonObject_AndNothingElse()
    {
        var key = GwsBusinessSuite.Application.AdminPortal.UserPreferenceRules.BoardFilters(Guid.NewGuid());
        GwsBusinessSuite.Application.AdminPortal.UserPreferenceRules.DefaultFor(key).Should().Be("{}");
        GwsBusinessSuite.Application.AdminPortal.UserPreferenceRules.IsAllowed(key, "{\"mine\":true}").Should().BeTrue();
        GwsBusinessSuite.Application.AdminPortal.UserPreferenceRules.IsAllowed(key, "[1]").Should().BeFalse();
        GwsBusinessSuite.Application.AdminPortal.UserPreferenceRules.IsAllowed(key, "{not json").Should().BeFalse();
        GwsBusinessSuite.Application.AdminPortal.UserPreferenceRules.IsAllowed(key, "{\"x\":\"" + new string('a', 2100) + "\"}").Should().BeFalse();
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

    [Fact]
    public async Task CardTemplates_ShouldMakeNamedCards_AtTheTopOfTheChosenColumn()
    {
        // K6: a template keeps a card's fields (Sentinel leaves the title out); a card made from it
        // on the board is named after the template and goes where it's asked, or where it was saved.
        var boardId = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(boardId))!;
        var source = await _boards.AddCardAsync(boardId, OptionId(board, "Review"), "Weekly report", "grant");
        await _boards.AddCardAsync(boardId, OptionId(board, "To do"), "Already here", "grant");
        await _boards.SetCardsValueAsync(boardId, [source], board.Fields.EstimatePropertyId!.Value, _ => System.Text.Json.Nodes.JsonValue.Create(2m), "grant");
        var template = await _databases.CreateRowTemplateFromRowAsync(boardId, source, "Weekly report", "grant");

        var inToDo = await _boards.AddCardFromTemplateAsync(boardId, template.Id, OptionId(board, "To do"), "grant");
        var asSaved = await _boards.AddCardFromTemplateAsync(boardId, template.Id, null, "grant");

        board = (await _boards.GetBoardAsync(boardId))!;
        Titles(board, "To do").Should().Equal("Weekly report", "Already here");
        Titles(board, "Review").Should().Equal("Weekly report", "Weekly report");
        board.Columns.SelectMany(column => column.Cards).Single(card => card.RowId == inToDo).Estimate.Should().Be(2m);
        board.Columns.Single(column => column.Label == "Review").Cards[0].RowId.Should().Be(asSaved);
        var missing = () => _boards.AddCardFromTemplateAsync(boardId, Guid.NewGuid(), null, "grant");
        await missing.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cards_ShouldTakeACoverFromTheirPage_AndRichCardsItsFirstLines()
    {
        // K7: a card with no cover of its own shows the first picture in its page; rich cards add
        // the page's first text. Backgrounds are a preset or a safe image.
        var boardId = await _boards.CreateBoardAsync("Look", "personal", "grant");
        var board = (await _boards.GetBoardAsync(boardId))!;
        var cardId = await _boards.AddCardAsync(boardId, OptionId(board, "To do"), "Hero shot", "grant");
        var blocks = WikiBlockJson.Serialize(
        [
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Image, 0, [], new Dictionary<string, string> { ["url"] = "javascript:alert(1)" }),
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Shoot the new hero image in the studio, then pick three.")], new Dictionary<string, string>()),
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Image, 0, [], new Dictionary<string, string> { ["url"] = "/media/hero.jpg" })
        ]);
        var tracked = await _db.WikiDatabaseRows.SingleAsync(item => item.Id == cardId);
        tracked.BlocksJson = blocks;
        await _db.SaveChangesAsync();

        Card(await _boards.GetBoardAsync(boardId), cardId).CoverImageUrl.Should().Be("/media/hero.jpg");
        Card(await _boards.GetBoardAsync(boardId), cardId).Snippet.Should().BeNull("only rich cards carry their first lines");

        await _boards.SaveSettingsAsync(boardId, KanbanSettings.Default with { CardSize = KanbanCardSizes.Rich, Background = "aurora", Accent = "#ec4899" }, "grant");
        var rich = (await _boards.GetBoardAsync(boardId))!;
        Card(rich, cardId).Snippet.Should().Be("Shoot the new hero image in the studio, then pick three.");
        var look = rich.Settings ?? throw new InvalidOperationException("The board has no settings.");
        look.Background.Should().Be("aurora");
        look.Accent.Should().Be("#ec4899");

        await _boards.SaveSettingsAsync(boardId, look with { Background = "/media/wall.jpg" }, "grant");
        (await _boards.GetSettingsAsync(boardId)).Background.Should().Be("/media/wall.jpg");
        await _boards.SaveSettingsAsync(boardId, look with { Background = KanbanLook.NoBackground }, "grant");
        (await _boards.GetSettingsAsync(boardId)).Background.Should().BeNull();
        var unsafeImage = () => _boards.SaveSettingsAsync(boardId, look with { Background = "https://x.example/a.jpg);color:red" }, "grant");
        await unsafeImage.Should().ThrowAsync<ArgumentException>();
        var unknown = () => _boards.SaveSettingsAsync(boardId, look with { Background = "lava" }, "grant");
        await unknown.Should().ThrowAsync<ArgumentException>();

        static KanbanCard Card(KanbanBoard? board, Guid id) => board!.Columns.SelectMany(column => column.Cards).Single(card => card.RowId == id);
    }

    [Fact]
    public async Task QuickCards_ShouldSaveEveryField_AndForecastsUseRealFinishes()
    {
        // K8: a one-line card's fields go in with the card; forecasts come from logged finishes.
        var boardId = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(boardId))!;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var quick = KanbanQuickParse.Parse("Fix login !urgent #bug @grant @sam tomorrow ~2", board, today, "grant");
        var id = await _boards.AddQuickCardAsync(boardId, OptionId(board, "To do"), quick, "grant");

        board = (await _boards.GetBoardAsync(boardId))!;
        var card = board.Columns.Single(column => column.Label == "To do").Cards[0];
        card.RowId.Should().Be(id);
        card.Title.Should().Be("Fix login");
        card.Priority!.Label.Should().Be("Urgent");
        card.Labels.Select(label => label.Label).Should().Equal("Bug");
        card.Assignees.Should().Equal("grant", "sam");
        DateOnly.FromDateTime(card.Due!.Value.UtcDateTime).Should().Be(today.AddDays(1));
        card.Estimate.Should().Be(2m);
        var untitled = () => _boards.AddQuickCardAsync(boardId, null, quick with { Title = " " }, "grant");
        await untitled.Should().ThrowAsync<ArgumentException>();

        // Two cards finished on each of the last five days (their moves into Done backdated).
        var done = OptionId(board, "Done");
        var finished = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var row = await _boards.AddCardAsync(boardId, OptionId(board, "To do"), $"Old {i}", "grant");
            await _boards.MoveCardAsync(boardId, row, done, null, "grant");
            finished.Add(row);
        }
        var noon = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        foreach (var (row, index) in finished.Select((row, index) => (row, index)))
        {
            var at = noon.AddDays(-(index / 2 + 1)).ToUnixTimeSeconds();
            foreach (var change in await _db.WikiDatabaseRowStatusChanges.Where(change => change.RowId == row).ToListAsync()) change.AtUnixSeconds = at;
        }
        foreach (var change in await _db.WikiDatabaseRowStatusChanges.Where(change => !finished.Contains(change.RowId)).ToListAsync())
            change.AtUnixSeconds = noon.AddDays(-6).ToUnixTimeSeconds();
        await _db.SaveChangesAsync();

        var insights = (await _boards.GetInsightsAsync(boardId, 30))!;
        insights.DailyDone.Should().Equal(0, 2, 2, 2, 2, 2);
        insights.Forecasts!.Select(forecast => forecast.Key).Should().Equal("open", "doing");
        var open = insights.Forecasts![0].Forecast;
        open.Remaining.Should().Be(1);
        open.CanForecast.Should().BeTrue();
        open.P95.Should().BeOnOrBefore(today.AddDays(3));

        var selected = await _boards.ForecastAsync(boardId, [id, finished[0]]);
        selected!.Remaining.Should().Be(1, "a finished card isn't left to do");
        (await _boards.ForecastAsync(Guid.NewGuid(), [id])).Should().BeNull();
    }

    [Fact]
    public async Task Brief_ShouldGatherTodaysMoves_AndWhatsStuck()
    {
        // K8b: the facts a standup and "what's stuck" are written from.
        var boardId = await _boards.CreateBoardAsync("Ops", "personal", "grant");
        var board = (await _boards.GetBoardAsync(boardId))!;
        var shipped = await _boards.AddCardAsync(boardId, OptionId(board, "Review"), "Press kit", "grant");
        var started = await _boards.AddCardAsync(boardId, OptionId(board, "To do"), "Hero section", "sam");
        var old = await _boards.AddCardAsync(boardId, OptionId(board, "Doing"), "Checkout", "grant");
        var blocked = await _boards.AddCardAsync(boardId, OptionId(board, "To do"), "Legal review", "grant");
        var sub = await _boards.AddSubItemAsync(boardId, started, "A step", "grant");
        // Everything so far happened a fortnight ago.
        foreach (var change in await _db.WikiDatabaseRowStatusChanges.ToListAsync()) change.AtUnixSeconds = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        await _db.SaveChangesAsync();

        var since = DateTimeOffset.UtcNow.AddHours(-1);
        await _boards.MoveCardAsync(boardId, shipped, OptionId(board, "Done"), null, "grant");
        await _boards.MoveCardAsync(boardId, started, OptionId(board, "Doing"), null, "sam");
        await _boards.SetSubItemDoneAsync(boardId, sub, true, "sam");
        await _boards.SetBlockedAsync(boardId, blocked, true, "waiting on legal", "grant");
        await _boards.SaveSettingsAsync(boardId, (await _boards.GetSettingsAsync(boardId)) with { WipLimits = new Dictionary<string, int> { [OptionId(board, "Doing")] = 1 } }, "grant");

        var brief = (await _boards.GetBriefAsync(boardId, since))!;
        brief.Moves.Select(move => (move.Title, move.From, move.To, move.By)).Should().Equal(
            ("Press kit", "Review", "Done", "grant"), ("Hero section", "To do", "Doing", "sam"));
        brief.Stuck.Select(card => card.Title).Should().Equal("Legal review", "Checkout");
        brief.Stuck[0].BlockedReason.Should().Be("waiting on legal");
        brief.Stuck[1].DaysInColumn.Should().BeGreaterThan(13);
        brief.OverLimit.Should().Equal("Doing");
        (await _boards.GetBriefAsync(Guid.NewGuid(), since)).Should().BeNull();
    }

    [Fact]
    public async Task BusinessLinks_ShouldTieCardsToContactsTicketsAndInvoices_AndTimeToTheCard()
    {
        // K8c: a ticket brings its contact; a deleted record shows as gone; a card can be made from
        // a ticket; Done can tell which tickets are still open; time started from a card is its own.
        var boardId = await _boards.CreateBoardAsync("Client work", "personal", "grant");
        var board = (await _boards.GetBoardAsync(boardId))!;
        var ada = new Contact { FullName = "Ada Lovelace", Company = "Analytical Engines" };
        var bob = new Contact { FullName = "Bob Builder" };
        _db.Contacts.AddRange(ada, bob);
        var ticket = new SupportTicket { ContactId = ada.Id, Subject = "Checkout fails with a coupon" };
        var closed = new SupportTicket { ContactId = ada.Id, Subject = "Old login issue", Status = SupportTicketStatuses.Resolved };
        var invoice = new Invoice { ContactId = bob.Id, Title = "October retainer" };
        _db.SupportTickets.AddRange(ticket, closed);
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();

        var card = await _boards.AddCardAsync(boardId, OptionId(board, "Doing"), "Fix checkout", "grant");
        await _boards.LinkAsync(boardId, card, KanbanLinkKinds.Ticket, ticket.Id, "grant");
        await _boards.LinkAsync(boardId, card, KanbanLinkKinds.Ticket, closed.Id, "grant");
        await _boards.LinkAsync(boardId, card, KanbanLinkKinds.Invoice, invoice.Id, "grant");
        await _boards.LinkAsync(boardId, card, KanbanLinkKinds.Ticket, ticket.Id, "grant"); // twice is once

        var links = await _boards.ListLinksAsync(boardId, card);
        links.Select(link => (link.Kind, link.Title)).Should().Equal(
            (KanbanLinkKinds.Contact, "Ada Lovelace"), (KanbanLinkKinds.Contact, "Bob Builder"),
            (KanbanLinkKinds.Ticket, "Checkout fails with a coupon"), (KanbanLinkKinds.Ticket, "Old login issue"),
            (KanbanLinkKinds.Invoice, "October retainer"));
        links.Single(link => link.Kind == KanbanLinkKinds.Ticket && link.Status == SupportTicketStatuses.Open).Detail.Should().Be("Ada Lovelace");
        (await _boards.OpenTicketsAsync(boardId, card)).Select(link => link.TargetId).Should().Equal(ticket.Id);
        (await _boards.GetBoardAsync(boardId))!.Columns.SelectMany(column => column.Cards).Single(item => item.RowId == card).LinkKinds
            .Should().HaveCount(5);

        var bad = () => _boards.LinkAsync(boardId, card, "deal", Guid.NewGuid(), "grant");
        await bad.Should().ThrowAsync<ArgumentException>();
        var missing = () => _boards.LinkAsync(boardId, card, KanbanLinkKinds.Contact, Guid.NewGuid(), "grant");
        await missing.Should().ThrowAsync<InvalidOperationException>();

        (await _boards.SearchLinkTargetsAsync(KanbanLinkKinds.Contact, "engines")).Select(found => found.Title).Should().Equal("Ada Lovelace");
        (await _boards.SearchLinkTargetsAsync(KanbanLinkKinds.Ticket, "coupon")).Single().Detail.Should().Be("Open · Ada Lovelace");
        (await _boards.SearchLinkTargetsAsync(KanbanLinkKinds.Invoice, "retainer")).Single().Id.Should().Be(invoice.Id);
        (await _boards.SearchLinkTargetsAsync(KanbanLinkKinds.Contact, "a")).Should().BeEmpty("one letter is too little to search on");

        // Time started from the card is the card's, billed to the ticket's contact.
        var time = new TimeTrackingService(_db, null!, TimeProvider.System);
        await time.StartAsync("grant", new GwsBusinessSuite.Application.TimeTracking.TimeEntryInput
            { ContactId = ada.Id, TicketId = ticket.Id, BoardCardId = card, Description = "Fix checkout", HourlyRateUsd = 120 });
        await time.StopAsync("grant");
        var logged = await time.ListForCardAsync(card);
        logged.Single().BoardCardId.Should().Be(card);
        logged.Single().ContactName.Should().Be("Ada Lovelace");

        // A ticket made into a card; a deleted record stays as a gone link until unlinked.
        var fromTicket = await _boards.AddCardFromTicketAsync(boardId, ticket.Id, null, "grant");
        (await _boards.GetBoardAsync(boardId))!.Columns[0].Cards[0].Title.Should().Be("Checkout fails with a coupon");
        (await _boards.ListLinksAsync(boardId, fromTicket)).Select(link => link.Kind).Should().Equal(KanbanLinkKinds.Contact, KanbanLinkKinds.Ticket);
        _db.Invoices.Remove(invoice);
        await _db.SaveChangesAsync();
        var gone = (await _boards.ListLinksAsync(boardId, card)).Single(link => link.Kind == KanbanLinkKinds.Invoice);
        gone.Gone.Should().BeTrue();
        await _boards.UnlinkAsync(boardId, gone.Id);
        (await _boards.ListLinksAsync(boardId, card)).Should().NotContain(link => link.Kind == KanbanLinkKinds.Invoice);
    }

    [Fact]
    public async Task MyDay_ShouldGatherMyCardsOnEveryBoard_ByWhenTheyreDue_WithMyTicketsAndFollowUps()
    {
        // K8d: overdue, due today, in progress, up next - only open cards assigned to me.
        var work = await _boards.CreateBoardAsync("Work", "personal", "grant");
        var home = await _boards.CreateBoardAsync("Home", "personal", "grant");
        var w = (await _boards.GetBoardAsync(work))!;
        var h = (await _boards.GetBoardAsync(home))!;
        var today = DateOnly.FromDateTime(DateTime.Now);
        async Task<Guid> Card(Guid board, KanbanBoard layout, string column, string text) =>
            await _boards.AddQuickCardAsync(board, OptionId(layout, column), KanbanQuickParse.Parse(text, layout, today, "grant"), "grant");

        // With its year: a day and month already gone this year would mean next year's.
        var late = await Card(work, w, "To do", $"Send the report @grant {today.AddDays(-2):dd-MM-yy}");
        var due = await Card(home, h, "To do", "Pay the plumber @Grant today");
        var doing = await Card(work, w, "Doing", "Draft the plan @grant");
        var next = await Card(home, h, "Backlog", "Paint the fence !high @grant");
        await Card(work, w, "Done", "Old thing @grant yesterday");
        await Card(work, w, "To do", "Sam's thing @sam today");

        var ada = new Contact { FullName = "Ada Lovelace", FollowUpDate = DateTimeOffset.Now.AddDays(-1) };
        var bob = new Contact { FullName = "Bob Builder", FollowUpDate = DateTimeOffset.Now.AddDays(3) };
        _db.Contacts.AddRange(ada, bob);
        _db.SupportTickets.AddRange(
            new SupportTicket { ContactId = ada.Id, Subject = "Mine", AssignedToUsername = "Grant" },
            new SupportTicket { ContactId = ada.Id, Subject = "Mine but resolved", AssignedToUsername = "grant", Status = SupportTicketStatuses.Resolved },
            new SupportTicket { ContactId = ada.Id, Subject = "Sam's", AssignedToUsername = "sam" });
        await _db.SaveChangesAsync();

        var day = await _boards.GetMyDayAsync("grant");
        day.Today.Should().Be(today);
        day.Cards.Select(item => (item.Card.RowId, item.Bucket)).Should().Equal(
            (late, KanbanMyDayBuckets.Overdue), (due, KanbanMyDayBuckets.Today), (doing, KanbanMyDayBuckets.Doing), (next, KanbanMyDayBuckets.Next));
        day.Cards.Select(item => item.BoardTitle).Should().Equal("Work", "Home", "Work", "Home");
        day.Cards.Should().OnlyContain(item => item.DoneOptionId == OptionId(item.BoardTitle == "Work" ? w : h, "Done"));
        day.Tickets.Select(ticket => ticket.Subject).Should().Equal("Mine");
        day.Tickets.Single().ContactName.Should().Be("Ada Lovelace");
        day.FollowUps.Select(followUp => followUp.Name).Should().Equal("Ada Lovelace");
    }
}
