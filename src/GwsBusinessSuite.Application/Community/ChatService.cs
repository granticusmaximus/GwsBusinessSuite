using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Community;

public sealed class ChatService(IAppDbContext dbContext, IChatNotifier? notifier = null) : IChatService
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

        var allMessageIds = allMessages.Select(m => m.Id).ToList();
        var attachmentMessageIds = (await dbContext.ChatMessageAttachments.AsNoTracking()
            .Where(a => allMessageIds.Contains(a.MessageId))
            .Select(a => a.MessageId)
            .ToListAsync(cancellationToken)).ToHashSet();

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

            var others = participantsInThread
                .Where(p => !string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase))
                .Select(p => displayNames.GetValueOrDefault(p.Username, p.Username))
                .OrderBy(name => name)
                .ToList();
            var preview = lastMessage is null
                ? null
                : string.IsNullOrWhiteSpace(lastMessage.Body) && attachmentMessageIds.Contains(lastMessage.Id) ? "📎 Attachment" : lastMessage.Body;

            summaries.Add(new ChatThreadSummary(
                thread.Id,
                thread.IsGroup,
                title,
                otherParticipant?.Username,
                preview,
                lastMessage?.CreatedAt,
                unreadCount,
                participantsInThread.Count,
                others));
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
        var messageIds = messages.Select(m => m.Id).ToList();
        // Metadata only - file bytes are fetched one at a time by GetAttachmentAsync.
        var attachments = (await dbContext.ChatMessageAttachments.AsNoTracking()
                .Where(a => messageIds.Contains(a.MessageId))
                .Select(a => new { a.Id, a.MessageId, a.FileName, a.ContentType, a.SizeBytes })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.MessageId, a => new ChatAttachmentView(a.Id, a.FileName, a.ContentType, a.SizeBytes));

        return messages.Select(m => new ChatMessageView(
            m.Id, m.CreatedBy, displayNames.GetValueOrDefault(m.CreatedBy, m.CreatedBy), m.Body, m.CreatedAt,
            attachments[m.Id].ToList()
        )).ToList();
    }

    public Task<ChatMessageView> SendMessageAsync(Guid threadId, string senderUsername, string body, CancellationToken cancellationToken = default) =>
        SendMessageAsync(threadId, senderUsername, body, [], cancellationToken);

    public async Task<ChatMessageView> SendMessageAsync(Guid threadId, string senderUsername, string body,
        IReadOnlyList<ChatAttachmentUpload> attachments, CancellationToken cancellationToken = default)
    {
        await EnsureParticipantAsync(threadId, senderUsername, cancellationToken);
        attachments ??= [];
        if (string.IsNullOrWhiteSpace(body) && attachments.Count == 0)
        {
            throw new ArgumentException("A message can't be empty.", nameof(body));
        }
        if (attachments.Count > ChatLimits.MaxAttachmentsPerMessage)
        {
            throw new ArgumentException($"Attach at most {ChatLimits.MaxAttachmentsPerMessage} files per message.", nameof(attachments));
        }
        if (attachments.FirstOrDefault(a => a.Content.LongLength > ChatLimits.MaxAttachmentBytes) is { } tooBig)
        {
            throw new ArgumentException($"'{tooBig.FileName}' is larger than {ChatLimits.MaxAttachmentBytes / 1024 / 1024} MB.", nameof(attachments));
        }

        var message = new ChatMessage { ThreadId = threadId, Body = (body ?? string.Empty).Trim(), CreatedBy = senderUsername };
        dbContext.ChatMessages.Add(message);
        var stored = new List<ChatMessageAttachment>();
        foreach (var upload in attachments)
        {
            var attachment = new ChatMessageAttachment
            {
                MessageId = message.Id,
                FileName = SafeFileName(upload.FileName),
                ContentType = string.IsNullOrWhiteSpace(upload.ContentType) ? "application/octet-stream" : upload.ContentType,
                SizeBytes = upload.Content.LongLength,
                Content = upload.Content,
                CreatedBy = senderUsername
            };
            stored.Add(attachment);
            dbContext.ChatMessageAttachments.Add(attachment);
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var displayNames = await ResolveDisplayNamesAsync([senderUsername], cancellationToken);
        var senderName = displayNames.GetValueOrDefault(senderUsername, senderUsername);
        await PublishAsync(threadId, senderUsername, senderName, message, stored.Count, cancellationToken);
        return new ChatMessageView(message.Id, senderUsername, senderName, message.Body, message.CreatedAt,
            stored.Select(a => new ChatAttachmentView(a.Id, a.FileName, a.ContentType, a.SizeBytes)).ToList());
    }

    public async Task<Guid> CreateGroupThreadAsync(string creatorUsername, string title, IEnumerable<string> memberUsernames, CancellationToken cancellationToken = default)
    {
        var members = await ExistingUsernamesAsync(memberUsernames.Append(creatorUsername), cancellationToken);
        if (members.Count < 2) throw new ArgumentException("Pick at least one other person for the group.");
        if (members.Count > ChatLimits.MaxGroupSize) throw new ArgumentException($"A group can have at most {ChatLimits.MaxGroupSize} people.");

        var thread = new ChatThread
        {
            IsGroup = true,
            Title = string.IsNullOrWhiteSpace(title) ? "Group chat" : title.Trim(),
            CreatedBy = creatorUsername
        };
        dbContext.ChatThreads.Add(thread);
        foreach (var member in members)
        {
            dbContext.ChatThreadParticipants.Add(new ChatThreadParticipant { ThreadId = thread.Id, Username = member, CreatedBy = creatorUsername });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return thread.Id;
    }

    public async Task AddParticipantsAsync(Guid threadId, string performedBy, IEnumerable<string> usernames, CancellationToken cancellationToken = default)
    {
        await EnsureParticipantAsync(threadId, performedBy, cancellationToken);
        var thread = await dbContext.ChatThreads.FirstOrDefaultAsync(t => t.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("That conversation no longer exists.");
        if (!thread.IsGroup) throw new InvalidOperationException("Start a new group to add people to a one-on-one conversation.");

        var existing = await dbContext.ChatThreadParticipants.Where(p => p.ThreadId == threadId).Select(p => p.Username).ToListAsync(cancellationToken);
        var toAdd = (await ExistingUsernamesAsync(usernames, cancellationToken))
            .Where(u => !existing.Contains(u, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (existing.Count + toAdd.Count > ChatLimits.MaxGroupSize) throw new ArgumentException($"A group can have at most {ChatLimits.MaxGroupSize} people.");
        foreach (var username in toAdd)
        {
            // Joiners see the history but nothing counts as unread from before they joined.
            dbContext.ChatThreadParticipants.Add(new ChatThreadParticipant { ThreadId = threadId, Username = username, LastReadAt = DateTimeOffset.UtcNow, CreatedBy = performedBy });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LeaveThreadAsync(Guid threadId, string username, CancellationToken cancellationToken = default)
    {
        var thread = await dbContext.ChatThreads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, cancellationToken);
        if (thread is null) return;
        if (!thread.IsGroup) throw new InvalidOperationException("You can only leave group conversations.");
        var participant = await dbContext.ChatThreadParticipants
            .FirstOrDefaultAsync(p => p.ThreadId == threadId && p.Username.ToLower() == username.ToLower(), cancellationToken);
        if (participant is null) return;
        dbContext.ChatThreadParticipants.Remove(participant);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatParticipantView>> GetParticipantsAsync(Guid threadId, string requestingUsername, CancellationToken cancellationToken = default)
    {
        await EnsureParticipantAsync(threadId, requestingUsername, cancellationToken);
        var usernames = await dbContext.ChatThreadParticipants.AsNoTracking()
            .Where(p => p.ThreadId == threadId).Select(p => p.Username).ToListAsync(cancellationToken);
        var names = await ResolveDisplayNamesAsync(usernames, cancellationToken);
        return usernames.Select(u => new ChatParticipantView(u, names.GetValueOrDefault(u, u))).OrderBy(p => p.DisplayName).ToList();
    }

    public async Task<ChatAttachmentFile?> GetAttachmentAsync(Guid attachmentId, string requestingUsername, CancellationToken cancellationToken = default)
    {
        var attachment = await dbContext.ChatMessageAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null) return null;
        var threadId = await dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Id == attachment.MessageId).Select(m => (Guid?)m.ThreadId).FirstOrDefaultAsync(cancellationToken);
        if (threadId is null) return null;
        await EnsureParticipantAsync(threadId.Value, requestingUsername, cancellationToken);
        return new ChatAttachmentFile(attachment.FileName, attachment.ContentType, attachment.Content);
    }

    public async Task<int> CountUnreadAsync(string username, CancellationToken cancellationToken = default) =>
        (await ListThreadsForUserAsync(username, cancellationToken)).Sum(t => t.UnreadCount);

    private async Task PublishAsync(Guid threadId, string senderUsername, string senderName, ChatMessage message, int attachmentCount, CancellationToken cancellationToken)
    {
        if (notifier is null) return;
        var thread = await dbContext.ChatThreads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, cancellationToken);
        var recipients = await dbContext.ChatThreadParticipants.AsNoTracking()
            .Where(p => p.ThreadId == threadId && p.Username.ToLower() != senderUsername.ToLower())
            .Select(p => p.Username)
            .ToListAsync(cancellationToken);
        var preview = string.IsNullOrWhiteSpace(message.Body)
            ? $"📎 {attachmentCount} attachment{(attachmentCount == 1 ? "" : "s")}"
            : message.Body.Length > 140 ? message.Body[..140] + "…" : message.Body;
        var title = thread?.IsGroup == true ? thread.Title ?? "Group chat" : senderName;
        notifier.Publish(new ChatMessageNotice(threadId, title, thread?.IsGroup == true, senderUsername, senderName, preview, recipients, message.CreatedAt));
    }

    private async Task<List<string>> ExistingUsernamesAsync(IEnumerable<string> usernames, CancellationToken cancellationToken)
    {
        var wanted = usernames.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim().ToLowerInvariant()).Distinct().ToList();
        return await dbContext.AppUsers.AsNoTracking()
            .Where(u => u.IsActive && wanted.Contains(u.Username.ToLower()))
            .Select(u => u.Username)
            .ToListAsync(cancellationToken);
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "file" : name.Length > 200 ? name[..200] : name;
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
