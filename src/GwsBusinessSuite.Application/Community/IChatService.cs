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

    // Group conversations: the creator plus the chosen members (duplicates/unknown accounts ignored).
    Task<Guid> CreateGroupThreadAsync(string creatorUsername, string title, IEnumerable<string> memberUsernames, CancellationToken cancellationToken = default);
    Task AddParticipantsAsync(Guid threadId, string performedBy, IEnumerable<string> usernames, CancellationToken cancellationToken = default);
    Task LeaveThreadAsync(Guid threadId, string username, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatParticipantView>> GetParticipantsAsync(Guid threadId, string requestingUsername, CancellationToken cancellationToken = default);

    // A message with files (body may be empty when there's at least one file).
    Task<ChatMessageView> SendMessageAsync(Guid threadId, string senderUsername, string body,
        IReadOnlyList<ChatAttachmentUpload> attachments, CancellationToken cancellationToken = default);

    // Throws UnauthorizedAccessException unless the requester is in the attachment's thread.
    Task<ChatAttachmentFile?> GetAttachmentAsync(Guid attachmentId, string requestingUsername, CancellationToken cancellationToken = default);

    Task<int> CountUnreadAsync(string username, CancellationToken cancellationToken = default);
}
