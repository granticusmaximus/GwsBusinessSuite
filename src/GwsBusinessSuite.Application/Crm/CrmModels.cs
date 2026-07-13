using System.ComponentModel.DataAnnotations;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Crm;

public sealed class ContactEditorModel
{
    public Guid? ContactId { get; set; }

    [Required]
    public string FullName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Company { get; set; }

    public string Status { get; set; } = ContactStatuses.Lead;

    public DateTimeOffset? FollowUpDate { get; set; }
}

public sealed class ContactActivityView
{
    public Guid Id { get; init; }
    public string Note { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
}
