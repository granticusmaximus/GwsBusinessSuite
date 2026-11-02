namespace GwsBusinessSuite.Application.Boards;

public sealed class KanbanBoardAccess
{
    public Guid BoardId { get; init; }
    public Guid UserId { get; init; }
    public bool CanView { get; init; }
    public bool CanEdit { get; init; }
}
