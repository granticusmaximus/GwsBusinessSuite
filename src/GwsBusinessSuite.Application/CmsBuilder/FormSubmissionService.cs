using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class FormSubmissionService(IAppDbContext dbContext) : IFormSubmissionService
{
    private const int MaxFieldCount = 50;
    private const int MaxFieldValueLength = 5000;

    public async Task<FormSubmission> SubmitAsync(
        Guid pageId,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default)
    {
        var trimmed = fields
            .Select(kvp => (Label: kvp.Key.Trim(), Value: (kvp.Value ?? string.Empty).Trim()))
            .Where(f => !string.IsNullOrWhiteSpace(f.Label) && !string.IsNullOrWhiteSpace(f.Value))
            .Take(MaxFieldCount)
            .ToDictionary(f => f.Label, f => f.Value.Length > MaxFieldValueLength ? f.Value[..MaxFieldValueLength] : f.Value);

        if (trimmed.Count == 0)
        {
            throw new ArgumentException("At least one field must have a value.", nameof(fields));
        }

        var pageExists = await dbContext.CmsPages.AnyAsync(page => page.Id == pageId, cancellationToken);
        if (!pageExists)
        {
            throw new InvalidOperationException("The page this form belongs to no longer exists.");
        }

        var submission = new FormSubmission
        {
            PageId = pageId,
            FieldsJson = JsonSerializer.Serialize(trimmed),
            CreatedBy = "public-form"
        };

        await dbContext.FormSubmissions.AddAsync(submission, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return submission;
    }

    public async Task<IReadOnlyList<FormSubmission>> ListAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var submissions = await dbContext.FormSubmissions
            .AsNoTracking()
            .Where(submission => submission.PageId == pageId)
            .ToListAsync(cancellationToken);

        return submissions
            .OrderByDescending(submission => submission.CreatedAt)
            .ToList();
    }

    public async Task MarkReadAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        var submission = await dbContext.FormSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return;
        }

        submission.IsRead = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        var submission = await dbContext.FormSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return;
        }

        dbContext.FormSubmissions.Remove(submission);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var submissions = await dbContext.FormSubmissions
            .Where(submission => submission.PageId == pageId)
            .ToListAsync(cancellationToken);

        if (submissions.Count > 0)
        {
            dbContext.FormSubmissions.RemoveRange(submissions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
