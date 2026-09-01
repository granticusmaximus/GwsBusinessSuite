namespace GwsBusinessSuite.Web.Services;

// The small bits of RelationshipChip.razor worth unit-testing without a component test
// harness - the contact-detail link target and the avatar-circle initial.
public static class RelationshipChipFormatting
{
    public static string ContactDetailUrl(Guid contactId) => $"/admin/crm/contacts/{contactId}";

    public static string Initial(string? contactName) =>
        string.IsNullOrWhiteSpace(contactName)
            ? "?"
            : char.ToUpperInvariant(contactName.Trim()[0]).ToString();
}
