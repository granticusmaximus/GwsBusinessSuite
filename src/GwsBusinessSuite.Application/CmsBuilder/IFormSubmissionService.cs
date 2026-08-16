using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IFormSubmissionService
{
    // fields is { label: submittedValue } for every field on the form widget that was
    // submitted, since forms have admin-defined arbitrary fields rather than a fixed set.
    Task<FormSubmission> SubmitAsync(
        Guid pageId,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormSubmission>> ListAsync(Guid pageId, CancellationToken cancellationToken = default);

    // For the admin detail page a notification email links to - a single submission by id,
    // regardless of which page it belongs to (unlike ListAsync, which is scoped per page).
    Task<FormSubmission?> GetAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default);
}
