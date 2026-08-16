using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class FormNotificationOptions
{
    public const string SectionName = "FormNotification";

    // Base URL for the "view this submission" link embedded in a notification email - the
    // admin portal always lives at this fixed host in production (see GrowthReportEmailOptions
    // .DashboardUrl for the same hardcoded-with-config-override convention).
    public string AdminBaseUrl { get; set; } = "https://admin.gwsapp.net";
}

public sealed class FormSubmissionService(
    IAppDbContext dbContext,
    IGrowthReportEmailSender emailSender,
    IOptions<FormNotificationOptions> notificationOptions,
    ILogger<FormSubmissionService> logger) : IFormSubmissionService
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

        var page = await dbContext.CmsPages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
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

        // A notification failure must never take down the submission itself - the visitor's
        // data is already safely saved by this point regardless of what happens next.
        try
        {
            await SendNotificationEmailAsync(page, submission, trimmed, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send form submission notification email for submission {SubmissionId}.", submission.Id);
        }

        return submission;
    }

    private async Task SendNotificationEmailAsync(
        CmsPage page,
        FormSubmission submission,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        var site = await dbContext.CmsSites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == page.SiteId, cancellationToken);
        if (site is null || string.IsNullOrWhiteSpace(site.FormNotificationEmail))
        {
            return;
        }

        if (!emailSender.Configuration.IsConfigured)
        {
            logger.LogWarning(
                "Skipped a form submission notification email for {SubmissionId} - SMTP delivery isn't configured ({Reason}).",
                submission.Id, emailSender.Configuration.Message);
            return;
        }

        var detailUrl = $"{notificationOptions.Value.AdminBaseUrl.TrimEnd('/')}/admin/form-submissions/{submission.Id}";
        var subject = $"New form submission — {page.Title}";

        var plainText = new StringBuilder();
        plainText.AppendLine($"A new submission came in on \"{page.Title}\":");
        plainText.AppendLine();
        foreach (var field in fields)
        {
            plainText.AppendLine($"{field.Key}: {field.Value}");
        }
        plainText.AppendLine();
        plainText.AppendLine($"View it here: {detailUrl}");

        var html = new StringBuilder();
        html.Append($"<p>A new submission came in on <strong>{System.Net.WebUtility.HtmlEncode(page.Title)}</strong>:</p><dl>");
        foreach (var field in fields)
        {
            html.Append($"<dt>{System.Net.WebUtility.HtmlEncode(field.Key)}</dt><dd>{System.Net.WebUtility.HtmlEncode(field.Value)}</dd>");
        }
        html.Append($"""</dl><p><a href="{detailUrl}">View this submission</a></p>""");

        await emailSender.SendAsync(
            new GrowthReportEmail(site.FormNotificationEmail, subject, plainText.ToString(), html.ToString()),
            cancellationToken);
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

    public async Task<FormSubmission?> GetAsync(Guid submissionId, CancellationToken cancellationToken = default) =>
        await dbContext.FormSubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);

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
