using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IFormSubmissionService
{
    // fields is { label: submittedValue } for every field on the form widget that was
    // submitted, since forms have admin-defined arbitrary fields rather than a fixed set.
    // identityFields is { role: submittedValue } (role in "email"/"name"/"company"/"phone") for
    // whichever fields the page builder admin marked with a role - see the "form" widget's
    // FormFieldDef.Role - used to populate FormSubmission's structured Email/FullName/Company/
    // Phone columns. autoCreateContact mirrors the widget's own opt-in checkbox: when true and
    // identityFields contains a non-empty "email", a Contact is found-or-created and linked.
    Task<FormSubmission> SubmitAsync(
        Guid pageId,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string>? identityFields = null,
        bool autoCreateContact = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormSubmission>> ListAsync(Guid pageId, CancellationToken cancellationToken = default);

    // For the new per-Contact detail page's Form Submissions section - every submission ever
    // linked to this Contact, newest first, regardless of which page it came from.
    Task<IReadOnlyList<FormSubmission>> ListForContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    // Sets (or overwrites) a submission's ContactId - used by the manual "Create Contact" button
    // on the submission detail page once a Contact has been found/created for it.
    Task LinkToContactAsync(Guid submissionId, Guid contactId, CancellationToken cancellationToken = default);

    // For the admin detail page a notification email links to - a single submission by id,
    // regardless of which page it belongs to (unlike ListAsync, which is scoped per page).
    Task<FormSubmission?> GetAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default);
}
