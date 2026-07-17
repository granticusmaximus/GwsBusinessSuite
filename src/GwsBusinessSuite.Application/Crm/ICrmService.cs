using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Crm;

public interface ICrmService
{
    Task<CrmDashboardData> GetDashboardAsync(CancellationToken cancellationToken = default);

    // Excludes trashed contacts unless includeTrashed is set - mirrors CmsBuilder's
    // ListPagesAsync/ListTrashedPagesAsync split.
    Task<IReadOnlyList<Contact>> ListContactsAsync(bool includeTrashed = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Contact>> ListTrashedContactsAsync(CancellationToken cancellationToken = default);

    Task<Contact?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<Contact> SaveContactAsync(ContactEditorModel editor, CancellationToken cancellationToken = default);

    // Soft-deletes (sets TrashedAt) so a contact can be recovered from Trash instead of
    // being lost outright - see RestoreContactAsync/DeleteContactPermanentlyAsync.
    Task TrashContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task RestoreContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task DeleteContactPermanentlyAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContactActivityView>> ListActivitiesAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<ContactActivityView> AddActivityAsync(Guid contactId, string note, CancellationToken cancellationToken = default);

    // Contacts with a FollowUpDate today-or-earlier (and not trashed), earliest due first.
    Task<IReadOnlyList<Contact>> ListDueFollowUpsAsync(CancellationToken cancellationToken = default);

    Task<int> CountDueFollowUpsAsync(CancellationToken cancellationToken = default);
}

public sealed record CrmDashboardData(
    IReadOnlyList<Contact> Contacts,
    IReadOnlyList<Contact> DueFollowUps);
