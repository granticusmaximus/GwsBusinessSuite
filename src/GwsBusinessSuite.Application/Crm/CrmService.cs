using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Crm;

public sealed class CrmService(IAppDbContext dbContext) : ICrmService
{
    public async Task<IReadOnlyList<Contact>> ListContactsAsync(CancellationToken cancellationToken = default)
    {
        var contacts = await dbContext.Contacts
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return contacts
            .OrderByDescending(contact => contact.UpdatedAt ?? contact.CreatedAt)
            .ThenBy(contact => contact.FullName)
            .ToList();
    }

    public async Task<Contact?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(contact => contact.Id == contactId, cancellationToken);
    }

    public async Task<Contact> SaveContactAsync(ContactEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        var contact = editor.ContactId is { } contactId
            ? await dbContext.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken)
            : null;

        var isNew = contact is null;
        contact ??= new Contact
        {
            FullName = string.Empty,
            CreatedAt = now,
            CreatedBy = "crm-ui"
        };

        contact.FullName = editor.FullName.Trim();
        contact.Email = string.IsNullOrWhiteSpace(editor.Email) ? null : editor.Email.Trim();
        contact.Company = string.IsNullOrWhiteSpace(editor.Company) ? null : editor.Company.Trim();
        contact.Status = string.IsNullOrWhiteSpace(editor.Status) ? "Lead" : editor.Status.Trim();
        contact.UpdatedAt = now;
        contact.UpdatedBy = "crm-ui";

        if (isNew)
        {
            await dbContext.Contacts.AddAsync(contact, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return contact;
    }

    public async Task DeleteContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await dbContext.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken);
        if (contact is null)
        {
            return;
        }

        dbContext.Contacts.Remove(contact);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
