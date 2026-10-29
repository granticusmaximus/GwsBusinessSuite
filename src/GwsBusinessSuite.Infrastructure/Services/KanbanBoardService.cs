using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Boards over Sentinel databases (.claude/plans/kanban-boards-2026-10.md). Writes go through
// IWikiDatabaseService, so a card moved here is moved in Sentinel too, with the same validation.
// Which property plays which part is read from the database itself: the Status property (or the
// Select a Board view groups by) makes the columns, and Priority, Labels, Due, Assignee, Estimate
// and the ID are found by type and the usual names, so any Sentinel database can open as a board.
public sealed partial class KanbanBoardService(IAppDbContext db, IWikiDatabaseService databases,
    ISentinelAccessService? access = null, ISecretProtector? secrets = null, BoardNotificationNotifier? notifier = null) : IKanbanBoardService
{
    private static readonly string[] PriorityNames = ["Priority"];
    private static readonly string[] LabelNames = ["Labels", "Label", "Tags"];
    private static readonly string[] DueNames = ["Due", "Due date", "Deadline"];
    private static readonly string[] AssigneeNames = ["Assignee", "Assignees", "Owner"];
    private static readonly string[] EstimateNames = ["Estimate", "Points", "Story points"];
    private static readonly string[] SprintNames = ["Sprint", "Cycle", "Iteration"];
    private const string BlockedName = "Blocked";
    private const string BlockedReasonName = "Blocked reason";
    private const string WaitingOnName = "Waiting on";
    private static readonly System.Text.Json.JsonSerializerOptions SettingsJson = new(System.Text.Json.JsonSerializerDefaults.Web);

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
        await AddAsync(BlockedName, WikiDatabasePropertyTypes.Checkbox);
        await AddAsync(BlockedReasonName, WikiDatabasePropertyTypes.Text);
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

    public Task DeleteBoardAsync(Guid databaseId, string performedBy, CancellationToken cancellationToken = default) =>
        databases.TrashDatabaseAsync(databaseId, performedBy, cancellationToken);

    public Task RestoreBoardAsync(Guid databaseId, string performedBy, CancellationToken cancellationToken = default) =>
        databases.RestoreDatabaseAsync(databaseId, performedBy, cancellationToken);

    public async Task RenameBoardAsync(Guid databaseId, string title, string? icon, string performedBy, CancellationToken cancellationToken = default)
    {
        var name = title.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the board a name.", nameof(title));
        if (name.Length > 120) throw new ArgumentException("Keep the name under 120 characters.", nameof(title));
        var emoji = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        if (emoji is { Length: > 16 }) throw new ArgumentException("Use one emoji for the icon.", nameof(icon));
        await databases.RenameDatabaseAsync(databaseId, name, emoji, performedBy, cancellationToken);
    }

    public async Task<string> AddColumnAsync(Guid databaseId, string label, string? group, string performedBy, CancellationToken cancellationToken = default)
    {
        var (status, options) = await ColumnOptionsAsync(databaseId, cancellationToken);
        var name = ColumnLabel(label, options, exceptId: null);
        var color = KanbanColumnEdits.Colors.FirstOrDefault(c => options.All(o => !string.Equals(o.Color, c, StringComparison.OrdinalIgnoreCase)))
            ?? KanbanColumnEdits.Colors[options.Count % KanbanColumnEdits.Colors.Count];
        var option = new WikiDatabasePropertyOption(Guid.NewGuid().ToString(), name, color, ColumnGroup(status, group));
        await SaveColumnOptionsAsync(databaseId, status, [.. options, option], performedBy, cancellationToken);
        return option.Id;
    }

    public async Task UpdateColumnAsync(Guid databaseId, string optionId, string label, string color, string? group, string performedBy,
        CancellationToken cancellationToken = default)
    {
        var (status, options) = await ColumnOptionsAsync(databaseId, cancellationToken);
        var current = options.FirstOrDefault(o => o.Id == optionId) ?? throw new KeyNotFoundException("That column isn't on the board any more.");
        if (!SafeColorPattern().IsMatch(color ?? string.Empty)) throw new ArgumentException("Pick a colour for the column.", nameof(color));
        var newGroup = ColumnGroup(status, group ?? current.Group);
        if (current.Group == WikiDatabaseStatusGroups.Complete && newGroup != WikiDatabaseStatusGroups.Complete
            && options.Count(o => o.Group == WikiDatabaseStatusGroups.Complete) == 1)
            throw new InvalidOperationException("Keep at least one Done column: it's how the board knows a card is finished.");
        var updated = current with { Label = ColumnLabel(label, options, exceptId: optionId), Color = color!, Group = newGroup };
        // Changing group moves the column to the end of its new group, as a new column would land.
        var list = options.Where(o => o.Id != optionId).ToList();
        if (newGroup == current.Group) list.Insert(options.ToList().FindIndex(o => o.Id == optionId), updated);
        else list.Add(updated);
        await SaveColumnOptionsAsync(databaseId, status, list, performedBy, cancellationToken);
    }

    public async Task MoveColumnAsync(Guid databaseId, string optionId, int offset, string performedBy, CancellationToken cancellationToken = default)
    {
        var (status, options) = await ColumnOptionsAsync(databaseId, cancellationToken);
        var current = options.FirstOrDefault(o => o.Id == optionId) ?? throw new KeyNotFoundException("That column isn't on the board any more.");
        // Neighbours as the board shows them: a Status board orders by group first, so a column
        // only swaps places with another column of its own group.
        var shown = ColumnOrderOf(status, options).Where(o => o.Group == current.Group).ToList();
        var at = shown.FindIndex(o => o.Id == optionId);
        var target = at + Math.Sign(offset);
        if (offset == 0 || target < 0 || target >= shown.Count) return;
        var list = options.ToList();
        var (a, b) = (list.FindIndex(o => o.Id == optionId), list.FindIndex(o => o.Id == shown[target].Id));
        (list[a], list[b]) = (list[b], list[a]);
        await SaveColumnOptionsAsync(databaseId, status, list, performedBy, cancellationToken);
    }

    public async Task DeleteColumnAsync(Guid databaseId, string optionId, string? moveCardsTo, string performedBy,
        CancellationToken cancellationToken = default)
    {
        var database = await databases.GetDatabaseAsync(databaseId, cancellationToken) ?? throw new KeyNotFoundException("That board isn't there any more.");
        var status = StatusProperty(database) ?? throw new KeyNotFoundException("That board has no columns to edit.");
        var options = WikiDatabasePropertyConfig.GetOptions(status).ToList();
        var current = options.FirstOrDefault(o => o.Id == optionId) ?? throw new KeyNotFoundException("That column isn't on the board any more.");
        if (options.Count == 1) throw new InvalidOperationException("A board needs at least one column.");
        if (current.Group == WikiDatabaseStatusGroups.Complete && options.Count(o => o.Group == WikiDatabaseStatusGroups.Complete) == 1)
            throw new InvalidOperationException("Keep at least one Done column: it's how the board knows a card is finished.");
        // Every card in it, archived ones and sub-items included, so none is left pointing at nothing.
        var rows = database.Rows
            .Where(row => WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), status.Id) == optionId)
            .Select(row => row.Id)
            .ToList();
        if (rows.Count > 0)
        {
            // The board asks where visible cards go; archived cards and sub-items it can't see
            // go to a neighbour in the same group (or the first other column) when it doesn't say.
            var others = ColumnOrderOf(status, options).Where(o => o.Id != optionId).ToList();
            var target = moveCardsTo is not null && others.Any(o => o.Id == moveCardsTo) ? moveCardsTo
                : moveCardsTo is null ? (others.FirstOrDefault(o => o.Group == current.Group) ?? others[0]).Id
                : throw new ArgumentException("Choose another column on this board for its cards.", nameof(moveCardsTo));
            await SetCardsValueAsync(databaseId, rows, status.Id, _ => JsonValue.Create(target), performedBy, cancellationToken);
        }
        await SaveColumnOptionsAsync(databaseId, status, options.Where(o => o.Id != optionId).ToList(), performedBy, cancellationToken);

        // Its WIP limit and its place on the public roadmap go with it.
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        if (settings.WipLimits.ContainsKey(optionId) || settings.Roadmap?.Columns.Contains(optionId) == true)
        {
            var limits = new Dictionary<string, int>(settings.WipLimits);
            limits.Remove(optionId);
            await SaveSettingsAsync(databaseId, settings with
            {
                WipLimits = limits,
                Roadmap = settings.Roadmap is null ? null : settings.Roadmap with { Columns = settings.Roadmap.Columns.Where(c => c != optionId).ToList() }
            }, performedBy, cancellationToken);
        }
    }

    private async Task<(WikiDatabaseProperty Status, IReadOnlyList<WikiDatabasePropertyOption> Options)> ColumnOptionsAsync(Guid databaseId,
        CancellationToken cancellationToken)
    {
        var database = await databases.GetDatabaseAsync(databaseId, cancellationToken) ?? throw new KeyNotFoundException("That board isn't there any more.");
        var status = StatusProperty(database) ?? throw new KeyNotFoundException("That board has no columns to edit.");
        return (status, WikiDatabasePropertyConfig.GetOptions(status));
    }

    // Through Sentinel, as the database editor saves it, keeping the property's other settings.
    private async Task SaveColumnOptionsAsync(Guid databaseId, WikiDatabaseProperty status, IReadOnlyList<WikiDatabasePropertyOption> options,
        string performedBy, CancellationToken cancellationToken)
    {
        var config = WikiDatabasePropertyConfig.Parse(status);
        await databases.SavePropertyAsync(databaseId, new WikiDatabasePropertyEditor
        {
            Id = status.Id,
            Name = status.Name,
            Type = status.Type,
            Options = options,
            IsRequired = config.IsRequired
        }, performedBy, cancellationToken);
    }

    private static string ColumnLabel(string label, IReadOnlyList<WikiDatabasePropertyOption> options, string? exceptId)
    {
        var name = (label ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("Give the column a name.", nameof(label));
        if (name.Length > KanbanColumnEdits.MaxLabelLength)
            throw new ArgumentException($"Keep column names under {KanbanColumnEdits.MaxLabelLength} characters.", nameof(label));
        if (options.Any(o => o.Id != exceptId && string.Equals(o.Label.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException($"There's already a column called \"{name}\".", nameof(label));
        return name;
    }

    // A Status board's columns each belong to a group; a Select board's have none.
    private static string? ColumnGroup(WikiDatabaseProperty status, string? group) =>
        status.Type != WikiDatabasePropertyTypes.Status ? null
            : group is not null && WikiDatabaseStatusGroups.All.Contains(group) ? group : WikiDatabaseStatusGroups.ToDo;

    public Task<KanbanBoard?> GetBoardAsync(Guid databaseId, CancellationToken cancellationToken = default) =>
        LoadBoardAsync(databaseId, includeArchived: false, cancellationToken);

    // Archived cards (K9a) are off the board, but Insights still counts them.
    private async Task<KanbanBoard?> LoadBoardAsync(Guid databaseId, bool includeArchived, CancellationToken cancellationToken)
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
            properties.FirstOrDefault(p => p.Type == WikiDatabasePropertyTypes.UniqueId)?.Id,
            Find(properties, WikiDatabasePropertyTypes.Select, SprintNames)?.Id,
            Find(properties, WikiDatabasePropertyTypes.Checkbox, [BlockedName])?.Id,
            Find(properties, WikiDatabasePropertyTypes.Text, [BlockedReasonName])?.Id,
            Find(properties, WikiDatabasePropertyTypes.Date, [KanbanArchive.PropertyName])?.Id,
            Find(properties, WikiDatabasePropertyTypes.Relation, [WaitingOnName])?.Id);
        var settings = await GetSettingsAsync(databaseId, cancellationToken);

        // When each card last moved into the column it's in now; a row from before the log, or
        // never logged into it, counts from its creation.
        var entries = (await db.WikiDatabaseRowStatusChanges.AsNoTracking()
                .Where(change => change.WikiDatabaseId == databaseId && change.PropertyId == status.Id && change.ToOptionId != null)
                .Select(change => new { change.RowId, change.ToOptionId, change.AtUnixSeconds })
                .ToListAsync(cancellationToken))
            .GroupBy(change => (change.RowId, change.ToOptionId!))
            .ToDictionary(group => group.Key, group => group.Max(change => change.AtUnixSeconds));

        var titleProperty = properties.FirstOrDefault(p => p.Type == WikiDatabasePropertyTypes.Title);
        var subItems = database.Rows.Where(row => row.ParentRowId is not null).GroupBy(row => row.ParentRowId!.Value).ToDictionary(group => group.Key, group => group.Count());
        // K8e: votes on the public roadmap.
        var votes = settings.Roadmap is null ? new Dictionary<Guid, int>()
            : (await db.BoardCardVotes.AsNoTracking().Where(vote => vote.WikiDatabaseId == databaseId).Select(vote => vote.RowId).ToListAsync(cancellationToken))
                .GroupBy(rowId => rowId).ToDictionary(group => group.Key, group => group.Count());
        // K8c: what each card is linked to, for its icons.
        var links = (await db.BoardCardLinks.AsNoTracking().Where(link => link.WikiDatabaseId == databaseId)
                .Select(link => new { link.RowId, link.Kind }).ToListAsync(cancellationToken))
            .GroupBy(link => link.RowId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(link => link.Kind).ToList());
        var archived = fields.ArchivedPropertyId is { } archivedProperty
            ? database.Rows.Where(row => row.ParentRowId is null
                && WikiPropertyValues.GetDate(WikiPropertyValues.ParseObject(row.PropertyValuesJson), archivedProperty) is not null)
                .Select(row => row.Id).ToHashSet()
            : [];
        var rowValues = database.Rows.Where(row => row.ParentRowId is null)
            .ToDictionary(row => row.Id, row => WikiPropertyValues.ParseObject(row.PropertyValuesJson));
        var completeRows = rowValues.Where(pair =>
                WikiPropertyValues.GetText(pair.Value, status.Id) is { } option && CompleteOptionIds(status).Contains(option))
            .Select(pair => pair.Key).ToHashSet();
        var cards = database.Rows
            .Where(row => row.ParentRowId is null && (includeArchived || !archived.Contains(row.Id)))
            .OrderBy(row => row.SortOrder)
            .Select(row =>
            {
                var current = WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), status.Id);
                var entered = current is not null && entries.TryGetValue((row.Id, current), out var at)
                    ? DateTimeOffset.FromUnixTimeSeconds(at)
                    : row.CreatedAt;
                var waitingOn = fields.WaitingOnPropertyId is { } waitingProperty
                    ? WikiPropertyValues.GetMultiSelect(WikiPropertyValues.ParseObject(row.PropertyValuesJson), waitingProperty)
                        .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null).Where(id => id is not null).Select(id => id!.Value)
                        .Where(rowValues.ContainsKey).Distinct().ToList()
                    : [];
                return (Status: current, Card: ToCard(row, properties, titleProperty, fields, subItems, settings.CardSize == KanbanCardSizes.Rich)
                    with { EnteredColumnAt = entered, LinkKinds = links.GetValueOrDefault(row.Id), Votes = votes.GetValueOrDefault(row.Id),
                        WaitingOn = waitingOn, WaitingOnOpen = waitingOn.Count(id => !completeRows.Contains(id)) });
            })
            .ToList();

        var options = ColumnOrder(status);
        var known = options.Select(option => option.Id).ToHashSet();
        var columns = options
            .Select(option => new KanbanColumn(option.Id, option.Label, SafeColor(option.Color), option.Group,
                cards.Where(card => card.Status == option.Id).Select(card => card.Card).ToList(),
                settings.WipLimits.TryGetValue(option.Id, out var limit) && limit > 0 ? limit : null))
            .ToList();
        var unsorted = cards.Where(card => card.Status is null || !known.Contains(card.Status)).Select(card => card.Card).ToList();
        if (unsorted.Count > 0)
        {
            columns.Insert(0, new KanbanColumn(null, "No status", "#78716c", null, unsorted));
        }
        IReadOnlyList<KanbanOption> OptionsOf(Guid? propertyId) => properties.FirstOrDefault(property => property.Id == propertyId) is { } property
            ? WikiDatabasePropertyConfig.GetOptions(property).Select(option => new KanbanOption(option.Id, option.Label, SafeColor(option.Color))).ToList()
            : [];
        var faceFields = properties.Where(property => property.Type != WikiDatabasePropertyTypes.Title
                && property.Id != status.Id && property.Id != fields.ArchivedPropertyId
                && property.Id != fields.BlockedPropertyId && property.Id != fields.BlockedReasonPropertyId)
            .OrderBy(property => property.SortOrder)
            .Select(property => new KanbanFaceField(property.Id, property.Name, property.Type)).ToList();
        return new KanbanBoard(database.Id, database.Title, database.Icon, fields, columns, OptionsOf(fields.PriorityPropertyId), OptionsOf(fields.LabelsPropertyId),
            settings, OptionsOf(fields.SprintPropertyId), archived.Count, faceFields);
    }

    // ---- Archive (K9a) ----

    public async Task<IReadOnlyList<KanbanArchivedCard>> ListArchivedAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        if (await LoadBoardAsync(databaseId, includeArchived: true, cancellationToken) is not { Fields.ArchivedPropertyId: { } archivedProperty } board) return [];
        var dates = (await db.WikiDatabaseRows.AsNoTracking()
                .Where(row => row.WikiDatabaseId == databaseId && row.TrashedAt == null && row.ParentRowId == null)
                .Select(row => new { row.Id, row.PropertyValuesJson })
                .ToListAsync(cancellationToken))
            .Select(row => (row.Id, At: WikiPropertyValues.GetDate(WikiPropertyValues.ParseObject(row.PropertyValuesJson), archivedProperty)))
            .Where(row => row.At is not null)
            .ToDictionary(row => row.Id, row => row.At!.Value);
        return board.Columns
            .SelectMany(column => column.Cards.Where(card => dates.ContainsKey(card.RowId))
                .Select(card => new KanbanArchivedCard(card.RowId, card.Title, card.Key, column.Label, dates[card.RowId])))
            .OrderByDescending(card => card.ArchivedAt)
            .ToList();
    }

    public async Task<int> ArchiveCardsAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, string performedBy, CancellationToken cancellationToken = default)
    {
        if (rowIds.Count == 0) return 0;
        var property = await EnsureArchivedPropertyAsync(databaseId, performedBy, cancellationToken);
        var now = JsonValue.Create(DateTimeOffset.UtcNow);
        var changed = await SetCardsValueAsync(databaseId, rowIds, property, previous => previous ?? now.DeepClone(), performedBy, cancellationToken);
        return changed.Count;
    }

    public async Task UnarchiveCardsAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, string performedBy, CancellationToken cancellationToken = default)
    {
        if (rowIds.Count == 0) return;
        var property = await EnsureArchivedPropertyAsync(databaseId, performedBy, cancellationToken);
        await SetCardsValueAsync(databaseId, rowIds, property, _ => null, performedBy, cancellationToken);
        // A restored card gets the full ArchiveAfterDays again before the sweep takes it.
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        var restored = new Dictionary<string, long>(settings.RestoredAt);
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var rowId in rowIds) restored[rowId.ToString()] = at;
        await SaveSettingsAsync(databaseId, settings with { RestoredAt = restored }, performedBy, cancellationToken);
    }

    public async Task<int> ArchiveDueCardsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var archived = 0;
        foreach (var summary in await ListBoardsAsync(cancellationToken))
        {
            if (await GetBoardAsync(summary.DatabaseId, cancellationToken) is not { Settings: { ArchiveAfterDays: > 0 } settings } board) continue;
            var cutoff = now.AddDays(-settings.ArchiveAfterDays);
            var due = board.Columns
                .Where(column => column.Group == WikiDatabaseStatusGroups.Complete)
                .SelectMany(column => column.Cards)
                .Where(card => card.EnteredColumnAt <= cutoff
                    && !(settings.RestoredAt.TryGetValue(card.RowId.ToString(), out var restored) && DateTimeOffset.FromUnixTimeSeconds(restored) > cutoff))
                .Select(card => card.RowId)
                .ToList();
            if (due.Count > 0) archived += await ArchiveCardsAsync(summary.DatabaseId, due, "board-archive", cancellationToken);
        }
        return archived;
    }

    private async Task<Guid> EnsureArchivedPropertyAsync(Guid databaseId, string performedBy, CancellationToken cancellationToken)
    {
        var properties = await db.WikiDatabaseProperties.AsNoTracking().Where(property => property.WikiDatabaseId == databaseId).ToListAsync(cancellationToken);
        return Find(properties, WikiDatabasePropertyTypes.Date, [KanbanArchive.PropertyName])?.Id
            ?? (await databases.SavePropertyAsync(databaseId,
                new WikiDatabasePropertyEditor { Name = KanbanArchive.PropertyName, Type = WikiDatabasePropertyTypes.Date }, performedBy, cancellationToken)).Id;
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
        if (optionId is not null && CompleteOptionIds(status).Contains(optionId))
            await UnblockDependentsAsync(databaseId, rowId, performedBy, cancellationToken);
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

    public async Task<KanbanSettings> GetSettingsAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        var json = await db.KanbanBoardSettings.AsNoTracking()
            .Where(row => row.WikiDatabaseId == databaseId)
            .Select(row => row.SettingsJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return KanbanSettings.Default;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<KanbanSettings>(json, SettingsJson) ?? KanbanSettings.Default;
        }
        catch (System.Text.Json.JsonException)
        {
            return KanbanSettings.Default;
        }
    }

    public async Task SaveSettingsAsync(Guid databaseId, KanbanSettings settings, string performedBy, CancellationToken cancellationToken = default)
    {
        if (settings.SwimlaneBy is { } lane && !KanbanSwimlanes.All.Contains(lane))
            throw new ArgumentException($"'{lane}' isn't a swimlane choice.", nameof(settings));
        if (settings.WipLimits.Values.Any(limit => limit < 1 || limit > 999))
            throw new ArgumentException("A WIP limit is a number from 1 to 999.", nameof(settings));
        if (settings.AgingDays is < 1 or > 365)
            throw new ArgumentException("Card age is shown after 1 to 365 days.", nameof(settings));
        if (settings.Background is { } background && !KanbanLook.IsPreset(background) && !KanbanLook.IsImage(background))
            throw new ArgumentException("A board background is one of the presets, or an image at an https:// address or on this site.", nameof(settings));
        var validFaceFields = (await db.WikiDatabaseProperties.AsNoTracking()
                .Where(property => property.WikiDatabaseId == databaseId && property.Type != WikiDatabasePropertyTypes.Title)
                .Select(property => property.Id).ToListAsync(cancellationToken)).ToHashSet();
        var clean = settings with
        {
            CardSize = KanbanCardSizes.All.Contains(settings.CardSize) ? settings.CardSize : KanbanCardSizes.Comfortable,
            Accent = settings.Accent is null ? null : SafeColor(settings.Accent),
            Background = settings.Background == KanbanLook.NoBackground ? null : settings.Background,
            CardFaceFields = settings.CardFaceFields?.Where(validFaceFields.Contains).Distinct().ToList()
        };

        var now = DateTimeOffset.UtcNow;
        var row = await db.KanbanBoardSettings.FirstOrDefaultAsync(item => item.WikiDatabaseId == databaseId, cancellationToken);
        if (row is null)
        {
            row = new KanbanBoardSettings { WikiDatabaseId = databaseId, CreatedAt = now, CreatedBy = performedBy };
            db.KanbanBoardSettings.Add(row);
        }
        row.SettingsJson = System.Text.Json.JsonSerializer.Serialize(clean, SettingsJson);
        row.UpdatedAt = now;
        row.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetBlockedAsync(Guid databaseId, Guid rowId, bool blocked, string? reason, string performedBy, CancellationToken cancellationToken = default)
    {
        var properties = await db.WikiDatabaseProperties.AsNoTracking().Where(property => property.WikiDatabaseId == databaseId).ToListAsync(cancellationToken);
        async Task<Guid> EnsureAsync(string name, string type) =>
            Find(properties, type, [name])?.Id
            ?? (await databases.SavePropertyAsync(databaseId, new WikiDatabasePropertyEditor { Name = name, Type = type }, performedBy, cancellationToken)).Id;
        var flag = await EnsureAsync(BlockedName, WikiDatabasePropertyTypes.Checkbox);
        var why = await EnsureAsync(BlockedReasonName, WikiDatabasePropertyTypes.Text);
        await SetCardsValueAsync(databaseId, [rowId], flag, _ => JsonValue.Create(blocked), performedBy, cancellationToken);
        var text = blocked && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : null;
        await SetCardsValueAsync(databaseId, [rowId], why, _ => text is null ? null : JsonValue.Create(text), performedBy, cancellationToken);
    }

    public async Task SetWaitingOnAsync(Guid databaseId, Guid rowId, IReadOnlyCollection<Guid> dependencyRowIds, string performedBy, CancellationToken cancellationToken = default)
    {
        var dependencies = dependencyRowIds.Distinct().ToList();
        if (dependencies.Contains(rowId)) throw new ArgumentException("A card can't wait on itself.", nameof(dependencyRowIds));
        var valid = await db.WikiDatabaseRows.AsNoTracking()
            .Where(item => item.WikiDatabaseId == databaseId && item.TrashedAt == null && item.ParentRowId == null && dependencies.Contains(item.Id))
            .Select(item => item.Id).ToListAsync(cancellationToken);
        if (valid.Count != dependencies.Count) throw new ArgumentException("Dependencies must be cards on this board.", nameof(dependencyRowIds));

        var properties = await db.WikiDatabaseProperties.AsNoTracking().Where(property => property.WikiDatabaseId == databaseId).ToListAsync(cancellationToken);
        var relation = Find(properties, WikiDatabasePropertyTypes.Relation, [WaitingOnName])
            ?? await databases.SavePropertyAsync(databaseId, new WikiDatabasePropertyEditor
            {
                Name = WaitingOnName, Type = WikiDatabasePropertyTypes.Relation, RelatedDatabaseId = databaseId
            }, performedBy, cancellationToken);
        await SetCardsValueAsync(databaseId, [rowId], relation.Id,
            _ => dependencies.Count == 0 ? null : new JsonArray(dependencies.Select(id => (JsonNode?)JsonValue.Create(id.ToString())).ToArray()),
            performedBy, cancellationToken);

        var board = await GetBoardAsync(databaseId, cancellationToken);
        var open = board?.Columns.SelectMany(column => column.Cards).FirstOrDefault(card => card.RowId == rowId)?.WaitingOnOpen ?? 0;
        if (open > 0) await SetBlockedAsync(databaseId, rowId, true, $"Waiting on {open} {(open == 1 ? "card" : "cards")}", performedBy, cancellationToken);
        else await ClearAutomaticDependencyBlockAsync(databaseId, rowId, performedBy, cancellationToken);
    }

    private async Task UnblockDependentsAsync(Guid databaseId, Guid completedRowId, string performedBy, CancellationToken cancellationToken)
    {
        var board = await GetBoardAsync(databaseId, cancellationToken);
        if (board?.Fields.WaitingOnPropertyId is null) return;
        foreach (var card in board.Columns.SelectMany(column => column.Cards)
                     .Where(card => card.WaitingOn?.Contains(completedRowId) == true && card.WaitingOnOpen == 0))
            await ClearAutomaticDependencyBlockAsync(databaseId, card.RowId, performedBy, cancellationToken, notifyAssignees: true);
    }

    private async Task ClearAutomaticDependencyBlockAsync(Guid databaseId, Guid rowId, string performedBy, CancellationToken cancellationToken, bool notifyAssignees = false)
    {
        var board = await GetBoardAsync(databaseId, cancellationToken);
        var card = board?.Columns.SelectMany(column => column.Cards).FirstOrDefault(card => card.RowId == rowId);
        if (card?.Blocked == true && card.BlockedReason?.StartsWith("Waiting on ", StringComparison.Ordinal) == true)
        {
            await SetBlockedAsync(databaseId, rowId, false, null, performedBy, cancellationToken);
            if (notifyAssignees && board is not null)
            {
                var active = await db.AppUsers.AsNoTracking().Where(user => user.IsActive)
                    .Select(user => user.Username).ToListAsync(cancellationToken);
                var eligible = active.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var now = DateTimeOffset.UtcNow;
                var key = $"dependencies-cleared:{now.ToUnixTimeSeconds()}";
                var name = card.Key is null ? $"\"{card.Title}\"" : $"{card.Key} \"{card.Title}\"";
                foreach (var assignee in card.Assignees.Where(eligible.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    db.BoardNotifications.Add(new BoardNotification
                    {
                        Username = assignee, WikiDatabaseId = databaseId, RowId = rowId,
                        Kind = BoardNotificationKinds.DependencyCleared, Key = key,
                        Message = $"{board.Title}: {name} is unblocked; its final dependency is complete.",
                        CreatedAt = now, CreatedBy = performedBy
                    });
                }
                await db.SaveChangesAsync(cancellationToken);
                foreach (var assignee in card.Assignees.Where(eligible.Contains).Distinct(StringComparer.OrdinalIgnoreCase)) notifier?.Notify(assignee);
            }
        }
    }

    // ---- Flow metrics (K5) ----

    public async Task<KanbanInsights?> GetInsightsAsync(Guid databaseId, int days, CancellationToken cancellationToken = default)
    {
        if (days is not (30 or 90 or 180)) throw new ArgumentException("Insights cover 30, 90 or 180 days.", nameof(days));
        var board = await LoadBoardAsync(databaseId, includeArchived: true, cancellationToken);
        if (board is null) return null;
        var status = board.Fields.StatusPropertyId;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-(days - 1));
        var groups = board.Columns.Where(column => column.OptionId is not null).ToDictionary(column => column.OptionId!, column => column.Group);
        bool Is(string? optionId, string group) => optionId is not null && groups.GetValueOrDefault(optionId) == group;

        var log = (await db.WikiDatabaseRowStatusChanges.AsNoTracking()
                .Where(change => change.WikiDatabaseId == databaseId && change.PropertyId == status)
                .Select(change => new { change.RowId, change.ToOptionId, change.AtUnixSeconds })
                .ToListAsync(cancellationToken))
            .OrderBy(change => change.AtUnixSeconds)
            .ToList();
        var byRow = log.GroupBy(change => change.RowId).ToDictionary(group => group.Key, group => group.ToList());
        DateTimeOffset At(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);
        DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.LocalDateTime);

        var cards = board.Columns.SelectMany(column => column.Cards.Select(card => (Card: card, Column: column))).ToList();

        // Cycle time for cards now complete whose finish falls in the window.
        var cycles = new List<KanbanCycle>();
        foreach (var (card, column) in cards.Where(entry => entry.Column.Group == WikiDatabaseStatusGroups.Complete))
        {
            if (!byRow.TryGetValue(card.RowId, out var changes)) continue;
            var finish = changes.LastOrDefault(change => Is(change.ToOptionId, WikiDatabaseStatusGroups.Complete));
            if (finish is null || Day(At(finish.AtUnixSeconds)) < from) continue;
            var started = changes.FirstOrDefault(change => Is(change.ToOptionId, WikiDatabaseStatusGroups.InProgress) && change.AtUnixSeconds <= finish.AtUnixSeconds)?.AtUnixSeconds
                ?? card.CreatedAt.ToUnixTimeSeconds();
            cycles.Add(new KanbanCycle(card.RowId, card.Key, card.Title, Day(At(finish.AtUnixSeconds)), Math.Max(0, (finish.AtUnixSeconds - started) / 86400d)));
        }
        cycles = cycles.OrderBy(cycle => cycle.Finished).ToList();
        double? Percentile(double p)
        {
            if (cycles.Count == 0) return null;
            var sorted = cycles.Select(cycle => cycle.Days).Order().ToList();
            return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        }

        // Weeks start on Monday; the first is the one the window opens in.
        var firstWeek = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        var throughput = new List<KanbanWeek>();
        for (var week = firstWeek; week <= today; week = week.AddDays(7))
        {
            var end = week.AddDays(7);
            throughput.Add(new KanbanWeek(week, cycles.Count(cycle => cycle.Finished >= week && cycle.Finished < end)));
        }

        // Cumulative flow: each card's column at the end of each day, from its last logged move by
        // then; a card with no log (from before logging) sits in its column now from its creation.
        var flowColumns = board.Columns.Where(column => column.OptionId is not null)
            .OrderBy(column => column.Group switch { WikiDatabaseStatusGroups.Complete => 0, WikiDatabaseStatusGroups.InProgress => 1, _ => 2 })
            .ThenByDescending(column => board.Columns.ToList().IndexOf(column))
            .Select(column => new KanbanFlowColumn(column.OptionId!, column.Label, column.Color, column.Group))
            .ToList();
        var index = flowColumns.Select((column, i) => (column.OptionId, i)).ToDictionary(entry => entry.OptionId, entry => entry.i);
        var flow = new List<KanbanFlowDay>();
        for (var day = from; day <= today; day = day.AddDays(1))
        {
            var endOfDay = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUnixTimeSeconds();
            var counts = new int[flowColumns.Count];
            foreach (var (card, column) in cards)
            {
                string? where;
                if (byRow.TryGetValue(card.RowId, out var changes))
                    where = changes.LastOrDefault(change => change.AtUnixSeconds < endOfDay)?.ToOptionId;
                else
                    where = card.CreatedAt.ToUnixTimeSeconds() < endOfDay ? column.OptionId : null;
                if (where is not null && index.TryGetValue(where, out var i)) counts[i]++;
            }
            flow.Add(new KanbanFlowDay(day, counts));
        }

        var aging = cards.Where(entry => entry.Column.Group == WikiDatabaseStatusGroups.InProgress && entry.Card.EnteredColumnAt is not null)
            .Select(entry => new KanbanAging(entry.Card.RowId, entry.Card.Key, entry.Card.Title, entry.Column.Label, entry.Column.Color,
                Math.Max(0, (DateTimeOffset.UtcNow - entry.Card.EnteredColumnAt!.Value).TotalDays)))
            .OrderByDescending(item => item.Days)
            .ToList();

        // Forecasts (K8) sample the days the log covers, up to yesterday - today isn't over, and a
        // day from before logging began would count as a day nothing was finished.
        var historyStarts = log.Count == 0 ? (DateOnly?)null : Day(At(log[0].AtUnixSeconds));
        var dailyDone = new List<int>();
        if (historyStarts is { } logged)
        {
            var finishedOn = cycles.GroupBy(cycle => cycle.Finished).ToDictionary(group => group.Key, group => group.Count());
            for (var day = logged > from ? logged : from; day < today; day = day.AddDays(1)) dailyDone.Add(finishedOn.GetValueOrDefault(day));
        }
        int Open(Func<KanbanCard, KanbanColumn, bool> include) =>
            cards.Count(entry => entry.Column.Group != WikiDatabaseStatusGroups.Complete && include(entry.Card, entry.Column));
        var forecasts = new List<KanbanNamedForecast>
        {
            new("open", "Everything open", KanbanForecasting.Forecast(dailyDone, Open((_, _) => true), today))
        };
        var sprintOptions = board.SprintOptions ?? [];
        if ((board.Settings?.Sprints ?? []).FirstOrDefault(sprint => sprint.State == KanbanSprintStates.Active) is { } running
            && sprintOptions.FirstOrDefault(option => option.Id == running.OptionId) is { } runningOption)
        {
            forecasts.Add(new KanbanNamedForecast("sprint", runningOption.Label, KanbanForecasting.Forecast(dailyDone, Open((card, _) => card.SprintOptionId == running.OptionId), today)));
        }
        forecasts.Add(new KanbanNamedForecast("doing", "What's in progress", KanbanForecasting.Forecast(dailyDone,
            Open((_, column) => column.Group == WikiDatabaseStatusGroups.InProgress), today)));

        return new KanbanInsights(days, from, historyStarts, cycles, Percentile(0.5), Percentile(0.85),
            throughput, flowColumns, flow, aging, cards.Count(entry => entry.Column.Group == WikiDatabaseStatusGroups.InProgress),
            dailyDone, forecasts);
    }

    // ---- Sprints (K4) ----

    private static readonly string[] SprintColors = ["#2563eb", "#7c3aed", "#0891b2", "#d97706", "#15803d", "#db2777"];

    public async Task<IReadOnlyList<KanbanSprintView>> ListSprintsAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        var database = await databases.GetDatabaseAsync(databaseId, cancellationToken);
        if (database is null || StatusProperty(database) is not { } status) return [];
        var properties = database.Properties.ToList();
        if (Find(properties, WikiDatabasePropertyTypes.Select, SprintNames) is not { } sprintProperty) return [];
        var estimate = Find(properties, WikiDatabasePropertyTypes.Number, EstimateNames);
        var complete = CompleteOptionIds(status);
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        var cards = database.Rows.Where(row => row.ParentRowId is null).Select(row =>
        {
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            return new SprintCard(row.Id, WikiPropertyValues.GetText(values, sprintProperty.Id),
                WikiPropertyValues.GetText(values, status.Id) is { } option && complete.Contains(option),
                estimate is null ? null : WikiPropertyValues.GetNumber(values, estimate.Id));
        }).ToList();
        // When each finished card last moved into a Complete column.
        var finishedAt = (await db.WikiDatabaseRowStatusChanges.AsNoTracking()
                .Where(change => change.WikiDatabaseId == databaseId && change.PropertyId == status.Id && change.ToOptionId != null)
                .Select(change => new { change.RowId, change.ToOptionId, change.AtUnixSeconds })
                .ToListAsync(cancellationToken))
            .Where(change => complete.Contains(change.ToOptionId!))
            .GroupBy(change => change.RowId)
            .ToDictionary(group => group.Key, group => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(group.Max(change => change.AtUnixSeconds)).LocalDateTime));
        var today = DateOnly.FromDateTime(DateTime.Now);

        return WikiDatabasePropertyConfig.GetOptions(sprintProperty).Select(option =>
        {
            var sprint = settings.Sprints.FirstOrDefault(item => item.OptionId == option.Id) ?? new KanbanSprint { OptionId = option.Id };
            // Points when the sprint's cards carry estimates; otherwise each card counts one.
            var raw = cards.Where(card => card.Sprint == option.Id).ToList();
            var usesEstimates = raw.Any(card => card.Estimate is not null);
            var inSprint = raw.Select(card => new SprintPoints(card.RowId, card.Done, usesEstimates ? card.Estimate ?? 0m : 1m)).ToList();
            return new KanbanSprintView(option.Id, option.Label, SafeColor(option.Color), sprint.Goal, sprint.Start, sprint.End, sprint.State, sprint.Capacity,
                inSprint.Count, inSprint.Count(card => card.Done), inSprint.Sum(card => card.Points), inSprint.Where(card => card.Done).Sum(card => card.Points),
                usesEstimates, Burndown(sprint, inSprint, finishedAt, today), sprint.CompletedAt);
        }).ToList();
    }

    private sealed record SprintCard(Guid RowId, string? Sprint, bool Done, decimal? Estimate);
    private sealed record SprintPoints(Guid RowId, bool Done, decimal Points);

    // Points left at the end of each day from the sprint's start to today (or its end): the scope
    // is the sprint's cards now; a finished card counts from the day it last reached a Complete
    // column (from the start, for one finished before the log).
    private static IReadOnlyList<KanbanBurnPoint> Burndown(KanbanSprint sprint, IReadOnlyList<SprintPoints> cards, IReadOnlyDictionary<Guid, DateOnly> finishedAt, DateOnly today)
    {
        if (sprint.Start is not { } start) return [];
        var last = sprint.State == KanbanSprintStates.Done && sprint.CompletedAt is { } completed ? DateOnly.FromDateTime(completed.LocalDateTime) : today;
        if (sprint.End is { } end && end < last) last = end;
        if (last < start) return [];
        var total = cards.Sum(card => card.Points);
        var points = new List<KanbanBurnPoint>();
        for (var day = start; day <= last; day = day.AddDays(1))
        {
            var finished = cards.Where(card => card.Done && finishedAt.GetValueOrDefault(card.RowId, start) <= day).Sum(card => card.Points);
            points.Add(new KanbanBurnPoint(day, total - finished));
        }
        return points;
    }

    public async Task<string> CreateSprintAsync(Guid databaseId, string name, string performedBy, CancellationToken cancellationToken = default)
    {
        var properties = await db.WikiDatabaseProperties.AsNoTracking().Where(property => property.WikiDatabaseId == databaseId).ToListAsync(cancellationToken);
        var sprintProperty = Find(properties, WikiDatabasePropertyTypes.Select, SprintNames);
        var existing = sprintProperty is null ? [] : WikiDatabasePropertyConfig.GetOptions(sprintProperty);
        var label = string.IsNullOrWhiteSpace(name) ? $"Sprint {existing.Count + 1}" : name.Trim();
        if (existing.Any(option => string.Equals(option.Label, label, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There's already a sprint called {label}.", nameof(name));
        var option = new WikiDatabasePropertyOption(Guid.NewGuid().ToString(), label, SprintColors[existing.Count % SprintColors.Length]);
        await databases.SavePropertyAsync(databaseId, new WikiDatabasePropertyEditor
        {
            Id = sprintProperty?.Id,
            Name = sprintProperty?.Name ?? "Sprint",
            Type = WikiDatabasePropertyTypes.Select,
            Options = [.. existing, option]
        }, performedBy, cancellationToken);
        await UpdateSettingsAsync(databaseId, settings => settings with
        {
            Sprints = [.. settings.Sprints, new KanbanSprint { OptionId = option.Id }]
        }, performedBy, cancellationToken);
        return option.Id;
    }

    public Task UpdateSprintAsync(Guid databaseId, string optionId, string goal, decimal? capacity, string performedBy, CancellationToken cancellationToken = default)
    {
        if (capacity is < 0) throw new ArgumentException("Capacity can't be negative.", nameof(capacity));
        return UpdateSprintEntryAsync(databaseId, optionId, sprint => sprint with { Goal = goal.Trim(), Capacity = capacity }, performedBy, cancellationToken);
    }

    public async Task StartSprintAsync(Guid databaseId, string optionId, DateOnly start, DateOnly end, string performedBy, CancellationToken cancellationToken = default)
    {
        if (end < start) throw new ArgumentException("A sprint ends after it starts.", nameof(end));
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        if (settings.Sprints.Any(sprint => sprint.State == KanbanSprintStates.Active && sprint.OptionId != optionId))
            throw new InvalidOperationException("Another sprint is running. Complete it first.");
        await UpdateSprintEntryAsync(databaseId, optionId, sprint => sprint.State == KanbanSprintStates.Done
            ? throw new InvalidOperationException("That sprint is already complete.")
            : sprint with { State = KanbanSprintStates.Active, Start = start, End = end }, performedBy, cancellationToken);
    }

    public async Task<KanbanSprintSummary> CompleteSprintAsync(Guid databaseId, string optionId, string? moveTo, string performedBy, CancellationToken cancellationToken = default)
    {
        var sprints = await ListSprintsAsync(databaseId, cancellationToken);
        var sprint = sprints.FirstOrDefault(item => item.OptionId == optionId) ?? throw new InvalidOperationException("That sprint no longer exists.");
        if (sprint.State == KanbanSprintStates.Done) throw new InvalidOperationException("That sprint is already complete.");
        if (moveTo is { Length: > 0 } && (moveTo == optionId || sprints.All(item => item.OptionId != moveTo || item.State == KanbanSprintStates.Done)))
            throw new ArgumentException("Unfinished cards can only move to a sprint that isn't complete.", nameof(moveTo));

        var target = moveTo ?? await CreateSprintAsync(databaseId, string.Empty, performedBy, cancellationToken);
        var properties = await db.WikiDatabaseProperties.AsNoTracking().Where(property => property.WikiDatabaseId == databaseId).ToListAsync(cancellationToken);
        var sprintProperty = Find(properties, WikiDatabasePropertyTypes.Select, SprintNames)!;
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var complete = CompleteOptionIds(status);
        var unfinished = (await db.WikiDatabaseRows.AsNoTracking()
                .Where(row => row.WikiDatabaseId == databaseId && row.TrashedAt == null && row.ParentRowId == null)
                .Select(row => new { row.Id, row.PropertyValuesJson })
                .ToListAsync(cancellationToken))
            .Where(row =>
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                return WikiPropertyValues.GetText(values, sprintProperty.Id) == optionId
                    && !(WikiPropertyValues.GetText(values, status.Id) is { } column && complete.Contains(column));
            })
            .Select(row => row.Id)
            .ToList();
        await SetCardsValueAsync(databaseId, unfinished, sprintProperty.Id, _ => target.Length == 0 ? null : JsonValue.Create(target), performedBy, cancellationToken);
        await UpdateSprintEntryAsync(databaseId, optionId, entry => entry with
        {
            State = KanbanSprintStates.Done,
            CompletedAt = DateTimeOffset.UtcNow,
            DoneCards = sprint.DoneCount,
            DonePoints = sprint.DonePoints
        }, performedBy, cancellationToken);
        var movedTo = target.Length == 0 ? null : WikiDatabasePropertyConfig.GetOptions(sprintProperty).FirstOrDefault(option => option.Id == target)?.Label
            ?? (await ListSprintsAsync(databaseId, cancellationToken)).FirstOrDefault(item => item.OptionId == target)?.Name;
        return new KanbanSprintSummary(sprint.Name, sprint.DoneCount, sprint.DonePoints, unfinished.Count, movedTo);
    }

    private Task UpdateSprintEntryAsync(Guid databaseId, string optionId, Func<KanbanSprint, KanbanSprint> change, string performedBy, CancellationToken cancellationToken) =>
        UpdateSettingsAsync(databaseId, settings =>
        {
            var current = settings.Sprints.FirstOrDefault(sprint => sprint.OptionId == optionId) ?? new KanbanSprint { OptionId = optionId };
            return settings with { Sprints = [.. settings.Sprints.Where(sprint => sprint.OptionId != optionId), change(current)] };
        }, performedBy, cancellationToken);

    // Settings changed from what's stored now, not from a copy read earlier.
    private async Task UpdateSettingsAsync(Guid databaseId, Func<KanbanSettings, KanbanSettings> change, string performedBy, CancellationToken cancellationToken) =>
        await SaveSettingsAsync(databaseId, change(await GetSettingsAsync(databaseId, cancellationToken)), performedBy, cancellationToken);

    public async Task<int> CompleteSubItemsAsync(Guid databaseId, Guid parentRowId, string performedBy, CancellationToken cancellationToken = default)
    {
        var open = (await ListSubItemsAsync(databaseId, parentRowId, cancellationToken)).Where(item => !item.Done).ToList();
        foreach (var item in open) await SetSubItemDoneAsync(databaseId, item.RowId, true, performedBy, cancellationToken);
        return open.Count;
    }

    public async Task<Guid> AddQuickCardAsync(Guid databaseId, string? optionId, KanbanQuickCard card, string performedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(card.Title)) throw new ArgumentException("A card needs a title.", nameof(card));
        var board = await GetBoardAsync(databaseId, cancellationToken) ?? throw new InvalidOperationException("That board no longer exists.");
        var titleProperty = await TitlePropertyAsync(databaseId, cancellationToken);
        var fields = board.Fields;
        var values = new JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, card.Title.Trim());
        if (optionId is not null) WikiPropertyValues.SetText(values, fields.StatusPropertyId, optionId);
        // Only what the board has: the parser checked, this checks again for a stale preview.
        if (card.PriorityOptionId is { } priority && fields.PriorityPropertyId is { } priorityField && board.PriorityOptions.Any(option => option.Id == priority))
            WikiPropertyValues.SetText(values, priorityField, priority);
        var labels = card.LabelOptionIds.Where(id => board.LabelOptions.Any(option => option.Id == id)).Distinct().ToList();
        if (labels.Count > 0 && fields.LabelsPropertyId is { } labelsField) WikiPropertyValues.SetMultiSelect(values, labelsField, labels);
        if (card.People.Count > 0 && fields.AssigneePropertyId is { } assigneeField)
            values[assigneeField.ToString()] = new JsonArray(card.People.Distinct(StringComparer.OrdinalIgnoreCase).Select(person => (JsonNode?)JsonValue.Create(person)).ToArray());
        // A picked date is kept at midnight UTC, as Sentinel's date picker stores it.
        if (card.Due is { } due && fields.DuePropertyId is { } dueField)
            WikiPropertyValues.SetDate(values, dueField, new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (card.Estimate is { } estimate && fields.EstimatePropertyId is { } estimateField) WikiPropertyValues.SetNumber(values, estimateField, estimate);

        var row = await databases.SaveRowAsync(databaseId, new WikiDatabaseRowEditor
        {
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone())
        }, performedBy, cancellationToken);
        await databases.MoveRowAsync(databaseId, row.Id, fields.StatusPropertyId, optionId, 0, performedBy, cancellationToken);
        return row.Id;
    }

    public async Task<KanbanForecast?> ForecastAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default)
    {
        var insights = await GetInsightsAsync(databaseId, 90, cancellationToken);
        var board = insights is null ? null : await GetBoardAsync(databaseId, cancellationToken);
        if (insights is null || board is null) return null;
        var wanted = rowIds.ToHashSet();
        var remaining = board.Columns.Where(column => column.Group != WikiDatabaseStatusGroups.Complete)
            .Sum(column => column.Cards.Count(card => wanted.Contains(card.RowId)));
        return KanbanForecasting.Forecast(insights.DailyDone ?? [], remaining, DateOnly.FromDateTime(DateTime.Now));
    }

    public async Task<KanbanBrief?> GetBriefAsync(Guid databaseId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var board = await GetBoardAsync(databaseId, cancellationToken);
        if (board is null) return null;
        var labels = board.Columns.Where(column => column.OptionId is not null).ToDictionary(column => column.OptionId!, column => column.Label);
        var cards = board.Columns.SelectMany(column => column.Cards.Select(card => (Card: card, Column: column))).ToDictionary(entry => entry.Card.RowId);
        var sinceSeconds = since.ToUnixTimeSeconds();
        var log = (await db.WikiDatabaseRowStatusChanges.AsNoTracking()
                .Where(change => change.WikiDatabaseId == databaseId && change.PropertyId == board.Fields.StatusPropertyId && change.AtUnixSeconds >= sinceSeconds)
                .Select(change => new { change.RowId, change.FromOptionId, change.ToOptionId, change.AtUnixSeconds, change.CreatedBy })
                .ToListAsync(cancellationToken))
            .OrderBy(change => change.AtUnixSeconds)
            .ToList();
        // Cards only: a sub-item ticked off isn't news for the standup.
        var moves = log
            .Where(change => change.ToOptionId is not null && labels.ContainsKey(change.ToOptionId) && cards.ContainsKey(change.RowId))
            .Select(change =>
            {
                var card = cards[change.RowId].Card;
                return new KanbanBriefMove(change.RowId, card.Key, card.Title,
                    change.FromOptionId is { } from && labels.TryGetValue(from, out var fromLabel) ? fromLabel : null,
                    labels[change.ToOptionId!], string.IsNullOrWhiteSpace(change.CreatedBy) ? "someone" : change.CreatedBy,
                    DateTimeOffset.FromUnixTimeSeconds(change.AtUnixSeconds));
            })
            .ToList();

        // Stuck: blocked anywhere open, or in progress past the board's 85% cycle time (its aging
        // threshold before there's history to say).
        var typical = (await GetInsightsAsync(databaseId, 90, cancellationToken))?.P85CycleDays;
        var threshold = typical ?? (board.Settings ?? KanbanSettings.Default).AgingDays;
        var now = DateTimeOffset.UtcNow;
        var stuck = cards.Values
            .Where(entry => entry.Column.Group != WikiDatabaseStatusGroups.Complete)
            .Select(entry => (entry.Card, entry.Column, Days: entry.Card.EnteredColumnAt is { } entered ? Math.Max(0, (now - entered).TotalDays) : 0))
            .Where(entry => entry.Card.Blocked || (entry.Column.Group == WikiDatabaseStatusGroups.InProgress && entry.Days >= threshold))
            .OrderByDescending(entry => entry.Card.Blocked).ThenByDescending(entry => entry.Days)
            .Take(10)
            .Select(entry => new KanbanBriefCard(entry.Card.RowId, entry.Card.Key, entry.Card.Title, entry.Column.Label, entry.Days,
                entry.Card.Blocked, entry.Card.BlockedReason, entry.Card.Assignees, typical is { } p85 && entry.Days > p85))
            .ToList();
        var overLimit = board.Columns.Where(column => column.WipLimit is { } limit && column.Cards.Count > limit).Select(column => column.Label).ToList();
        return new KanbanBrief(since, moves, stuck, overLimit, typical);
    }

    // ---- Public roadmap (K8e) ----

    public async Task<KanbanRoadmapView> GetRoadmapAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        if (settings.Roadmap is not { } roadmap) return new KanbanRoadmapView(false, null, [], true, false, 0, 0);
        var views = await db.SentinelPublicShares.AsNoTracking().Where(share => share.Id == roadmap.ShareId).Select(share => share.ViewCount).FirstOrDefaultAsync(cancellationToken);
        var totalVotes = await db.BoardCardVotes.AsNoTracking().CountAsync(vote => vote.WikiDatabaseId == databaseId, cancellationToken);
        return new KanbanRoadmapView(true, RoadmapUrl(roadmap), roadmap.Columns, roadmap.Votes, roadmap.Descriptions, views, totalVotes);
    }

    public async Task<KanbanRoadmapView> PublishRoadmapAsync(Guid databaseId, IReadOnlyList<string> columns, bool votes, bool descriptions, string performedBy,
        CancellationToken cancellationToken = default)
    {
        if (access is null || secrets is null) throw new InvalidOperationException("Publishing isn't available here.");
        var board = await GetBoardAsync(databaseId, cancellationToken) ?? throw new InvalidOperationException("That board no longer exists.");
        var chosen = board.Columns.Where(column => column.OptionId is not null && columns.Contains(column.OptionId)).Select(column => column.OptionId!).ToList();
        if (chosen.Count == 0) throw new ArgumentException("Choose at least one column to show.", nameof(columns));
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        var roadmap = settings.Roadmap;
        // A link that was revoked elsewhere (Sentinel's share panel) gets a fresh one.
        var live = roadmap is not null && await db.SentinelPublicShares.AsNoTracking().AnyAsync(share => share.Id == roadmap.ShareId && share.RevokedAt == null, cancellationToken);
        if (!live)
        {
            var share = await access.CreatePublicShareAsync(databaseId, isDatabase: true, expiresAt: null, allowSearchIndexing: false, password: null,
                performedBy, cancellationToken: cancellationToken, isRoadmap: true);
            roadmap = new KanbanRoadmap { ShareId = share.Id, ProtectedToken = secrets.Protect(share.PublicToken!) };
        }
        await SaveSettingsAsync(databaseId, settings with { Roadmap = roadmap! with { Columns = chosen, Votes = votes, Descriptions = descriptions } }, performedBy, cancellationToken);
        return await GetRoadmapAsync(databaseId, cancellationToken);
    }

    public async Task UnpublishRoadmapAsync(Guid databaseId, string performedBy, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(databaseId, cancellationToken);
        if (settings.Roadmap is not { } roadmap) return;
        if (access is not null) await access.RevokePublicShareAsync(roadmap.ShareId, performedBy, cancellationToken);
        await SaveSettingsAsync(databaseId, settings with { Roadmap = null }, performedBy, cancellationToken);
    }

    public async Task<KanbanPublicRoadmap?> GetPublicRoadmapAsync(string token, string? voterHash, CancellationToken cancellationToken = default)
    {
        if (await LiveRoadmapAsync(token, cancellationToken) is not { } live) return null;
        var (share, roadmap, board) = live;
        var shown = board.Columns.Where(column => column.OptionId is not null && roadmap.Columns.Contains(column.OptionId)).ToList();
        var ids = shown.SelectMany(column => column.Cards).Select(card => card.RowId).ToList();
        var counts = (await db.BoardCardVotes.AsNoTracking().Where(vote => ids.Contains(vote.RowId)).Select(vote => new { vote.RowId, vote.VoterHash }).ToListAsync(cancellationToken))
            .GroupBy(vote => vote.RowId).ToDictionary(group => group.Key, group => (Count: group.Count(), Mine: voterHash is not null && group.Any(vote => vote.VoterHash == voterHash)));
        var descriptions = new Dictionary<Guid, string?>();
        if (roadmap.Descriptions)
        {
            foreach (var row in await db.WikiDatabaseRows.AsNoTracking().Where(row => ids.Contains(row.Id)).Select(row => new { row.Id, row.BlocksJson }).ToListAsync(cancellationToken))
                descriptions[row.Id] = SnippetOf(WikiBlockJson.ParseBlocks(row.BlocksJson));
        }
        return new KanbanPublicRoadmap(share.Id, board.Title, board.Icon,
            shown.Select(column => new KanbanPublicColumn(column.Label, column.Color, column.Group, column.Cards
                .Select(card => new KanbanPublicCard(card.RowId, card.Title, card.Labels, descriptions.GetValueOrDefault(card.RowId),
                    counts.TryGetValue(card.RowId, out var count) ? count.Count : 0, counts.TryGetValue(card.RowId, out var mine) && mine.Mine))
                .ToList())).ToList(),
            roadmap.Votes);
    }

    public async Task<int?> VoteAsync(string token, Guid rowId, string voterHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voterHash) || voterHash.Length > 64) return null;
        if (await LiveRoadmapAsync(token, cancellationToken) is not { } live || !live.Roadmap.Votes) return null;
        var (_, roadmap, board) = live;
        var onShow = board.Columns.Any(column => column.OptionId is not null && roadmap.Columns.Contains(column.OptionId) && column.Cards.Any(card => card.RowId == rowId));
        if (!onShow) return null;
        var existing = await db.BoardCardVotes.FirstOrDefaultAsync(vote => vote.RowId == rowId && vote.VoterHash == voterHash, cancellationToken);
        if (existing is null) db.BoardCardVotes.Add(new BoardCardVote { WikiDatabaseId = board.DatabaseId, RowId = rowId, VoterHash = voterHash, CreatedBy = "roadmap" });
        else db.BoardCardVotes.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return await db.BoardCardVotes.CountAsync(vote => vote.RowId == rowId, cancellationToken);
    }

    // A token that is a live roadmap: its share, the board's roadmap settings for that same share
    // (a replaced link stops working), and the board.
    private async Task<(SentinelShareView Share, KanbanRoadmap Roadmap, KanbanBoard Board)?> LiveRoadmapAsync(string token, CancellationToken cancellationToken)
    {
        if (access is null || await access.ResolvePublicShareAsync(token, cancellationToken) is not { IsRoadmap: true } share) return null;
        if ((await GetSettingsAsync(share.TargetId, cancellationToken)).Roadmap is not { } roadmap || roadmap.ShareId != share.Id) return null;
        if (await GetBoardAsync(share.TargetId, cancellationToken) is not { } board) return null;
        return (share, roadmap, board);
    }

    private string? RoadmapUrl(KanbanRoadmap roadmap)
    {
        if (secrets is null || string.IsNullOrEmpty(roadmap.ProtectedToken)) return null;
        try { return $"/roadmap/{secrets.Unprotect(roadmap.ProtectedToken)}"; }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { return null; }
    }

    // ---- My day (K8d) ----

    public async Task<KanbanMyDay> GetMyDayAsync(string username, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var cards = new List<KanbanMyDayCard>();
        foreach (var summary in await ListBoardsAsync(cancellationToken))
        {
            if (await GetBoardAsync(summary.DatabaseId, cancellationToken) is not { } board) continue;
            var done = board.Columns.FirstOrDefault(column => column.Group == WikiDatabaseStatusGroups.Complete && column.OptionId is not null)?.OptionId;
            foreach (var column in board.Columns.Where(column => column.Group != WikiDatabaseStatusGroups.Complete))
            {
                foreach (var card in column.Cards.Where(card => card.Assignees.Contains(username, StringComparer.OrdinalIgnoreCase)))
                {
                    // A picked date is stored at midnight UTC: its UTC date is the day as written.
                    var due = card.Due is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : (DateOnly?)null;
                    var bucket = due < today ? KanbanMyDayBuckets.Overdue
                        : due == today ? KanbanMyDayBuckets.Today
                        : column.Group == WikiDatabaseStatusGroups.InProgress ? KanbanMyDayBuckets.Doing
                        : KanbanMyDayBuckets.Next;
                    cards.Add(new KanbanMyDayCard(board.DatabaseId, board.Title, board.Icon, card, column.Label, column.Color, bucket, done));
                }
            }
        }
        int PriorityRank(KanbanCard card) => card.Priority?.Label.ToLowerInvariant() switch { "urgent" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4 };
        var ordered = cards
            .OrderBy(item => item.Bucket switch { KanbanMyDayBuckets.Overdue => 0, KanbanMyDayBuckets.Today => 1, KanbanMyDayBuckets.Doing => 2, _ => 3 })
            .ThenBy(item => item.Card.Due ?? DateTimeOffset.MaxValue)
            .ThenBy(item => PriorityRank(item.Card))
            .ThenBy(item => item.Card.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Tickets and follow-ups are filtered here: SQLite can't compare a DateTimeOffset, and the
        // username is matched without regard to case.
        var tickets = (await db.SupportTickets.AsNoTracking()
                .Where(ticket => ticket.AssignedToUsername != null && (ticket.Status == SupportTicketStatuses.Open || ticket.Status == SupportTicketStatuses.Pending))
                .Select(ticket => new { ticket.Id, ticket.Subject, ticket.Status, ticket.Priority, ticket.ContactId, ticket.AssignedToUsername, ticket.ResolutionDueAt })
                .ToListAsync(cancellationToken))
            .Where(ticket => string.Equals(ticket.AssignedToUsername, username, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var names = await ContactNamesAsync(tickets.Select(ticket => ticket.ContactId), cancellationToken);
        var endOfToday = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        var followUps = (await db.Contacts.AsNoTracking()
                .Where(contact => contact.TrashedAt == null && contact.FollowUpDate != null)
                .Select(contact => new { contact.Id, contact.FullName, contact.Company, contact.FollowUpDate })
                .ToListAsync(cancellationToken))
            .Where(contact => contact.FollowUpDate < endOfToday)
            .OrderBy(contact => contact.FollowUpDate)
            .Take(20)
            .Select(contact => new KanbanMyDayFollowUp(contact.Id, contact.FullName, contact.Company, contact.FollowUpDate!.Value))
            .ToList();
        return new KanbanMyDay(today, ordered,
            tickets.OrderBy(ticket => ticket.ResolutionDueAt ?? DateTimeOffset.MaxValue)
                .Select(ticket => new KanbanMyDayTicket(ticket.Id, ticket.Subject, ticket.Status, ticket.Priority, names.GetValueOrDefault(ticket.ContactId, "no contact"), ticket.ResolutionDueAt))
                .ToList(),
            followUps);
    }

    // ---- Business links (K8c) ----

    public async Task<IReadOnlyList<KanbanCardLink>> ListLinksAsync(Guid databaseId, Guid rowId, CancellationToken cancellationToken = default)
    {
        var links = await db.BoardCardLinks.AsNoTracking()
            .Where(link => link.WikiDatabaseId == databaseId && link.RowId == rowId)
            .ToListAsync(cancellationToken);
        return await DescribeLinksAsync(links, cancellationToken);
    }

    public async Task LinkAsync(Guid databaseId, Guid rowId, string kind, Guid targetId, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!KanbanLinkKinds.All.Contains(kind)) throw new ArgumentException($"A card can't be linked to a {kind}.", nameof(kind));
        if (!await db.WikiDatabaseRows.AnyAsync(row => row.Id == rowId && row.WikiDatabaseId == databaseId && row.TrashedAt == null, cancellationToken))
            throw new InvalidOperationException("That card no longer exists.");
        Guid? alsoContact = null;
        switch (kind)
        {
            case KanbanLinkKinds.Contact:
                if (!await db.Contacts.AnyAsync(contact => contact.Id == targetId && contact.TrashedAt == null, cancellationToken))
                    throw new InvalidOperationException("That contact no longer exists.");
                break;
            case KanbanLinkKinds.Ticket:
                alsoContact = (await db.SupportTickets.AsNoTracking().Where(ticket => ticket.Id == targetId).Select(ticket => (Guid?)ticket.ContactId).FirstOrDefaultAsync(cancellationToken))
                    ?? throw new InvalidOperationException("That ticket no longer exists.");
                break;
            case KanbanLinkKinds.Invoice:
                alsoContact = (await db.Invoices.AsNoTracking().Where(invoice => invoice.Id == targetId).Select(invoice => (Guid?)invoice.ContactId).FirstOrDefaultAsync(cancellationToken))
                    ?? throw new InvalidOperationException("That invoice no longer exists.");
                break;
        }
        var now = DateTimeOffset.UtcNow;
        async Task AddAsync(string linkKind, Guid id)
        {
            if (await db.BoardCardLinks.AnyAsync(link => link.RowId == rowId && link.Kind == linkKind && link.TargetId == id, cancellationToken)) return;
            db.BoardCardLinks.Add(new BoardCardLink { WikiDatabaseId = databaseId, RowId = rowId, Kind = linkKind, TargetId = id, CreatedAt = now, CreatedBy = performedBy });
        }
        await AddAsync(kind, targetId);
        if (alsoContact is { } contactId && contactId != Guid.Empty) await AddAsync(KanbanLinkKinds.Contact, contactId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnlinkAsync(Guid databaseId, Guid linkId, CancellationToken cancellationToken = default)
    {
        var link = await db.BoardCardLinks.FirstOrDefaultAsync(item => item.Id == linkId && item.WikiDatabaseId == databaseId, cancellationToken);
        if (link is null) return;
        db.BoardCardLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KanbanLinkCandidate>> SearchLinkTargetsAsync(string kind, string query, CancellationToken cancellationToken = default)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length < 2) return [];
        var pattern = $"%{text.Replace("%", "").Replace("_", "")}%";
        return kind switch
        {
            KanbanLinkKinds.Contact => (await db.Contacts.AsNoTracking()
                    .Where(contact => contact.TrashedAt == null && (EF.Functions.Like(contact.FullName, pattern) || EF.Functions.Like(contact.Email, pattern) || (contact.Company != null && EF.Functions.Like(contact.Company, pattern))))
                    .OrderBy(contact => contact.FullName).Take(8)
                    .Select(contact => new { contact.Id, contact.FullName, contact.Company, contact.Email })
                    .ToListAsync(cancellationToken))
                .Select(contact => new KanbanLinkCandidate(kind, contact.Id, contact.FullName, string.IsNullOrWhiteSpace(contact.Company) ? contact.Email : contact.Company))
                .ToList(),
            KanbanLinkKinds.Ticket => await NamedAsync((await db.SupportTickets.AsNoTracking()
                    .Where(ticket => EF.Functions.Like(ticket.Subject, pattern))
                    .Select(ticket => new { ticket.Id, ticket.Subject, ticket.Status, ticket.ContactId, ticket.CreatedAt })
                    .Take(50).ToListAsync(cancellationToken))
                // Newest first, sorted here: SQLite can't order by a DateTimeOffset.
                .OrderByDescending(ticket => ticket.CreatedAt).Take(8)
                .Select(ticket => (ticket.Id, ticket.Subject, ticket.Status, ticket.ContactId)).ToList()),
            KanbanLinkKinds.Invoice => await NamedAsync((await db.Invoices.AsNoTracking()
                    .Where(invoice => EF.Functions.Like(invoice.Title, pattern))
                    .Select(invoice => new { invoice.Id, invoice.Title, invoice.Status, invoice.ContactId, invoice.CreatedAt })
                    .Take(50).ToListAsync(cancellationToken))
                .OrderByDescending(invoice => invoice.CreatedAt).Take(8)
                .Select(invoice => (invoice.Id, invoice.Title, invoice.Status, invoice.ContactId)).ToList()),
            _ => []
        };

        // "Open · Ada Lovelace": each record's status and its contact's name.
        async Task<IReadOnlyList<KanbanLinkCandidate>> NamedAsync(IReadOnlyList<(Guid Id, string Title, string Status, Guid ContactId)> found)
        {
            var names = await ContactNamesAsync(found.Select(item => item.ContactId), cancellationToken);
            return found.Select(item => new KanbanLinkCandidate(kind, item.Id, item.Title, $"{item.Status} · {names.GetValueOrDefault(item.ContactId, "no contact")}")).ToList();
        }
    }

    private async Task<Dictionary<Guid, string>> ContactNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Contacts.AsNoTracking().Where(contact => wanted.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);
    }

    public async Task<Guid> AddCardFromTicketAsync(Guid databaseId, Guid ticketId, string? optionId, string performedBy, CancellationToken cancellationToken = default)
    {
        var subject = await db.SupportTickets.AsNoTracking().Where(ticket => ticket.Id == ticketId).Select(ticket => ticket.Subject).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("That ticket no longer exists.");
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var column = optionId ?? ColumnOrder(status).FirstOrDefault()?.Id;
        var rowId = await AddCardAsync(databaseId, column, string.IsNullOrWhiteSpace(subject) ? "Support ticket" : subject.Trim(), performedBy, cancellationToken);
        await LinkAsync(databaseId, rowId, KanbanLinkKinds.Ticket, ticketId, performedBy, cancellationToken);
        return rowId;
    }

    public async Task<IReadOnlyList<KanbanCardLink>> OpenTicketsAsync(Guid databaseId, Guid rowId, CancellationToken cancellationToken = default) =>
        (await ListLinksAsync(databaseId, rowId, cancellationToken))
            .Where(link => link.Kind == KanbanLinkKinds.Ticket && !link.Gone
                && link.Status is not (SupportTicketStatuses.Resolved or SupportTicketStatuses.Closed))
            .ToList();

    // Each link with what its record says now; contacts first, then tickets, then invoices.
    private async Task<IReadOnlyList<KanbanCardLink>> DescribeLinksAsync(IReadOnlyList<BoardCardLink> links, CancellationToken cancellationToken)
    {
        if (links.Count == 0) return [];
        List<Guid> Ids(string kind) => links.Where(link => link.Kind == kind).Select(link => link.TargetId).ToList();
        var contactIds = Ids(KanbanLinkKinds.Contact);
        var ticketIds = Ids(KanbanLinkKinds.Ticket);
        var invoiceIds = Ids(KanbanLinkKinds.Invoice);
        var contacts = await db.Contacts.AsNoTracking().Where(contact => contactIds.Contains(contact.Id) && contact.TrashedAt == null)
            .ToDictionaryAsync(contact => contact.Id, contact => new { contact.FullName, contact.Company }, cancellationToken);
        var tickets = await db.SupportTickets.AsNoTracking().Where(ticket => ticketIds.Contains(ticket.Id))
            .ToDictionaryAsync(ticket => ticket.Id, ticket => new { ticket.Subject, ticket.Status, ticket.ContactId }, cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(invoice => invoiceIds.Contains(invoice.Id))
            .ToDictionaryAsync(invoice => invoice.Id, invoice => new { invoice.Title, invoice.Status, invoice.ContactId }, cancellationToken);
        var names = await ContactNamesAsync(tickets.Values.Select(ticket => ticket.ContactId).Concat(invoices.Values.Select(invoice => invoice.ContactId)), cancellationToken);
        return links
            .Select(link => link.Kind switch
            {
                KanbanLinkKinds.Contact => contacts.TryGetValue(link.TargetId, out var contact)
                    ? new KanbanCardLink(link.Id, link.Kind, link.TargetId, contact.FullName, null, false, link.TargetId, contact.Company)
                    : new KanbanCardLink(link.Id, link.Kind, link.TargetId, "Deleted contact", null, true, null),
                KanbanLinkKinds.Ticket => tickets.TryGetValue(link.TargetId, out var ticket)
                    ? new KanbanCardLink(link.Id, link.Kind, link.TargetId, ticket.Subject, ticket.Status, false, ticket.ContactId, names.GetValueOrDefault(ticket.ContactId))
                    : new KanbanCardLink(link.Id, link.Kind, link.TargetId, "Deleted ticket", null, true, null),
                _ => invoices.TryGetValue(link.TargetId, out var invoice)
                    ? new KanbanCardLink(link.Id, link.Kind, link.TargetId, invoice.Title, invoice.Status, false, invoice.ContactId, names.GetValueOrDefault(invoice.ContactId))
                    : new KanbanCardLink(link.Id, link.Kind, link.TargetId, "Deleted invoice", null, true, null)
            })
            .OrderBy(link => KanbanLinkKinds.All.ToList().IndexOf(link.Kind)).ThenBy(link => link.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<Guid> AddCardFromTemplateAsync(Guid databaseId, Guid templateId, string? optionId, string performedBy, CancellationToken cancellationToken = default)
    {
        var name = await db.WikiDatabaseRowTemplates.AsNoTracking()
            .Where(template => template.Id == templateId && template.WikiDatabaseId == databaseId)
            .Select(template => template.Name)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("That card template no longer exists.");
        var row = await databases.CreateRowFromTemplateAsync(databaseId, templateId, null, performedBy, name, cancellationToken);
        var status = await StatusPropertyAsync(databaseId, cancellationToken);
        var target = optionId ?? WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), status.Id);
        var top = await db.WikiDatabaseRows.AsNoTracking()
            .Where(item => item.WikiDatabaseId == databaseId && item.Id != row.Id && item.ParentRowId == null && item.TrashedAt == null)
            .OrderBy(item => item.SortOrder)
            .Select(item => new { item.Id, item.PropertyValuesJson })
            .ToListAsync(cancellationToken);
        var first = top.FirstOrDefault(item => (WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(item.PropertyValuesJson), status.Id) ?? string.Empty) == (target ?? string.Empty));
        await MoveCardAsync(databaseId, row.Id, target, first?.Id, performedBy, cancellationToken);
        return row.Id;
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
    private static IReadOnlyList<WikiDatabasePropertyOption> ColumnOrder(WikiDatabaseProperty property) =>
        ColumnOrderOf(property, WikiDatabasePropertyConfig.GetOptions(property));

    private static IReadOnlyList<WikiDatabasePropertyOption> ColumnOrderOf(WikiDatabaseProperty property, IReadOnlyList<WikiDatabasePropertyOption> options)
    {
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
        KanbanFields fields, IReadOnlyDictionary<Guid, int> subItems, bool withSnippet = false)
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

        KanbanLabel? sprint = null;
        string? sprintId = null;
        if (Property(fields.SprintPropertyId) is { } sprintProperty && WikiPropertyValues.GetText(values, sprintProperty.Id) is { } chosenSprint)
        {
            sprintId = chosenSprint;
            sprint = WikiDatabasePropertyConfig.GetOptions(sprintProperty).FirstOrDefault(option => option.Id == chosenSprint) is { } sprintOption
                ? new KanbanLabel(sprintOption.Label, SafeColor(sprintOption.Color)) : null;
        }

        var blocks = WikiBlockJson.ParseBlocks(row.BlocksJson);
        var checklist = blocks.Where(block => block.Type == WikiBlockTypes.ToDo && block.PlainText.Trim().Length > 0).ToList();
        // K7: no cover of its own, so the first picture in the card's page.
        var cover = !string.IsNullOrWhiteSpace(row.CoverImageUrl) ? row.CoverImageUrl
            : blocks.Where(block => block.Type == WikiBlockTypes.Image).Select(block => block.Props.GetValueOrDefault("url")).FirstOrDefault(IsPictureUrl);
        var cardProperties = properties
            .Where(property => property.Type is not (WikiDatabasePropertyTypes.Title or WikiDatabasePropertyTypes.Status or WikiDatabasePropertyTypes.Button)
                && property.Id != fields.ArchivedPropertyId && property.Id != fields.BlockedPropertyId && property.Id != fields.BlockedReasonPropertyId)
            .Select(property => new KanbanCardProperty(property.Id, property.Name, property.Type,
                WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy)))
            .Where(property => !string.IsNullOrWhiteSpace(property.Value)).ToList();
        return new KanbanCard(
            row.Id,
            key,
            titleProperty is null ? "Untitled" : (WikiPropertyValues.GetText(values, titleProperty.Id) is { Length: > 0 } title ? title : "Untitled"),
            row.Icon,
            cover,
            labels,
            priority,
            fields.DuePropertyId is { } due ? WikiPropertyValues.GetDate(values, due) : null,
            fields.AssigneePropertyId is { } assignee ? WikiPropertyValues.GetMultiSelect(values, assignee) : [],
            fields.EstimatePropertyId is { } estimate ? WikiPropertyValues.GetNumber(values, estimate) : null,
            checklist.Count(block => block.Props.GetValueOrDefault("checked") == "true"),
            checklist.Count,
            subItems.GetValueOrDefault(row.Id),
            row.CreatedAt,
            row.UpdatedAt ?? row.CreatedAt,
            fields.PriorityPropertyId is { } priorityField ? WikiPropertyValues.GetText(values, priorityField) : null,
            sprint,
            sprintId,
            fields.BlockedPropertyId is { } blockedId && WikiPropertyValues.GetCheckbox(values, blockedId),
            fields.BlockedReasonPropertyId is { } reasonId ? WikiPropertyValues.GetText(values, reasonId) : null,
            Snippet: withSnippet ? SnippetOf(blocks) : null,
            Properties: cardProperties);
    }

    private static bool IsPictureUrl(string? url) =>
        url is { Length: > 0 }
        && ((url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
            || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"));

    // The first words of the card's page (its first text block), up to 160 characters.
    private static string? SnippetOf(IReadOnlyList<WikiBlock> blocks)
    {
        var text = blocks
            .Where(block => block.Type is WikiBlockTypes.Paragraph or WikiBlockTypes.Quote or WikiBlockTypes.Callout
                or WikiBlockTypes.BulletedListItem or WikiBlockTypes.NumberedListItem)
            .Select(block => block.PlainText.Trim())
            .FirstOrDefault(plain => plain.Length > 0);
        if (text is null) return null;
        return text.Length <= 160 ? text : text[..157].TrimEnd() + "…";
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

// Board housekeeping every hour, starting two minutes after start-up: archives each board's cards
// that have sat in a Complete column past its ArchiveAfterDays (K9a).
public sealed class KanbanHousekeepingBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<KanbanHousekeepingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var archived = await scope.ServiceProvider.GetRequiredService<IKanbanBoardService>().ArchiveDueCardsAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (archived > 0) logger.LogInformation("Archived {Count} finished board card(s).", archived);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Board housekeeping failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
