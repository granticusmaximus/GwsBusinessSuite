using GwsBusinessSuite.Domain.Common;

namespace GwsBusinessSuite.Domain.Entities;

// Workstream E (BuddyPress/Woffice-inspired internal community/intranet system) - scoped as an
// internal staff intranet, not a public-facing social network: members are existing AppUser
// accounts (extended with a profile here), not a new public-registration user class. AppUser
// itself stays untouched (auth-only: Username/PasswordHash/Role/IsActive/lockout/MFA, no display
// name/avatar/bio) - these are separate tables, matching this codebase's general pattern of
// keeping auth-critical entities minimal.
//
// Every cross-entity reference below (AppUserId, DepartmentId, ThreadId, LeadUsername) is a loose
// reference - no FK constraint, no cascade delete - same convention already established by
// ContactActivity.ContactId and ClientPortalLoginToken's own ContactId: a trashed/deleted AppUser
// or Department shouldn't be blocked by, or silently cascade-delete, a row that merely references
// it elsewhere.

// 1:1 with AppUser via AppUserId (unique index, not a DB-level FK - see note above).
public sealed class MemberProfile : AuditableEntity
{
    public Guid AppUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;

    // The member's primary department (shown first in the directory). Further departments live
    // in DepartmentMembership rows.
    public Guid? DepartmentId { get; set; }

    public string Phone { get; set; } = string.Empty;
    // Work email - also where unread-message reminders go (when NotifyUnreadMessagesByEmail).
    public string Email { get; set; } = string.Empty;
    public bool NotifyUnreadMessagesByEmail { get; set; } = true;
    public string LinkedInUrl { get; set; } = string.Empty;
    public string TwitterUrl { get; set; } = string.Empty;
    public string WebsiteUrl { get; set; } = string.Empty;
}

// LeadUsername (an AppUser.Username, loose reference) powers the department-scoped permission
// added in Phase 3 - a lead can edit their own department's member profiles - without a
// separate role/permission table.
// Additional departments beyond a member's primary MemberProfile.DepartmentId - a person can
// belong to several teams. Membership in any of them counts for department-scoped permissions
// (Sentinel sharing, department-lead profile editing).
public sealed class DepartmentMembership : AuditableEntity
{
    public Guid DepartmentId { get; set; }
    public Guid AppUserId { get; set; }
}

public sealed class Department : AuditableEntity
{
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? LeadUsername { get; set; }
}

// Append-only activity log entry. CreatedBy/CreatedAt (from AuditableEntity) ARE the actor and
// timestamp - same convention as ContactActivity above, no separate ActorUsername/OccurredAt
// fields duplicating them. Populated by hooking into existing actions (see
// IActivityFeedService's own doc comment for the concrete hook points) rather than a bolt-on
// separate tracking system, per the master plan's own framing.
public sealed class ActivityEvent : AuditableEntity
{
    // An already-human-readable phrase describing what happened, e.g. "edited the wiki page" or
    // "created a new CRM contact" - the feed renders "{actor} {Verb} \"{TargetLabel}\"" directly,
    // so this is content, not a code needing a separate phrase-lookup table.
    public required string Verb { get; set; }
    public string TargetLabel { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
}

// IsGroup=false, Title=null for a 1:1 direct-message thread between exactly two participants;
// IsGroup=true with a real Title for a named group thread.
public sealed class ChatThread : AuditableEntity
{
    public bool IsGroup { get; set; }
    public string? Title { get; set; }
}

// One row per (ThreadId, Username) pair. LastReadAt powers each participant's own unread-count
// badge without a separate read-receipts table.
public sealed class ChatThreadParticipant : AuditableEntity
{
    public Guid ThreadId { get; set; }
    public required string Username { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    // When this participant was last emailed about unread messages in the thread - the reminder
    // sweep only emails again once newer unread messages arrive after it.
    public DateTimeOffset? UnreadEmailSentAt { get; set; }
}

// CreatedBy/CreatedAt double as sender/sent-at, same ContactActivity convention as ActivityEvent
// above.
public sealed class ChatMessage : AuditableEntity
{
    public Guid ThreadId { get; set; }
    // May be empty when the message is only attachments.
    public required string Body { get; set; }
}

// A file sent with a chat message. Stored in the database (like SupportTicketAttachment) and only
// downloadable by participants of the message's thread.
public sealed class ChatMessageAttachment : AuditableEntity
{
    public Guid MessageId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public byte[] Content { get; set; } = [];
}
