using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Crm;
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
    ILogger<FormSubmissionService> logger,
    // Both optional, resolved by DI in production - same fire-and-forget/no-op-in-tests pattern
    // as CrmService's own automationTriggerService dependency. crmService powers the opt-in
    // auto-create-Contact behavior; automationTriggerService fires cms.formSubmittedTrigger
    // subscribers. Neither failure can prevent the submission itself from saving.
    ICrmService? crmService = null,
    IAutomationTriggerService? automationTriggerService = null) : IFormSubmissionService
{
    private const int MaxFieldCount = 50;
    private const int MaxFieldValueLength = 5000;

    public async Task<FormSubmission> SubmitAsync(
        Guid pageId,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string>? identityFields = null,
        bool autoCreateContact = false,
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

        var email = identityFields?.GetValueOrDefault("email");
        var fullName = identityFields?.GetValueOrDefault("name");
        var company = identityFields?.GetValueOrDefault("company");
        var phone = identityFields?.GetValueOrDefault("phone");

        var submission = new FormSubmission
        {
            PageId = pageId,
            FieldsJson = JsonSerializer.Serialize(trimmed),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim(),
            Company = string.IsNullOrWhiteSpace(company) ? null : company.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
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

        if (autoCreateContact && crmService is not null && !string.IsNullOrWhiteSpace(submission.Email))
        {
            try
            {
                var contact = await crmService.FindOrCreateContactAsync(
                    submission.Email, submission.FullName, submission.Company, "form-submission", cancellationToken);
                submission.ContactId = contact.Id;
                await dbContext.SaveChangesAsync(cancellationToken);
                await crmService.AddActivityAsync(contact.Id, $"Submitted the \"{page.Title}\" form.", cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to auto-create/link a Contact for form submission {SubmissionId}.", submission.Id);
            }
        }

        if (automationTriggerService is not null)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    submissionId = submission.Id.ToString(),
                    pageId = page.Id.ToString(),
                    siteId = page.SiteId.ToString(),
                    slug = page.Slug,
                    email = submission.Email,
                    fullName = submission.FullName,
                    company = submission.Company,
                    phone = submission.Phone,
                    fields = trimmed
                });
                await automationTriggerService.TriggerCmsFormSubmittedAsync(page.SiteId, payload, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CMS form-submitted automation trigger failed for submission {SubmissionId}.", submission.Id);
            }
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

    public async Task<IReadOnlyList<FormSubmission>> ListForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var submissions = await dbContext.FormSubmissions
            .AsNoTracking()
            .Where(submission => submission.ContactId == contactId)
            .ToListAsync(cancellationToken);

        return submissions
            .OrderByDescending(submission => submission.CreatedAt)
            .ToList();
    }

    public async Task LinkToContactAsync(Guid submissionId, Guid contactId, CancellationToken cancellationToken = default)
    {
        var submission = await dbContext.FormSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return;
        }

        submission.ContactId = contactId;
        await dbContext.SaveChangesAsync(cancellationToken);
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
