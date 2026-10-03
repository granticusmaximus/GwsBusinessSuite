using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Community;

public sealed record DepartmentView(
    Guid Id,
    string Name,
    string Description,
    string? LeadUsername,
    string? LeadDisplayName,
    int MemberCount);

public sealed class DepartmentEditorModel
{
    public Guid? DepartmentId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public string? LeadUsername { get; set; }
}

// One per AppUser, lazily materialized (see ICommunityDirectoryService.GetProfileByUsernameAsync)
// rather than backfilled for every existing account up front - an account with no MemberProfile
// row yet just shows sensible AppUser-derived defaults (DisplayName = Username, everything else
// empty) until its owner (or an admin) fills it in.
public sealed record MemberProfileView(
    Guid? ProfileId,
    string Username,
    string Role,
    bool IsActive,
    string DisplayName,
    string AvatarUrl,
    string Bio,
    string JobTitle,
    Guid? DepartmentId,
    string? DepartmentName,
    string Phone,
    string LinkedInUrl,
    string TwitterUrl,
    string WebsiteUrl,
    // Every department the member belongs to, primary first.
    IReadOnlyList<DepartmentMembershipView>? Departments = null,
    string Email = "",
    bool NotifyUnreadMessagesByEmail = true)
{
    public IReadOnlyList<DepartmentMembershipView> AllDepartments => Departments ?? [];
    public bool IsInDepartment(Guid departmentId) => AllDepartments.Any(d => d.Id == departmentId);
}

public sealed record DepartmentMembershipView(Guid Id, string Name, bool IsPrimary);

public sealed class MemberProfileEditorModel
{
    [Required]
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    // Further departments beyond the primary one.
    public List<Guid> AdditionalDepartmentIds { get; set; } = [];
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool NotifyUnreadMessagesByEmail { get; set; } = true;
    public string LinkedInUrl { get; set; } = string.Empty;
    public string TwitterUrl { get; set; } = string.Empty;
    public string WebsiteUrl { get; set; } = string.Empty;
}

public sealed record ActivityEventView(
    Guid Id,
    string ActorUsername,
    string ActorDisplayName,
    string Verb,
    string TargetLabel,
    string TargetUrl,
    DateTimeOffset OccurredAt);

public sealed record ChatThreadSummary(
    Guid ThreadId,
    bool IsGroup,
    string Title,
    string? OtherParticipantUsername,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    int UnreadCount,
    int ParticipantCount = 2,
    // Display names of everyone else in the thread.
    IReadOnlyList<string>? OtherParticipantNames = null);

public sealed record ChatMessageView(
    Guid Id,
    string SenderUsername,
    string SenderDisplayName,
    string Body,
    DateTimeOffset SentAt,
    IReadOnlyList<ChatAttachmentView>? Attachments = null)
{
    public IReadOnlyList<ChatAttachmentView> AllAttachments => Attachments ?? [];
}

public sealed record ChatAttachmentView(Guid Id, string FileName, string ContentType, long SizeBytes)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed record ChatAttachmentUpload(string FileName, string ContentType, byte[] Content);

public sealed record ChatAttachmentFile(string FileName, string ContentType, byte[] Content);

public sealed record ChatParticipantView(string Username, string DisplayName);

// Raised after a message is stored, for live delivery (the bell and open Messages pages).
public sealed record ChatMessageNotice(Guid ThreadId, string ThreadTitle, bool IsGroup, string SenderUsername,
    string SenderDisplayName, string Preview, IReadOnlyList<string> RecipientUsernames, DateTimeOffset SentAt);

public interface IChatNotifier
{
    void Publish(ChatMessageNotice notice);
}

public static class ChatLimits
{
    public const int MaxAttachmentsPerMessage = 5;
    public const long MaxAttachmentBytes = 10 * 1024 * 1024;
    public const int MaxGroupSize = 50;
}
