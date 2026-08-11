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

public sealed class DealEditorModel
{
    public Guid? DealId { get; set; }

    [Required]
    public Guid ContactId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Stage { get; set; } = DealStages.Lead;

    public decimal ValueUsd { get; set; }

    public DateTimeOffset? ExpectedCloseDate { get; set; }

    public string Notes { get; set; } = string.Empty;
}

// Denormalizes the contact's name onto the view so the pipeline board doesn't need a
// second round trip per card - deals are always browsed alongside whose deal they are.
public sealed class DealView
{
    public Guid Id { get; init; }
    public Guid ContactId { get; init; }
    public string ContactName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Stage { get; init; } = DealStages.Lead;
    public decimal ValueUsd { get; init; }
    public DateTimeOffset? ExpectedCloseDate { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}
