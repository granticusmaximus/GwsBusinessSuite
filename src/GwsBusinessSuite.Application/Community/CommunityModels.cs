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
    string WebsiteUrl);

public sealed class MemberProfileEditorModel
{
    [Required]
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public string Phone { get; set; } = string.Empty;
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
    int UnreadCount);

public sealed record ChatMessageView(
    Guid Id,
    string SenderUsername,
    string SenderDisplayName,
    string Body,
    DateTimeOffset SentAt);
