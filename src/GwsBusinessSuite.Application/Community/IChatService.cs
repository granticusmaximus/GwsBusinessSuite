namespace GwsBusinessSuite.Application.Community;

// Workstream E, Phase 4 (direct messaging). Real-time delivery is a client-side poll (the calling
// Razor page re-fetches GetMessagesAsync every few seconds while a thread is open) rather than a
// push-based SignalR fan-out - a simple, robust pattern that doesn't require plumbing
// cross-circuit notifications between two different users' Blazor Server circuits, at the cost of
// a few seconds' delivery latency instead of instant push. Revisit only if that latency proves to
// be a real problem in practice.
public interface IChatService
{
    Task<IReadOnlyList<ChatThreadSummary>> ListThreadsForUserAsync(string username, CancellationToken cancellationToken = default);

    // Finds the existing 1:1 thread between exactly these two usernames, or creates one - never
    // creates a duplicate for the same pair.
    Task<Guid> GetOrCreateDirectThreadAsync(string usernameA, string usernameB, CancellationToken cancellationToken = default);

    // Throws UnauthorizedAccessException if requestingUsername isn't a participant of this thread.
    Task<IReadOnlyList<ChatMessageView>> GetMessagesAsync(Guid threadId, string requestingUsername, CancellationToken cancellationToken = default);

    // Throws UnauthorizedAccessException if senderUsername isn't a participant of this thread.
    Task<ChatMessageView> SendMessageAsync(Guid threadId, string senderUsername, string body, CancellationToken cancellationToken = default);

    Task MarkThreadReadAsync(Guid threadId, string username, CancellationToken cancellationToken = default);
}
