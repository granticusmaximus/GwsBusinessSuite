using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IFormSubmissionService
{
    Task<FormSubmission> SubmitAsync(
        Guid pageId,
        string name,
        string email,
        string message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormSubmission>> ListAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid submissionId, CancellationToken cancellationToken = default);

    Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default);
}
