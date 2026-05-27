using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Crm;

public interface ICrmService
{
    Task<IReadOnlyList<Contact>> ListContactsAsync(CancellationToken cancellationToken = default);
    Task<Contact?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default);
    Task<Contact> SaveContactAsync(ContactEditorModel editor, CancellationToken cancellationToken = default);
    Task DeleteContactAsync(Guid contactId, CancellationToken cancellationToken = default);
}
