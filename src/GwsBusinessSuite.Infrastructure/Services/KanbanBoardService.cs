using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Boards over Sentinel databases (.claude/plans/kanban-boards-2026-10.md). Writes go through
// IWikiDatabaseService, so a card moved here is moved in Sentinel too, with the same validation.
// Which property plays which part is read from the database itself: the Status property (or the
// Select a Board view groups by) makes the columns, and Priority, Labels, Due, Assignee, Estimate
// and the ID are found by type and the usual names, so any Sentinel database can open as a board.
public sealed partial class KanbanBoardService(IAppDbContext db, IWikiDatabaseService databases) : IKanbanBoardService
{
    private static readonly string[] PriorityNames = ["Priority"];
    private static readonly string[] LabelNames = ["Labels", "Label", "Tags"];
    private static readonly string[] DueNames = ["Due", "Due date", "Deadline"];
    private static readonly string[] AssigneeNames = ["Assignee", "Assignees", "Owner"];
    private static readonly string[] EstimateNames = ["Estimate", "Points", "Story points"];

    public async Task<IReadOnlyList<KanbanBoardSummary>> ListBoardsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await db.WikiDatabases.AsNoTracking()
            .Where(database => database.TrashedAt == null)
            .Include(database => database.Properties)
            .Include(database => database.Views)
            .ToListAsync(cancellationToken);
        var boards = candidates.Select(database => (Database: database, Status: StatusProperty(database))).Where(entry => entry.Status is not null).ToList();
        if (boards.Count == 0) return [];

        var ids = boards.Select(entry => entry.Database.Id).ToList();
        var rows = await db.WikiDatabaseRows.AsNoTracking()
            .Where(row => ids.Contains(row.WikiDatabaseId) && row.TrashedAt == null && row.ParentRowId == null)
            .Select(row => new { row.WikiDatabaseId, row.PropertyValuesJson, row.CreatedAt, row.UpdatedAt })
            .ToListAsync(cancellationToken);
        var rowsByBoard = rows.ToLookup(row => row.WikiDatabaseId);

        // SQLite can't order by a DateTimeOffset, so the sort happens here.
        return boards.Select(entry =>
            {
                var status = entry.Status!;
                var complete = CompleteOptionIds(status);
                var boardRows = rowsByBoard[entry.Database.Id].ToList();
                var open = boardRows.Count(row => WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), status.Id) is not { } option || !complete.Contains(option));
                var last = boardRows.Select(row => row.UpdatedAt ?? row.CreatedAt).DefaultIfEmpty(entry.Database.UpdatedAt ?? entry.Database.CreatedAt).Max();
                return new KanbanBoardSummary(entry.Database.Id, entry.Database.Title, entry.Database.Icon, boardRows.Count, open, last);
            })
            .OrderByDescending(board => board.LastActivity)
            .ToList();
    }

    public async Task<Guid> CreateBoardAsync(string title, string templateKey, string performedBy, CancellationToken cancellationToken = default)
    {
        var template = KanbanTemplates.Find(templateKey) ?? throw new ArgumentException($"There's no board template called '{templateKey}'.", nameof(templateKey));
        var name = string.IsNullOrWhiteSpace(title) ? template.Name : title.Trim();
        var database = await databases.CreateDatabaseAsync(name, null, performedBy, cancellationToken);
        await databases.RenameDatabaseAsync(database.Id, name, template.Icon, performedBy, cancellationToken);

        static WikiDatabasePropertyOption Option(string label, string color, string? group = null) => new(Guid.NewGuid().ToString(), label, color, group);
        async Task<WikiDatabaseProperty> AddAsync(string propertyName, string type, IReadOnlyList<WikiDatabasePropertyOption>? options = null, string? prefix = null) =>
            await databases.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
            {
                Name = propertyName,
                Type = type,
                Options = options ?? [],
                UniqueIdPrefix = prefix
            }, performedBy, cancellationToken);

        var status = await AddAsync("Status", WikiDatabasePropertyTypes.Status,
            template.Columns.Select(column => Option(column.Label, column.Color, column.Group)).ToList());
        await AddAsync("Priority", WikiDatabasePropertyTypes.Select,
            [Option("Urgent", "#b91c1c"), Option("High", "#d97706"), Option("Medium", "#2563eb"), Option("Low", "#78716c")]);
        await AddAsync("Labels", WikiDatabasePropertyTypes.MultiSelect,
            [Option("Bug", "#b91c1c"), Option("Feature", "#2563eb"), Option("Improvement", "#15803d"), Option("Chore", "#78716c"), Option("Research", "#7c3aed")]);
        await AddAsync("Due", WikiDatabasePropertyTypes.Date);
        await AddAsync("Assignee", WikiDatabasePropertyTypes.Person);
        await AddAsync("Estimate", WikiDatabasePropertyTypes.Number);
        if (template.Sprints)
        {
            await AddAsync("Sprint", WikiDatabasePropertyTypes.Select, [Option("Sprint 1", "#2563eb")]);
        }
        await AddAsync("ID", WikiDatabasePropertyTypes.UniqueId, prefix: KeyPrefix(name));

        // The database opens as this board in Sentinel too, with a table of every card beside it.
        // A saved view keeps its type, so the default table makes way for new ones rather than
        // being converted; Board comes first, so it's the view Sentinel opens.
        var defaultViews = (await databases.GetDatabaseAsync(database.Id, cancellationToken) ?? database).Views.Select(view => view.Id).ToList();
        await databases.SaveViewAsync(database.Id, null, "Board", WikiDatabaseViewTypes.Board,
            WikiDatabaseViewConfig.Empty with { GroupByPropertyId = status.Id.ToString() }, performedBy, cancellationToken);
        await databases.SaveViewAsync(database.Id, null, "All cards", WikiDatabaseViewTypes.Table, WikiDatabaseViewConfig.Empty, performedBy, cancellationToken);
        foreach (var viewId in defaultViews) await databases.DeleteViewAsync(database.Id, viewId, performedBy, cancellationToken);
        return database.Id;
    }

    public async Task<KanbanBoard?> GetBoardAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        var database = await databases.GetDatabaseAsync(databaseId, cancellationToken);
        if (database is null || StatusProperty(database) is not { } status) return null;

        var properties = database.Properties.ToList();
        var fields = new KanbanFields(
            status.Id,
            Find(properties, WikiDatabasePropertyTypes.Select, PriorityNames)?.Id,
            Find(properties, WikiDatabasePropertyTypes.MultiSelect, LabelNames)?.Id,
            Find(properties, WikiDatabasePropertyTypes.Date, DueNames)?.Id,
            (Find(properties, WikiDatabasePropertyTypes.Person, AssigneeNames) ?? properties.FirstOrDefault(p => p.Type == WikiDatabasePropertyTypes.Person))?.Id,
            Find(properties, WikiDatabasePropertyTypes.Number, EstimateNames)?.Id,
            properties.FirstOrDefault(p => p.Type == WikiDatabasePropertyTypes.UniqueId)?.Id);

        var titleProperty = properties.FirstOrDefault(p => p.Type == WikiDatabasePropertyTypes.Title);
        var subItems = database.Rows.Where(row => row.ParentRowId is not null).GroupBy(row => row.ParentRowId!.Value).ToDictionary(group => group.Key, group => group.Count());
        var cards = database.Rows
            .Where(row => row.ParentRowId is null)
            .OrderBy(row => row.SortOrder)
            .Select(row => (Status: WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), status.Id), Card: ToCard(row, properties, titleProperty, fields, subItems)))
            .ToList();

        var options = ColumnOrder(status);
        var known = options.Select(option => option.Id).ToHashSet();
        var columns = options
            .Select(option => new KanbanColumn(option.Id, option.Label, SafeColor(option.Color), option.Group,
                cards.Where(card => card.Status == option.Id).Select(card => card.Card).ToList()))
            .ToList();
        var unsorted = cards.Where(card => card.Status is null || !known.Contains(card.Status)).Select(card => card.Card).ToList();
        if (unsorted.Count > 0)
        {
            columns.Insert(0, new KanbanColumn(null, "No status", "#78716c", null, unsorted));
        }
        IReadOnlyList<KanbanOption> OptionsOf(Guid? propertyId) => properties.FirstOrDefault(property => property.Id == propertyId) is { } property
            ? WikiDatabasePropertyConfig.GetOptions(property).Select(option => new KanbanOption(option.Id, option.Label, SafeColor(option.Color))).ToList()
            : [];
        return new KanbanBoard(database.Id, database.Title, database.Icon, fields, columns, OptionsOf(fields.PriorityPropertyId), OptionsOf(fields.LabelsPropertyId));
    }

    public async Task MoveCardAsync(Guid databaseId, Guid rowId, string? optionId, Guid? beforeRowId, string performedBy, CancellationToken cancellationToken = default)
    {
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        // MoveRowAsync's index counts every other row in the target column (sub-items and trashed
        // rows included), ordered by SortOrder; this finds the anchor card's place in that list.
        var others = await db.WikiDatabaseRows.AsNoTracking()
            .Where(row => row.WikiDatabaseId == databaseId && row.Id != rowId)
            .Select(row => new { row.Id, row.SortOrder, row.PropertyValuesJson })
            .ToListAsync(cancellationToken);
        var target = optionId ?? string.Empty;
        var column = others
            .Where(row => (WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), status.Id) ?? string.Empty) == target)
            .OrderBy(row => row.SortOrder)
            .Select(row => row.Id)
            .ToList();
        var index = beforeRowId is { } before && column.IndexOf(before) is >= 0 and var at ? at : column.Count;
        await databases.MoveRowAsync(databaseId, rowId, status.Id, optionId, index, performedBy, cancellationToken);
    }

    public async Task<Guid> AddCardAsync(Guid databaseId, string? optionId, string title, string performedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A card needs a title.", nameof(title));
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var titleProperty = await TitlePropertyAsync(databaseId, cancellationToken);
        var values = new JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, title.Trim());
        if (optionId is not null) WikiPropertyValues.SetText(values, status.Id, optionId);
        var row = await databases.SaveRowAsync(databaseId, new WikiDatabaseRowEditor
        {
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone())
        }, performedBy, cancellationToken);
        // New cards go to the top of their column, where the quick-add box is.
        await databases.MoveRowAsync(databaseId, row.Id, status.Id, optionId, 0, performedBy, cancellationToken);
        return row.Id;
    }

    public async Task<IReadOnlyDictionary<Guid, JsonNode?>> SetCardsValueAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, Guid propertyId,
        Func<JsonNode?, JsonNode?> change, string performedBy, CancellationToken cancellationToken = default)
    {
        var key = propertyId.ToString();
        var before = new Dictionary<Guid, JsonNode?>();
        foreach (var rowId in rowIds.Distinct())
        {
            var row = await db.WikiDatabaseRows.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == rowId && item.WikiDatabaseId == databaseId && item.TrashedAt == null, cancellationToken)
                ?? throw new InvalidOperationException("That card no longer exists.");
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            var previous = values[key]?.DeepClone();
            var next = change(previous?.DeepClone());
            if (JsonNode.DeepEquals(previous, next)) continue;
            values[key] = next?.DeepClone();
            await SaveValuesAsync(databaseId, row, values, performedBy, cancellationToken);
            before[rowId] = previous;
        }
        return before;
    }

    public async Task RestoreCardValuesAsync(Guid databaseId, Guid propertyId, IReadOnlyDictionary<Guid, JsonNode?> values, string performedBy, CancellationToken cancellationToken = default)
    {
        foreach (var (rowId, value) in values)
        {
            await SetCardsValueAsync(databaseId, [rowId], propertyId, _ => value?.DeepClone(), performedBy, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<KanbanSubItem>> ListSubItemsAsync(Guid databaseId, Guid parentRowId, CancellationToken cancellationToken = default)
    {
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var complete = CompleteOptionIds(status);
        var titleProperty = await TitlePropertyAsync(databaseId, cancellationToken);
        var rows = await db.WikiDatabaseRows.AsNoTracking()
            .Where(row => row.WikiDatabaseId == databaseId && row.ParentRowId == parentRowId && row.TrashedAt == null)
            .OrderBy(row => row.SortOrder)
            .Select(row => new { row.Id, row.PropertyValuesJson })
            .ToListAsync(cancellationToken);
        return rows.Select(row =>
        {
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            var title = WikiPropertyValues.GetText(values, titleProperty.Id);
            return new KanbanSubItem(row.Id, string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                WikiPropertyValues.GetText(values, status.Id) is { } option && complete.Contains(option));
        }).ToList();
    }

    public async Task<Guid> AddSubItemAsync(Guid databaseId, Guid parentRowId, string title, string performedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A sub-item needs a title.", nameof(title));
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var titleProperty = await TitlePropertyAsync(databaseId, cancellationToken);
        var values = new JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, title.Trim());
        if (ColumnOrder(status).FirstOrDefault() is { } first) WikiPropertyValues.SetText(values, status.Id, first.Id);
        var row = await databases.SaveRowAsync(databaseId, new WikiDatabaseRowEditor
        {
            ParentRowId = parentRowId,
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone())
        }, performedBy, cancellationToken);
        return row.Id;
    }

    public async Task SetSubItemDoneAsync(Guid databaseId, Guid rowId, bool done, string performedBy, CancellationToken cancellationToken = default)
    {
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var options = ColumnOrder(status);
        var target = done
            ? options.FirstOrDefault(option => option.Group == WikiDatabaseStatusGroups.Complete) ?? options.LastOrDefault()
            : options.FirstOrDefault();
        if (target is null) return;
        await SetCardsValueAsync(databaseId, [rowId], status.Id, _ => JsonValue.Create(target.Id), performedBy, cancellationToken);
    }

    public async Task TrashCardsAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, string performedBy, CancellationToken cancellationToken = default)
    {
        foreach (var rowId in rowIds.Distinct()) await databases.TrashRowAsync(databaseId, rowId, performedBy, cancellationToken);
    }

    public async Task RestoreCardsAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, string performedBy, CancellationToken cancellationToken = default)
    {
        foreach (var rowId in rowIds.Distinct()) await databases.RestoreRowAsync(databaseId, rowId, performedBy, cancellationToken);
    }

    // A property-only save through Sentinel (its validation, relations and automation triggers),
    // leaving the card's page alone.
    private Task<WikiDatabaseRow> SaveValuesAsync(Guid databaseId, WikiDatabaseRow row, JsonObject values, string performedBy, CancellationToken cancellationToken) =>
        databases.SaveRowAsync(databaseId, new WikiDatabaseRowEditor
        {
            Id = row.Id,
            ParentRowId = row.ParentRowId,
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone())
        }, performedBy, cancellationToken);

    private async Task<WikiDatabaseProperty> TitlePropertyAsync(Guid databaseId, CancellationToken cancellationToken) =>
        await db.WikiDatabaseProperties.AsNoTracking()
            .FirstAsync(property => property.WikiDatabaseId == databaseId && property.Type == WikiDatabasePropertyTypes.Title, cancellationToken);

    private async Task<WikiDatabaseProperty> StatusPropertyAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var database = await db.WikiDatabases.AsNoTracking()
            .Include(item => item.Properties)
            .Include(item => item.Views)
            .FirstOrDefaultAsync(item => item.Id == databaseId && item.TrashedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("That board no longer exists.");
        return StatusProperty(database) ?? throw new InvalidOperationException("That board has no Status column any more.");
    }

    // The columns: a Status property, else the Select a Board view groups by.
    internal static WikiDatabaseProperty? StatusProperty(WikiDatabase database)
    {
        var properties = database.Properties.OrderBy(property => property.SortOrder).ToList();
        if (properties.FirstOrDefault(property => property.Type == WikiDatabasePropertyTypes.Status) is { } status) return status;
        var grouped = database.Views
            .Where(view => view.Type == WikiDatabaseViewTypes.Board)
            .OrderBy(view => view.SortOrder)
            .Select(view => WikiDatabaseViewConfigJson.Parse(view.ConfigJson).GroupByPropertyId)
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));
        return grouped is null ? null : properties.FirstOrDefault(property => property.Id.ToString() == grouped && property.Type == WikiDatabasePropertyTypes.Select);
    }

    // A Status property reads left to right through its groups (To-do, In progress, Complete);
    // within a group, and for a Select, the options keep their own order.
    private static IReadOnlyList<WikiDatabasePropertyOption> ColumnOrder(WikiDatabaseProperty property)
    {
        var options = WikiDatabasePropertyConfig.GetOptions(property);
        if (property.Type != WikiDatabasePropertyTypes.Status) return options;
        return options
            .Select((option, index) => (Option: option, Index: index))
            .OrderBy(entry => entry.Option.Group is { } group && WikiDatabaseStatusGroups.All.Contains(group)
                ? WikiDatabaseStatusGroups.All.ToList().IndexOf(group)
                : WikiDatabaseStatusGroups.All.Count)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Option)
            .ToList();
    }

    private static HashSet<string> CompleteOptionIds(WikiDatabaseProperty status) =>
        WikiDatabasePropertyConfig.GetOptions(status).Where(option => option.Group == WikiDatabaseStatusGroups.Complete).Select(option => option.Id).ToHashSet();

    private static WikiDatabaseProperty? Find(IReadOnlyList<WikiDatabaseProperty> properties, string type, string[] names) =>
        properties.FirstOrDefault(property => property.Type == type && names.Contains(property.Name.Trim(), StringComparer.OrdinalIgnoreCase));

    private static KanbanCard ToCard(WikiDatabaseRow row, IReadOnlyList<WikiDatabaseProperty> properties, WikiDatabaseProperty? titleProperty,
        KanbanFields fields, IReadOnlyDictionary<Guid, int> subItems)
    {
        var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
        WikiDatabaseProperty? Property(Guid? id) => id is { } value ? properties.FirstOrDefault(property => property.Id == value) : null;

        string? key = null;
        if (Property(fields.KeyPropertyId) is { } keyProperty && WikiPropertyValues.GetNumber(values, keyProperty.Id) is { } number)
        {
            key = $"{WikiDatabasePropertyConfig.Parse(keyProperty).UniqueIdPrefix}{number:0}";
        }

        IReadOnlyList<KanbanLabel> labels = [];
        if (Property(fields.LabelsPropertyId) is { } labelProperty)
        {
            var chosen = WikiPropertyValues.GetMultiSelect(values, labelProperty.Id);
            labels = WikiDatabasePropertyConfig.GetOptions(labelProperty).Where(option => chosen.Contains(option.Id))
                .Select(option => new KanbanLabel(option.Label, SafeColor(option.Color))).ToList();
        }

        KanbanLabel? priority = null;
        if (Property(fields.PriorityPropertyId) is { } priorityProperty && WikiPropertyValues.GetText(values, priorityProperty.Id) is { } priorityId)
        {
            priority = WikiDatabasePropertyConfig.GetOptions(priorityProperty).FirstOrDefault(option => option.Id == priorityId) is { } option
                ? new KanbanLabel(option.Label, SafeColor(option.Color)) : null;
        }

        var checklist = WikiBlockJson.ParseBlocks(row.BlocksJson).Where(block => block.Type == WikiBlockTypes.ToDo && block.PlainText.Trim().Length > 0).ToList();
        return new KanbanCard(
            row.Id,
            key,
            titleProperty is null ? "Untitled" : (WikiPropertyValues.GetText(values, titleProperty.Id) is { Length: > 0 } title ? title : "Untitled"),
            row.Icon,
            row.CoverImageUrl,
            labels,
            priority,
            fields.DuePropertyId is { } due ? WikiPropertyValues.GetDate(values, due) : null,
            fields.AssigneePropertyId is { } assignee ? WikiPropertyValues.GetMultiSelect(values, assignee) : [],
            fields.EstimatePropertyId is { } estimate ? WikiPropertyValues.GetNumber(values, estimate) : null,
            checklist.Count(block => block.Props.GetValueOrDefault("checked") == "true"),
            checklist.Count,
            subItems.GetValueOrDefault(row.Id),
            row.CreatedAt,
            row.UpdatedAt ?? row.CreatedAt);
    }

    // Option colours are typed by people in Sentinel and end up in style attributes, so only a hex
    // colour or a plain colour name gets through; anything else shows grey.
    internal static string SafeColor(string? color) =>
        color is not null && SafeColorPattern().IsMatch(color) ? color : "#78716c";

    [System.Text.RegularExpressions.GeneratedRegex("^(#[0-9a-fA-F]{3,8}|[a-zA-Z]{3,20})$")]
    private static partial System.Text.RegularExpressions.Regex SafeColorPattern();

    // "Website relaunch" -> "WEB-": a short ID prefix from the board's first word.
    internal static string KeyPrefix(string title)
    {
        var letters = new string(title.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        return (letters.Length >= 2 ? letters : "TASK") + "-";
    }
}
