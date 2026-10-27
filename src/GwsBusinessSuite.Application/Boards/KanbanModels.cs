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
    DateTimeOffset UpdatedAt);

// OptionId null is the "No status" column (cards with no value yet), shown only when it has cards.
public sealed record KanbanColumn(string? OptionId, string Label, string Color, string? Group, IReadOnlyList<KanbanCard> Cards);

// Which of the database's properties play which part on the board. Only Status is required.
public sealed record KanbanFields(Guid StatusPropertyId, Guid? PriorityPropertyId, Guid? LabelsPropertyId, Guid? DuePropertyId,
    Guid? AssigneePropertyId, Guid? EstimatePropertyId, Guid? KeyPropertyId);

public sealed record KanbanBoard(Guid DatabaseId, string Title, string? Icon, KanbanFields Fields, IReadOnlyList<KanbanColumn> Columns);

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
}
