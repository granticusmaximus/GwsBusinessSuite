using GwsBusinessSuite.Application.AdminPortal;

namespace GwsBusinessSuite.Application.Boards;

// Who else has a board open (Boards K7), and which card each has open, for the rings on the
// header's avatars and the pulse on a card someone is in. One session per open board page; it
// checks in every 30 seconds and is forgotten 90 seconds after its last check-in (a closed laptop).
public sealed record KanbanViewer(Guid SessionId, string Username, Guid? OpenCardId);

// Since 05-11-26 a board-shaped view of the app's one RecordPresenceTracker - the board is the
// record, its open card the focus. It used to be a second copy of the same tracker.
public sealed class KanbanPresence
{
    public const string RecordKind = "board";

    private readonly RecordPresenceTracker _tracker;

    public KanbanPresence(RecordPresenceTracker tracker)
    {
        _tracker = tracker;
        // Both are app-lifetime singletons, so this subscription lives as long as they do.
        _tracker.Changed += record =>
        {
            if (record.Kind == RecordKind) Notify(record.Id);
        };
    }

    // The board whose viewers changed.
    public event Action<Guid>? Changed;

    // Joining a board, or saying which card is open now (null: none).
    public void Enter(Guid sessionId, Guid boardId, string username, Guid? openCardId) =>
        _tracker.Enter(sessionId, Key(boardId), username, openCardId);

    public void Touch(Guid sessionId) => _tracker.Touch(sessionId);

    public void Leave(Guid sessionId) => _tracker.Leave(sessionId);

    // Everyone else on the board (other people, and the same person in another window), by name.
    public IReadOnlyList<KanbanViewer> Others(Guid boardId, Guid exceptSessionId) =>
        _tracker.Others(Key(boardId), exceptSessionId)
            .Select(viewer => new KanbanViewer(viewer.SessionId, viewer.Username, viewer.FocusId))
            .ToList();

    private static RecordPresenceKey Key(Guid boardId) => new(RecordKind, boardId);

    // One board page failing to redraw mustn't stop the others hearing about it.
    private void Notify(Guid boardId)
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Guid>>())
        {
            try { handler(boardId); }
            catch (Exception) { }
        }
    }
}
