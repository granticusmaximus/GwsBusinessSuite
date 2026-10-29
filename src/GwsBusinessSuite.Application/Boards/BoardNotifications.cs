namespace GwsBusinessSuite.Application.Boards;

// One person's board notification (K9b) for the bell: a card assigned to them, due tomorrow, or overdue.
public sealed record BoardNotificationView(Guid Id, Guid DatabaseId, Guid RowId, string Kind, string Title, string Message,
    DateTimeOffset CreatedAt, bool IsRead)
{
    public string Url => $"/admin/boards/{DatabaseId}?card={RowId}";
}

public interface IBoardNotificationService
{
    // Looks over every board: new assignments, cards due tomorrow, cards overdue. Returns how
    // many notifications it made.
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BoardNotificationView>> ListAsync(string username, CancellationToken cancellationToken = default);
    Task MarkReadAsync(string username, Guid id, CancellationToken cancellationToken = default);
    Task MarkAllReadAsync(string username, CancellationToken cancellationToken = default);

    // After 07:00, one email to each person who asked for it, with the notifications made since
    // the last one. Returns how many emails went.
    Task<int> SendDigestsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

// Raised for a person when the sweep makes them a notification, so an open bell shows it at once.
public sealed class BoardNotificationNotifier
{
    public event Action<string>? OnNotified;

    public void Notify(string username) => OnNotified?.Invoke(username);
}
