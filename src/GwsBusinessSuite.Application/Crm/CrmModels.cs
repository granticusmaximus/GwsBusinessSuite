using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Crm;

public sealed class ContactEditorModel
{
    public Guid? ContactId { get; set; }

    [Required]
    public string FullName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Company { get; set; }

    public string Status { get; set; } = "Lead";
}
