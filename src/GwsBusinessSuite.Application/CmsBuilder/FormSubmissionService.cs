using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed partial class FormSubmissionService(IAppDbContext dbContext) : IFormSubmissionService
{
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;
    private const int MaxMessageLength = 5000;

    public async Task<FormSubmission> SubmitAsync(
        Guid pageId,
        string name,
        string email,
        string message,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedEmail = (email ?? string.Empty).Trim();
        var trimmedMessage = (message ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required and must be {MaxNameLength} characters or fewer.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(trimmedEmail) || trimmedEmail.Length > MaxEmailLength || !EmailPattern().IsMatch(trimmedEmail))
        {
            throw new ArgumentException("A valid email address is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(trimmedMessage) || trimmedMessage.Length > MaxMessageLength)
        {
            throw new ArgumentException($"Message is required and must be {MaxMessageLength} characters or fewer.", nameof(message));
        }

        var pageExists = await dbContext.CmsPages.AnyAsync(page => page.Id == pageId, cancellationToken);
        if (!pageExists)
        {
            throw new InvalidOperationException("The page this form belongs to no longer exists.");
        }

        var submission = new FormSubmission
        {
            PageId = pageId,
            Name = trimmedName,
            Email = trimmedEmail,
            Message = trimmedMessage,
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

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
