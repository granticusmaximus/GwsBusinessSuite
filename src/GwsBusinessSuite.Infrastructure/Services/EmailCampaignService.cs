using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class EmailCampaignService(
    IAppDbContext db,
    IEmailCampaignEmailSender emailSender,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<EmailCampaignEmailOptions> emailOptions,
    TimeProvider timeProvider,
    ILogger<EmailCampaignService> logger) : IEmailCampaignService
{
    // Non-expiring by design - IDataProtector.Unprotect has no built-in TTL unless a
    // time-limited protector is used, and an unsubscribe link must keep working from an email
    // sent months ago, unlike the short-lived state tokens OAuth connect flows use.
    private const string UnsubscribeProtectorPurpose = "GwsBusinessSuite.EmailCampaignUnsubscribe.v1";

    public async Task<IReadOnlyList<EmailCampaignView>> ListCampaignsAsync(CancellationToken cancellationToken = default)
    {
        var campaigns = await db.EmailCampaigns.AsNoTracking().Include(campaign => campaign.Steps).ToListAsync(cancellationToken);
        var activeCounts = (await db.EmailCampaignEnrollments.AsNoTracking()
            .Where(enrollment => enrollment.Status == EmailCampaignEnrollmentStatuses.Active)
            .Select(enrollment => enrollment.CampaignId)
            .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        return campaigns
            .OrderBy(campaign => campaign.Name)
            .Select(campaign => ToView(campaign, activeCounts.GetValueOrDefault(campaign.Id)))
            .ToList();
    }

    public async Task<EmailCampaignView?> GetCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.EmailCampaigns.AsNoTracking()
            .Include(item => item.Steps)
            .FirstOrDefaultAsync(item => item.Id == campaignId, cancellationToken);
        if (campaign is null) return null;

        var activeCount = await db.EmailCampaignEnrollments.AsNoTracking()
            .CountAsync(enrollment => enrollment.CampaignId == campaignId && enrollment.Status == EmailCampaignEnrollmentStatuses.Active, cancellationToken);
        return ToView(campaign, activeCount);
    }

    public async Task<EmailCampaignView> SaveCampaignAsync(EmailCampaignEditorModel editor, string performedBy, CancellationToken cancellationToken = default)
    {
        var name = editor.Name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A campaign name is required.", nameof(editor));
        }
        foreach (var step in editor.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Subject))
            {
                throw new ArgumentException("Every step needs a subject.", nameof(editor));
            }
            if (step.DelayDays < 0)
            {
                throw new ArgumentException("A step's delay can't be negative.", nameof(editor));
            }
        }

        var now = timeProvider.GetUtcNow();
        EmailCampaign campaign;
        if (editor.Id is Guid id)
        {
            campaign = await db.EmailCampaigns.Include(item => item.Steps)
                .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Campaign {id} was not found.");
            db.EmailCampaignSteps.RemoveRange(campaign.Steps);
            campaign.Steps.Clear();
        }
        else
        {
            campaign = new EmailCampaign { Name = name, CreatedAt = now, CreatedBy = performedBy };
            db.EmailCampaigns.Add(campaign);
        }

        campaign.Name = name;
        campaign.Description = editor.Description.Trim();
        campaign.UpdatedAt = now;
        campaign.UpdatedBy = performedBy;

        for (var index = 0; index < editor.Steps.Count; index++)
        {
            var stepEditor = editor.Steps[index];
            campaign.Steps.Add(new EmailCampaignStep
            {
                CampaignId = campaign.Id,
                StepOrder = index,
                Subject = stepEditor.Subject.Trim(),
                Body = stepEditor.Body,
                DelayDays = stepEditor.DelayDays,
                CreatedAt = now,
                CreatedBy = performedBy,
                UpdatedAt = now,
                UpdatedBy = performedBy
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var activeCount = await db.EmailCampaignEnrollments.AsNoTracking()
            .CountAsync(enrollment => enrollment.CampaignId == campaign.Id && enrollment.Status == EmailCampaignEnrollmentStatuses.Active, cancellationToken);
        return ToView(campaign, activeCount);
    }

    public async Task DeleteCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.EmailCampaigns.Include(item => item.Steps).FirstOrDefaultAsync(item => item.Id == campaignId, cancellationToken);
        if (campaign is null) return;

        var enrollments = await db.EmailCampaignEnrollments.Where(item => item.CampaignId == campaignId).ToListAsync(cancellationToken);
        var enrollmentIds = enrollments.Select(item => item.Id).ToList();
        var sendLogs = await db.EmailCampaignSendLogs.Where(item => enrollmentIds.Contains(item.EnrollmentId)).ToListAsync(cancellationToken);
        db.EmailCampaignSendLogs.RemoveRange(sendLogs);
        db.EmailCampaignEnrollments.RemoveRange(enrollments);
        db.EmailCampaignSteps.RemoveRange(campaign.Steps);
        db.EmailCampaigns.Remove(campaign);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<EmailCampaignView> SetCampaignStatusAsync(Guid campaignId, string status, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!EmailCampaignStatuses.All.Contains(status))
        {
            throw new ArgumentException($"'{status}' is not a valid campaign status.", nameof(status));
        }

        var campaign = await db.EmailCampaigns.Include(item => item.Steps)
            .FirstOrDefaultAsync(item => item.Id == campaignId, cancellationToken)
            ?? throw new InvalidOperationException($"Campaign {campaignId} was not found.");

        campaign.Status = status;
        campaign.UpdatedAt = timeProvider.GetUtcNow();
        campaign.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);

        var activeCount = await db.EmailCampaignEnrollments.AsNoTracking()
            .CountAsync(enrollment => enrollment.CampaignId == campaignId && enrollment.Status == EmailCampaignEnrollmentStatuses.Active, cancellationToken);
        return ToView(campaign, activeCount);
    }

    public async Task<IReadOnlyList<EmailCampaignEnrollmentView>> ListEnrollmentsAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var enrollments = await db.EmailCampaignEnrollments.AsNoTracking()
            .Where(enrollment => enrollment.CampaignId == campaignId)
            .ToListAsync(cancellationToken);

        var contactIds = enrollments.Select(enrollment => enrollment.ContactId).Distinct().ToList();
        var contactNames = await db.Contacts.AsNoTracking()
            .Where(contact => contactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);

        return enrollments
            .OrderByDescending(enrollment => enrollment.CreatedAt)
            .Select(enrollment => new EmailCampaignEnrollmentView(
                enrollment.Id, enrollment.ContactId, contactNames.GetValueOrDefault(enrollment.ContactId, "Unknown contact"),
                enrollment.Status, enrollment.NextStepIndex, enrollment.NextSendAt, enrollment.CompletedAt, enrollment.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<EmailCampaignEnrollmentForContactView>> ListEnrollmentsForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var enrollments = await db.EmailCampaignEnrollments.AsNoTracking()
            .Where(enrollment => enrollment.ContactId == contactId)
            .ToListAsync(cancellationToken);

        var campaignIds = enrollments.Select(enrollment => enrollment.CampaignId).Distinct().ToList();
        var campaigns = await db.EmailCampaigns.AsNoTracking()
            .Include(campaign => campaign.Steps)
            .Where(campaign => campaignIds.Contains(campaign.Id))
            .ToDictionaryAsync(campaign => campaign.Id, cancellationToken);

        return enrollments
            .OrderByDescending(enrollment => enrollment.CreatedAt)
            .Select(enrollment =>
            {
                var campaign = campaigns.GetValueOrDefault(enrollment.CampaignId);
                return new EmailCampaignEnrollmentForContactView(
                    enrollment.Id, enrollment.CampaignId, campaign?.Name ?? "Unknown campaign",
                    enrollment.Status, enrollment.NextStepIndex, campaign?.Steps.Count ?? 0,
                    enrollment.NextSendAt, enrollment.CompletedAt, enrollment.CreatedAt);
            })
            .ToList();
    }

    public async Task<bool> EnrollContactAsync(Guid campaignId, Guid contactId, CancellationToken cancellationToken = default)
    {
        var campaign = await db.EmailCampaigns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == campaignId, cancellationToken);
        if (campaign is null || campaign.Status != EmailCampaignStatuses.Active) return false;

        var contact = await db.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == contactId && item.TrashedAt == null, cancellationToken);
        if (contact is null || contact.UnsubscribedFromCampaignsAt is not null) return false;

        var alreadyEnrolled = await db.EmailCampaignEnrollments.AsNoTracking()
            .AnyAsync(enrollment => enrollment.CampaignId == campaignId && enrollment.ContactId == contactId, cancellationToken);
        if (alreadyEnrolled) return false;

        var now = timeProvider.GetUtcNow();
        db.EmailCampaignEnrollments.Add(new EmailCampaignEnrollment
        {
            CampaignId = campaignId,
            ContactId = contactId,
            NextStepIndex = 0,
            NextSendAt = now,
            CreatedAt = now,
            CreatedBy = "enrollment"
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UnsubscribeByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        Guid contactId;
        try
        {
            contactId = Guid.Parse(dataProtectionProvider.CreateProtector(UnsubscribeProtectorPurpose).Unprotect(token));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return false;
        }

        var contact = await db.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken);
        if (contact is null) return false;

        var now = timeProvider.GetUtcNow();
        contact.UnsubscribedFromCampaignsAt ??= now;

        var activeEnrollments = await db.EmailCampaignEnrollments
            .Where(enrollment => enrollment.ContactId == contactId && enrollment.Status == EmailCampaignEnrollmentStatuses.Active)
            .ToListAsync(cancellationToken);
        foreach (var enrollment in activeEnrollments)
        {
            enrollment.Status = EmailCampaignEnrollmentStatuses.Cancelled;
            enrollment.UpdatedAt = now;
            enrollment.UpdatedBy = "unsubscribe";
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ResubscribeContactAsync(Guid contactId, string performedBy, CancellationToken cancellationToken = default)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(
            item => item.Id == contactId && item.TrashedAt == null,
            cancellationToken);
        if (contact is null || contact.UnsubscribedFromCampaignsAt is null)
        {
            return false;
        }

        contact.UnsubscribedFromCampaignsAt = null;
        contact.UpdatedAt = timeProvider.GetUtcNow();
        contact.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> ProcessDueSendsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        // SQLite/EF Core can't translate a DateTimeOffset "<=" comparison - materialize every
        // Active enrollment with a NextSendAt set, then filter due-ness client-side.
        var candidates = (await db.EmailCampaignEnrollments
            .Where(enrollment => enrollment.Status == EmailCampaignEnrollmentStatuses.Active && enrollment.NextSendAt != null)
            .ToListAsync(cancellationToken))
            .Where(enrollment => enrollment.NextSendAt <= now)
            .ToList();
        if (candidates.Count == 0) return 0;

        var campaignIds = candidates.Select(enrollment => enrollment.CampaignId).Distinct().ToList();
        var campaigns = await db.EmailCampaigns.Include(campaign => campaign.Steps)
            .Where(campaign => campaignIds.Contains(campaign.Id))
            .ToDictionaryAsync(campaign => campaign.Id, cancellationToken);

        var attempted = 0;
        foreach (var enrollment in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!campaigns.TryGetValue(enrollment.CampaignId, out var campaign) || campaign.Status != EmailCampaignStatuses.Active)
            {
                continue;
            }

            var contact = await db.Contacts.FirstOrDefaultAsync(item => item.Id == enrollment.ContactId, cancellationToken);
            if (contact is null || contact.TrashedAt is not null || contact.UnsubscribedFromCampaignsAt is not null || string.IsNullOrWhiteSpace(contact.Email))
            {
                enrollment.Status = EmailCampaignEnrollmentStatuses.Cancelled;
                enrollment.UpdatedAt = now;
                enrollment.UpdatedBy = "campaign-sweep";
                continue;
            }

            var orderedSteps = campaign.Steps.OrderBy(step => step.StepOrder).ToList();
            var step = orderedSteps.ElementAtOrDefault(enrollment.NextStepIndex);
            if (step is null)
            {
                enrollment.Status = EmailCampaignEnrollmentStatuses.Completed;
                enrollment.CompletedAt = now;
                enrollment.UpdatedAt = now;
                enrollment.UpdatedBy = "campaign-sweep";
                continue;
            }

            attempted++;
            var succeeded = true;
            var errorMessage = string.Empty;
            try
            {
                var unsubscribeToken = dataProtectionProvider.CreateProtector(UnsubscribeProtectorPurpose).Protect(contact.Id.ToString());
                var unsubscribeUrl = $"{emailOptions.Value.PublicBaseUrl.TrimEnd('/')}/campaigns/unsubscribe/{Uri.EscapeDataString(unsubscribeToken)}";
                var subject = ResolveTokens(step.Subject, contact);
                var body = ResolveTokens(step.Body, contact);
                await emailSender.SendStepAsync(contact.Email!, subject, body, unsubscribeUrl, cancellationToken);
            }
            catch (Exception ex)
            {
                succeeded = false;
                errorMessage = ex.Message;
                logger.LogWarning(ex, "Campaign step send failed for enrollment {EnrollmentId}.", enrollment.Id);
            }

            db.EmailCampaignSendLogs.Add(new EmailCampaignSendLog
            {
                EnrollmentId = enrollment.Id,
                StepId = step.Id,
                Succeeded = succeeded,
                ErrorMessage = errorMessage,
                CreatedAt = now,
                CreatedBy = "campaign-sweep"
            });

            var nextStep = orderedSteps.ElementAtOrDefault(enrollment.NextStepIndex + 1);
            enrollment.NextStepIndex++;
            enrollment.UpdatedAt = now;
            enrollment.UpdatedBy = "campaign-sweep";
            if (nextStep is null)
            {
                enrollment.Status = EmailCampaignEnrollmentStatuses.Completed;
                enrollment.CompletedAt = now;
                enrollment.NextSendAt = null;
            }
            else
            {
                enrollment.NextSendAt = now.AddDays(nextStep.DelayDays);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return attempted;
    }

    private static string ResolveTokens(string text, Contact contact)
    {
        var firstName = contact.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? contact.FullName;
        return text
            .Replace("{{FirstName}}", firstName, StringComparison.Ordinal)
            .Replace("{{FullName}}", contact.FullName, StringComparison.Ordinal);
    }

    private static EmailCampaignView ToView(EmailCampaign campaign, int activeEnrollmentCount) => new(
        campaign.Id,
        campaign.Name,
        campaign.Description,
        campaign.Status,
        campaign.Steps
            .OrderBy(step => step.StepOrder)
            .Select(step => new EmailCampaignStepView(step.Id, step.StepOrder, step.Subject, step.Body, step.DelayDays))
            .ToList(),
        activeEnrollmentCount,
        campaign.CreatedAt);
}
