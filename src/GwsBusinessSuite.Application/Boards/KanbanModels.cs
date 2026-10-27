using System.Text.Json.Nodes;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Boards;

// Boards (.claude/plans/kanban-boards-2026-10.md): a board is a Sentinel database whose Status (or
// Select) property supplies the columns; each card is a row, so each card is also a page. These are
// the shapes the Boards pages show.

public sealed record KanbanBoardSummary(Guid DatabaseId, string Title, string? Icon, int CardCount, int OpenCount, DateTimeOffset LastActivity);

public sealed record KanbanLabel(string Label, string Color);

public sealed record KanbanCard(
    Guid RowId,
    string? Key,
    string Title,
    string? Icon,
    string? CoverImageUrl,
    IReadOnlyList<KanbanLabel> Labels,
    KanbanLabel? Priority,
    DateTimeOffset? Due,
    IReadOnlyList<string> Assignees,
    decimal? Estimate,
    int ChecklistDone,
    int ChecklistTotal,
    int SubItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PriorityOptionId = null,
    KanbanLabel? Sprint = null,
    string? SprintOptionId = null,
    bool Blocked = false,
    string? BlockedReason = null,
    // When it moved into the column it's in (the status-change log; its creation before that log).
    DateTimeOffset? EnteredColumnAt = null);

// OptionId null is the "No status" column (cards with no value yet), shown only when it has cards.
public sealed record KanbanColumn(string? OptionId, string Label, string Color, string? Group, IReadOnlyList<KanbanCard> Cards, int? WipLimit = null);

// Which of the database's properties play which part on the board. Only Status is required.
public sealed record KanbanFields(Guid StatusPropertyId, Guid? PriorityPropertyId, Guid? LabelsPropertyId, Guid? DuePropertyId,
    Guid? AssigneePropertyId, Guid? EstimatePropertyId, Guid? KeyPropertyId,
    Guid? SprintPropertyId = null, Guid? BlockedPropertyId = null, Guid? BlockedReasonPropertyId = null);

public sealed record KanbanOption(string Id, string Label, string Color);

// PriorityOptions / LabelOptions: the choices for those fields, in order (empty when the board has none).
public sealed record KanbanBoard(Guid DatabaseId, string Title, string? Icon, KanbanFields Fields, IReadOnlyList<KanbanColumn> Columns,
    IReadOnlyList<KanbanOption> PriorityOptions, IReadOnlyList<KanbanOption> LabelOptions,
    KanbanSettings? Settings = null, IReadOnlyList<KanbanOption>? SprintOptions = null);

// A board's own settings, kept in KanbanBoardSettings.SettingsJson (K3; K7 adds the look).
public sealed record KanbanSettings
{
    // The most cards a column should hold, by Status option id. A column without one has no limit.
    public Dictionary<string, int> WipLimits { get; init; } = [];
    // Swimlanes: one of KanbanSwimlanes, or null for none.
    public string? SwimlaneBy { get; init; }
    // Days a card can sit in an in-progress column before it starts to show its age.
    public int AgingDays { get; init; } = 5;
    public string? Background { get; init; }
    public string? Accent { get; init; }
    public string CardSize { get; init; } = KanbanCardSizes.Comfortable;
    // The board's sprints (K4), one per option of its Sprint property.
    public List<KanbanSprint> Sprints { get; init; } = [];

    public static KanbanSettings Default { get; } = new();
}

public static class KanbanSprintStates
{
    public const string Planned = "planned";
    public const string Active = "active";
    public const string Done = "done";
}

// What a board keeps about a sprint beyond its name (the Sprint property's option). The Done* figures
// are fixed when it completes.
public sealed record KanbanSprint
{
    public string OptionId { get; init; } = string.Empty;
    public string Goal { get; init; } = string.Empty;
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public string State { get; init; } = KanbanSprintStates.Planned;
    public decimal? Capacity { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? DoneCards { get; init; }
    public decimal? DonePoints { get; init; }
}

// A sprint as the board shows it. Points are estimates, or one per card on a board without them.
public sealed record KanbanSprintView(
    string OptionId, string Name, string Color, string Goal, DateOnly? Start, DateOnly? End, string State, decimal? Capacity,
    int CardCount, int DoneCount, decimal Points, decimal DonePoints, bool UsesEstimates,
    IReadOnlyList<KanbanBurnPoint> Burndown, DateTimeOffset? CompletedAt);

// Points left at the end of a sprint day.
public sealed record KanbanBurnPoint(DateOnly Day, decimal Remaining);

// Flow metrics for a board over the last Days days (K5), from the status-change log.
// HistoryStarts: the board's first logged move; before it, flow is approximate.
public sealed record KanbanInsights(
    int Days,
    DateOnly From,
    DateOnly? HistoryStarts,
    IReadOnlyList<KanbanCycle> Cycles,
    double? MedianCycleDays,
    double? P85CycleDays,
    IReadOnlyList<KanbanWeek> Throughput,
    IReadOnlyList<KanbanFlowColumn> FlowColumns,
    IReadOnlyList<KanbanFlowDay> Flow,
    IReadOnlyList<KanbanAging> Aging,
    int WorkInProgress);

// A card finished in the window and how long it took: from first entering an in-progress column
// (or its creation, if it skipped them) to last reaching a complete one.
public sealed record KanbanCycle(Guid RowId, string? Key, string Title, DateOnly Finished, double Days);
public sealed record KanbanWeek(DateOnly Start, int Finished);
// Cumulative flow's bands, bottom first (complete columns at the bottom, as the method draws it).
public sealed record KanbanFlowColumn(string OptionId, string Label, string Color, string? Group);
// Cards in each FlowColumns band at the end of a day, in the same order.
public sealed record KanbanFlowDay(DateOnly Day, IReadOnlyList<int> Counts);
public sealed record KanbanAging(Guid RowId, string? Key, string Title, string Column, string Color, double Days);

public sealed record KanbanSprintSummary(string Name, int DoneCount, decimal DonePoints, int MovedCount, string? MovedTo);

public static class KanbanSwimlanes
{
    public const string Priority = "priority";
    public const string Assignee = "assignee";
    public const string Sprint = "sprint";
    public static readonly IReadOnlyList<string> All = [Priority, Assignee, Sprint];
}

public static class KanbanCardSizes
{
    public const string Compact = "compact";
    public const string Comfortable = "comfortable";
    public const string Rich = "rich";
    public static readonly IReadOnlyList<string> All = [Compact, Comfortable, Rich];
}

// A card's sub-item (a row nested under it), done when its status is in the Complete group.
public sealed record KanbanSubItem(Guid RowId, string Title, bool Done);

public sealed record KanbanColumnSeed(string Label, string Color, string Group);

public sealed record KanbanTemplate(string Key, string Name, string Description, string Icon, IReadOnlyList<KanbanColumnSeed> Columns, bool Sprints = false);

public static class KanbanTemplates
{
    // Column colours are the status palette's fills (StatusTones), so a column reads like the
    // pills everywhere else.
    private const string Grey = "#78716c";
    private const string Blue = "#2563eb";
    private const string Amber = "#d97706";
    private const string Violet = "#7c3aed";
    private const string Green = "#15803d";

    public static readonly IReadOnlyList<KanbanTemplate> All =
    [
        new("personal", "Personal Kanban", "Your own work: a backlog to pull from, a short Doing list, and Done.", "🗂️",
        [
            new("Backlog", Grey, WikiDatabaseStatusGroups.ToDo), new("To do", Blue, WikiDatabaseStatusGroups.ToDo),
            new("Doing", Amber, WikiDatabaseStatusGroups.InProgress), new("Review", Violet, WikiDatabaseStatusGroups.InProgress),
            new("Done", Green, WikiDatabaseStatusGroups.Complete)
        ]),
        new("sprint", "Sprint board", "Timeboxed work with estimates: plan a sprint, run it, roll over what's left.", "🏃",
        [
            new("Backlog", Grey, WikiDatabaseStatusGroups.ToDo), new("Ready", Blue, WikiDatabaseStatusGroups.ToDo),
            new("In progress", Amber, WikiDatabaseStatusGroups.InProgress), new("In review", Violet, WikiDatabaseStatusGroups.InProgress),
            new("Done", Green, WikiDatabaseStatusGroups.Complete)
        ], Sprints: true),
        new("bugs", "Bug triage", "Reports in, confirmed, fixed and verified.", "🐞",
        [
            new("Triage", Grey, WikiDatabaseStatusGroups.ToDo), new("Confirmed", Blue, WikiDatabaseStatusGroups.ToDo),
            new("Fixing", Amber, WikiDatabaseStatusGroups.InProgress), new("Verifying", Violet, WikiDatabaseStatusGroups.InProgress),
            new("Closed", Green, WikiDatabaseStatusGroups.Complete)
        ]),
        new("content", "Content pipeline", "Ideas through drafts and edits to published.", "✍️",
        [
            new("Idea", Grey, WikiDatabaseStatusGroups.ToDo), new("Drafting", Amber, WikiDatabaseStatusGroups.InProgress),
            new("Editing", Violet, WikiDatabaseStatusGroups.InProgress), new("Scheduled", Blue, WikiDatabaseStatusGroups.InProgress),
            new("Published", Green, WikiDatabaseStatusGroups.Complete)
        ])
    ];

    public static KanbanTemplate? Find(string? key) => All.FirstOrDefault(template => template.Key == key);
}

public interface IKanbanBoardService
{
    // Sentinel databases that work as boards: those with a Status property, or a Select property
    // that a Board view groups by.
    Task<IReadOnlyList<KanbanBoardSummary>> ListBoardsAsync(CancellationToken cancellationToken = default);

    // A new Sentinel database laid out for the template; returns its id.
    Task<Guid> CreateBoardAsync(string title, string templateKey, string performedBy, CancellationToken cancellationToken = default);

    // Null when the database is gone or has nothing to make columns from.
    Task<KanbanBoard?> GetBoardAsync(Guid databaseId, CancellationToken cancellationToken = default);

    // Puts the card in a column (null = No status) just before another card, or last when
    // beforeRowId is null. "Before a card" rather than an index, since the board shows only
    // top-level cards (and a filter hides more) while Sentinel orders every row in the column.
    Task MoveCardAsync(Guid databaseId, Guid rowId, string? optionId, Guid? beforeRowId, string performedBy, CancellationToken cancellationToken = default);

    // A new card at the top of a column; returns its row id.
    Task<Guid> AddCardAsync(Guid databaseId, string? optionId, string title, string performedBy, CancellationToken cancellationToken = default);

    // One property on one or more cards, each to what change makes of its current value (a
    // Select/Status option id, a JSON array of option ids or usernames, a number, an ISO date,
    // text, or null to clear). Returns each card's value from before, for Undo.
    Task<IReadOnlyDictionary<Guid, JsonNode?>> SetCardsValueAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, Guid propertyId,
        Func<JsonNode?, JsonNode?> change, string performedBy, CancellationToken cancellationToken = default);

    // Undo for SetCardsValueAsync: each card's value put back.
    Task RestoreCardValuesAsync(Guid databaseId, Guid propertyId, IReadOnlyDictionary<Guid, JsonNode?> values, string performedBy, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KanbanSubItem>> ListSubItemsAsync(Guid databaseId, Guid parentRowId, CancellationToken cancellationToken = default);
    // A sub-item starts in the board's first column; done moves it to the first Complete one.
    Task<Guid> AddSubItemAsync(Guid databaseId, Guid parentRowId, string title, string performedBy, CancellationToken cancellationToken = default);
    Task SetSubItemDoneAsync(Guid databaseId, Guid rowId, bool done, string performedBy, CancellationToken cancellationToken = default);

    // Marks every open sub-item of a card done; returns how many changed. The board rule "complete
    // its sub-items" runs this through the database.completeSubItems node.
    Task<int> CompleteSubItemsAsync(Guid databaseId, Guid parentRowId, string performedBy, CancellationToken cancellationToken = default);

    // A card from a card template (K6), named after it, at the top of a column (null: the column
    // the template was saved from). Returns the new card's row id.
    Task<Guid> AddCardFromTemplateAsync(Guid databaseId, Guid templateId, string? optionId, string performedBy, CancellationToken cancellationToken = default);

    Task<KanbanSettings> GetSettingsAsync(Guid databaseId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(Guid databaseId, KanbanSettings settings, string performedBy, CancellationToken cancellationToken = default);

    // Flags or clears a card as blocked, with an optional reason. A board without Blocked / Blocked
    // reason properties gets them the first time a card is blocked.
    Task SetBlockedAsync(Guid databaseId, Guid rowId, bool blocked, string? reason, string performedBy, CancellationToken cancellationToken = default);

    // Sprints (K4), oldest first.
    Task<IReadOnlyList<KanbanSprintView>> ListSprintsAsync(Guid databaseId, CancellationToken cancellationToken = default);
    // A planned sprint, as a new option of the Sprint property (added to a board without one); its option id.
    Task<string> CreateSprintAsync(Guid databaseId, string name, string performedBy, CancellationToken cancellationToken = default);
    Task UpdateSprintAsync(Guid databaseId, string optionId, string goal, decimal? capacity, string performedBy, CancellationToken cancellationToken = default);
    // Only one sprint runs at a time.
    Task StartSprintAsync(Guid databaseId, string optionId, DateOnly start, DateOnly end, string performedBy, CancellationToken cancellationToken = default);
    // Marks it done with what got finished, and moves the unfinished cards to another sprint
    // (moveTo an option id), the backlog (""), or a new sprint (null).
    Task<KanbanSprintSummary> CompleteSprintAsync(Guid databaseId, string optionId, string? moveTo, string performedBy, CancellationToken cancellationToken = default);

    // Flow metrics (K5) over the last 30, 90 or 180 days; null for a board that isn't one.
    Task<KanbanInsights?> GetInsightsAsync(Guid databaseId, int days, CancellationToken cancellationToken = default);

    // To Sentinel's trash, and back (Undo).
    Task TrashCardsAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, string performedBy, CancellationToken cancellationToken = default);
    Task RestoreCardsAsync(Guid databaseId, IReadOnlyCollection<Guid> rowIds, string performedBy, CancellationToken cancellationToken = default);
}
