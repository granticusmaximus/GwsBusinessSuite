using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Community;

public sealed class ChatService(IAppDbContext dbContext) : IChatService
{
    public async Task<IReadOnlyList<ChatThreadSummary>> ListThreadsForUserAsync(string username, CancellationToken cancellationToken = default)
    {
        var myParticipation = await dbContext.ChatThreadParticipants.AsNoTracking()
            .Where(p => p.Username.ToLower() == username.ToLower())
            .ToListAsync(cancellationToken);
        if (myParticipation.Count == 0) return [];

        var threadIds = myParticipation.Select(p => p.ThreadId).ToList();
        var threads = await dbContext.ChatThreads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToListAsync(cancellationToken);
        var allParticipants = await dbContext.ChatThreadParticipants.AsNoTracking()
            .Where(p => threadIds.Contains(p.ThreadId))
            .ToListAsync(cancellationToken);
        var allMessages = await dbContext.ChatMessages.AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId))
            .ToListAsync(cancellationToken);

        var otherUsernames = allParticipants
            .Where(p => !string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Username)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayNames = await ResolveDisplayNamesAsync(otherUsernames, cancellationToken);

        var summaries = new List<ChatThreadSummary>();
        foreach (var thread in threads)
        {
            var myLastReadAt = myParticipation.First(p => p.ThreadId == thread.Id).LastReadAt;
            var participantsInThread = allParticipants.Where(p => p.ThreadId == thread.Id).ToList();
            var otherParticipant = participantsInThread.FirstOrDefault(p => !string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
            var messagesInThread = allMessages.Where(m => m.ThreadId == thread.Id).OrderByDescending(m => m.CreatedAt).ToList();
            var lastMessage = messagesInThread.FirstOrDefault();
            var unreadCount = myLastReadAt is { } readAt
                ? messagesInThread.Count(m => m.CreatedAt > readAt && !string.Equals(m.CreatedBy, username, StringComparison.OrdinalIgnoreCase))
                : messagesInThread.Count(m => !string.Equals(m.CreatedBy, username, StringComparison.OrdinalIgnoreCase));

            var title = thread.IsGroup
                ? (thread.Title ?? "Group chat")
                : (otherParticipant is null ? "Unknown" : displayNames.GetValueOrDefault(otherParticipant.Username, otherParticipant.Username));

            summaries.Add(new ChatThreadSummary(
                thread.Id,
                thread.IsGroup,
                title,
                otherParticipant?.Username,
                lastMessage?.Body,
                lastMessage?.CreatedAt,
                unreadCount));
        }

        return summaries.OrderByDescending(s => s.LastMessageAt ?? DateTimeOffset.MinValue).ToList();
    }

    public async Task<Guid> GetOrCreateDirectThreadAsync(string usernameA, string usernameB, CancellationToken cancellationToken = default)
    {
        if (string.Equals(usernameA, usernameB, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cannot start a direct-message thread with yourself.");
        }

        var aThreadIds = await dbContext.ChatThreadParticipants.AsNoTracking()
            .Where(p => p.Username.ToLower() == usernameA.ToLower())
            .Select(p => p.ThreadId)
            .ToListAsync(cancellationToken);
        var bThreadIds = await dbContext.ChatThreadParticipants.AsNoTracking()
            .Where(p => p.Username.ToLower() == usernameB.ToLower())
            .Select(p => p.ThreadId)
            .ToListAsync(cancellationToken);
        var sharedThreadIds = aThreadIds.Intersect(bThreadIds).ToList();

        if (sharedThreadIds.Count > 0)
        {
            var candidateThreads = await dbContext.ChatThreads.AsNoTracking()
                .Where(t => sharedThreadIds.Contains(t.Id) && !t.IsGroup)
                .ToListAsync(cancellationToken);
            foreach (var candidate in candidateThreads)
            {
                var participantCount = await dbContext.ChatThreadParticipants.AsNoTracking()
                    .CountAsync(p => p.ThreadId == candidate.Id, cancellationToken);
                if (participantCount == 2) return candidate.Id;
            }
        }

        var thread = new ChatThread { IsGroup = false, CreatedBy = usernameA };
        dbContext.ChatThreads.Add(thread);
        dbContext.ChatThreadParticipants.Add(new ChatThreadParticipant { ThreadId = thread.Id, Username = usernameA, CreatedBy = usernameA });
        dbContext.ChatThreadParticipants.Add(new ChatThreadParticipant { ThreadId = thread.Id, Username = usernameB, CreatedBy = usernameA });
        await dbContext.SaveChangesAsync(cancellationToken);
        return thread.Id;
    }

    public async Task<IReadOnlyList<ChatMessageView>> GetMessagesAsync(Guid threadId, string requestingUsername, CancellationToken cancellationToken = default)
    {
        await EnsureParticipantAsync(threadId, requestingUsername, cancellationToken);

        // Materialize then sort client-side - EF Core/SQLite can't translate an ORDER BY over a
        // DateTimeOffset column (see ActivityFeedService.GetRecentAsync's own comment).
        var messages = (await dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.ThreadId == threadId)
            .ToListAsync(cancellationToken))
            .OrderBy(m => m.CreatedAt)
            .ToList();
        if (messages.Count == 0) return [];

        var senderUsernames = messages.Select(m => m.CreatedBy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var displayNames = await ResolveDisplayNamesAsync(senderUsernames, cancellationToken);

        return messages.Select(m => new ChatMessageView(
            m.Id, m.CreatedBy, displayNames.GetValueOrDefault(m.CreatedBy, m.CreatedBy), m.Body, m.CreatedAt
        )).ToList();
    }

    public async Task<ChatMessageView> SendMessageAsync(Guid threadId, string senderUsername, string body, CancellationToken cancellationToken = default)
    {
        await EnsureParticipantAsync(threadId, senderUsername, cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A message can't be empty.", nameof(body));
        }

        var message = new ChatMessage { ThreadId = threadId, Body = body.Trim(), CreatedBy = senderUsername };
        dbContext.ChatMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        var displayNames = await ResolveDisplayNamesAsync([senderUsername], cancellationToken);
        return new ChatMessageView(message.Id, senderUsername, displayNames.GetValueOrDefault(senderUsername, senderUsername), message.Body, message.CreatedAt);
    }

    public async Task MarkThreadReadAsync(Guid threadId, string username, CancellationToken cancellationToken = default)
    {
        var participant = await dbContext.ChatThreadParticipants
            .FirstOrDefaultAsync(p => p.ThreadId == threadId && p.Username.ToLower() == username.ToLower(), cancellationToken);
        if (participant is null) return;

        participant.LastReadAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureParticipantAsync(Guid threadId, string username, CancellationToken cancellationToken)
    {
        var isParticipant = await dbContext.ChatThreadParticipants.AsNoTracking()
            .AnyAsync(p => p.ThreadId == threadId && p.Username.ToLower() == username.ToLower(), cancellationToken);
        if (!isParticipant)
        {
            throw new UnauthorizedAccessException("You are not a participant of this conversation.");
        }
    }

    private async Task<Dictionary<string, string>> ResolveDisplayNamesAsync(IReadOnlyList<string> usernames, CancellationToken cancellationToken)
    {
        if (usernames.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lowerUsernames = usernames.Select(u => u.ToLowerInvariant()).ToList();
        var users = await dbContext.AppUsers.AsNoTracking()
            .Where(u => lowerUsernames.Contains(u.Username.ToLower()))
            .ToListAsync(cancellationToken);
        var profiles = await dbContext.MemberProfiles.AsNoTracking()
            .Where(p => users.Select(u => u.Id).Contains(p.AppUserId))
            .ToListAsync(cancellationToken);
        var profileByUserId = profiles.ToDictionary(p => p.AppUserId);

        return users.ToDictionary(
            u => u.Username,
            u => profileByUserId.TryGetValue(u.Id, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : u.Username,
            StringComparer.OrdinalIgnoreCase);
    }
}
