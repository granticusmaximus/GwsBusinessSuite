using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Privacy;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class PrivacyOperationsService(
    IAppDbContext db,
    ICurrentUserAccessor currentUser,
    ISecurityAuditService securityAudit,
    IBackupOperations backupOperations,
    TimeProvider timeProvider,
    ILogger<PrivacyOperationsService> logger) : IPrivacyOperationsService
{
    private DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public async Task<PrivacyDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRetentionPoliciesAsync(cancellationToken);
        var policies = await db.PrivacyRetentionPolicies.AsNoTracking().OrderBy(x => x.DataCategory).ToListAsync(cancellationToken);
        var requests = (await db.PrivacyRequests.AsNoTracking().ToListAsync(cancellationToken)).OrderByDescending(x => x.ReceivedAt).ToList();
        var incidents = (await db.SecurityIncidents.AsNoTracking().ToListAsync(cancellationToken)).OrderByDescending(x => x.DetectedAt).ToList();
        var updates = (await db.SecurityIncidentUpdates.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToList();

        var retentionViews = new List<RetentionPolicyView>();
        foreach (var policy in policies)
            retentionViews.Add(new(policy.Id, policy.DataCategory, policy.Description, policy.RetentionDays,
                policy.LegalBasis, policy.IsEnabled, policy.AutomationApproved, policy.LastReviewedAt,
                await CountEligibleAsync(policy, cancellationToken)));

        return new(retentionViews, requests.Select(Map).ToList(),
            incidents.Select(x => Map(x, updates.Where(u => u.SecurityIncidentId == x.Id))).ToList(),
            requests.Count(x => x.CompletedAt is null && x.DueAt < UtcNow),
            incidents.Count(x => x.RegulatorNotificationRequired && x.RegulatorNotifiedAt is null
                && x.RegulatorNotificationDueAt <= UtcNow.AddHours(24)));
    }

    public async Task<PrivacyRequestView> CreateRequestAsync(CreatePrivacyRequest input, CancellationToken cancellationToken = default)
    {
        if (!PrivacyRequestTypes.All.Contains(input.RequestType)) throw new ArgumentException("Unsupported request type.");
        var subject = input.SubjectIdentifier.Trim();
        if (subject.Length is < 2 or > 320) throw new ArgumentException("A valid subject identifier is required.");
        var actor = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        var entity = new PrivacyRequest
        {
            RequestNumber = $"PR-{UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            RequestType = input.RequestType, SubjectIdentifier = subject,
            ReceivedAt = UtcNow, DueAt = UtcNow.AddMonths(1), DecisionNotes = input.DecisionNotes.Trim(),
            CreatedBy = actor
        };
        db.PrivacyRequests.Add(entity); await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("PrivacyRequestCreated", entity.Id, new Dictionary<string, string?> { ["requestType"] = entity.RequestType }, cancellationToken);
        return Map(entity);
    }

    public async Task VerifyIdentityAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var entity = await FindRequestAsync(requestId, cancellationToken);
        entity.IdentityVerifiedAt = UtcNow;
        entity.IdentityVerifiedBy = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        entity.Status = PrivacyRequestStatuses.IdentityVerified;
        entity.UpdatedAt = UtcNow; entity.UpdatedBy = entity.IdentityVerifiedBy;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("PrivacyIdentityVerified", entity.Id, null, cancellationToken);
    }

    public async Task CompleteRequestAsync(Guid requestId, string status, string decisionNotes,
        CancellationToken cancellationToken = default)
    {
        if (status is not (PrivacyRequestStatuses.Fulfilled or PrivacyRequestStatuses.Denied or PrivacyRequestStatuses.InReview))
            throw new ArgumentException("Unsupported completion status.");
        var entity = await FindRequestAsync(requestId, cancellationToken);
        if (entity.IdentityVerifiedAt is null) throw new InvalidOperationException("Identity must be verified before a request can be decided.");
        if (status == PrivacyRequestStatuses.Denied && string.IsNullOrWhiteSpace(decisionNotes))
            throw new ArgumentException("A denial reason is required.");
        // Real evidence, not a human attestation: DeletionExecutedAt is only ever set by a
        // successful DeleteSubjectDataAsync run (see below), so this cannot be faked from the UI
        // the way the old erasureDataDeletionConfirmed checkbox could.
        if (entity.RequestType == PrivacyRequestTypes.Erasure && status == PrivacyRequestStatuses.Fulfilled && entity.DeletionExecutedAt is null)
        {
            throw new InvalidOperationException(
                "Erasure requests can only be marked Fulfilled after DeleteSubjectDataAsync has actually deleted the subject's data.");
        }
        entity.Status = status; entity.DecisionNotes = decisionNotes.Trim();
        entity.CompletedAt = status is PrivacyRequestStatuses.Fulfilled or PrivacyRequestStatuses.Denied ? UtcNow : null;
        entity.UpdatedAt = UtcNow; entity.UpdatedBy = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("PrivacyRequestStatusChanged", entity.Id,
            new Dictionary<string, string?> { ["status"] = status }, cancellationToken);
    }

    public async Task<SubjectDataExport> ExportSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await FindRequestAsync(requestId, cancellationToken);
        if (request.RequestType != PrivacyRequestTypes.Access || request.IdentityVerifiedAt is null)
            throw new InvalidOperationException("Only identity-verified access requests can be exported.");
        var resolution = await SubjectResolver.ResolveAsync(db, request.SubjectIdentifier, cancellationToken);
        var matches = resolution.MatchValues;
        var users = resolution.User is { } resolvedUser
            ? new[] { new { resolvedUser.Id, resolvedUser.Username, resolvedUser.Role, resolvedUser.IsActive, resolvedUser.MfaEnabled, resolvedUser.MfaEnrolledAt, resolvedUser.CreatedAt, resolvedUser.UpdatedAt } }
            : [];
        var contacts = resolution.Contact is { } resolvedContact
            ? new[] { new { resolvedContact.Id, resolvedContact.FullName, resolvedContact.Email, resolvedContact.Company, resolvedContact.Status, resolvedContact.CreatedAt, resolvedContact.UpdatedAt } }
            : [];
        var comments = await db.Comments.AsNoTracking().Where(x => matches.Contains(x.AuthorEmail))
            .Select(x => new { x.Id, x.ArticleId, x.AuthorName, x.AuthorEmail, x.Body, x.Status, x.CreatedAt }).ToListAsync(cancellationToken);
        var aiRuns = await db.SentinelAiRuns.AsNoTracking().Where(x => matches.Contains(x.CreatedBy))
            .Select(x => new { x.Id, x.ConversationId, x.Action, x.Instruction, x.Output, x.Status, x.Model, x.CreatedAt }).ToListAsync(cancellationToken);
        var listening = await db.PodcastListenProgresses.AsNoTracking().Where(x => matches.Contains(x.Username))
            .Select(x => new { x.Id, x.EpisodeId, x.PositionSeconds, x.IsCompleted, x.LastPlayedAt }).ToListAsync(cancellationToken);
        var auditEvents = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => matches.Contains(x.ActorUsername) || (x.TargetId != null && matches.Contains(x.TargetId)))
            .Select(x => new { x.Id, x.OccurredAtUnixSeconds, x.Category, x.Action, x.Outcome, x.TargetType, x.TargetId }).ToListAsync(cancellationToken);
        var payload = new { generatedAt = UtcNow, request = request.RequestNumber, subject = request.SubjectIdentifier, users, contacts, comments, sentinelGpt = aiRuns, podcastProgress = listening, securityEvents = auditEvents };
        var content = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
        await AuditAsync("SubjectDataExported", request.Id,
            new Dictionary<string, string?> { ["requestNumber"] = request.RequestNumber }, cancellationToken);
        return new($"gws-subject-export-{request.RequestNumber}.json", content);
    }

    // Invoice/InvoiceLineItem and FormSubmission.FieldsJson/the wide internal-staff CreatedBy
    // convention are deliberately out of scope for automated erasure - see the erasure plan's
    // scope notes. This note is surfaced in the preview so staff know they weren't silently
    // skipped, and stays fixed text rather than something callers can suppress.
    private const string ErasureNotScannedNote =
        "Invoices are counted separately below and require manual review (financial/tax retention " +
        "rules vary and this app cannot determine which apply) - if any exist, the Contact record " +
        "itself is also kept (Invoices have a real database link to it) even though everything " +
        "else about the subject is deleted. FormSubmission entries and internal staff activity " +
        "metadata (CreatedBy/UpdatedBy on unrelated collaboration records) are not scanned by this " +
        "tool at all.";

    public async Task<SubjectDeletionPreview> PreviewErasureAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await FindRequestAsync(requestId, cancellationToken);
        if (request.RequestType != PrivacyRequestTypes.Erasure)
            throw new InvalidOperationException("Only Erasure requests can be previewed for deletion.");

        var resolution = await SubjectResolver.ResolveAsync(db, request.SubjectIdentifier, cancellationToken);
        if (resolution.IsEmpty)
            return new SubjectDeletionPreview(false, [], 0, ErasureNotScannedNote);

        var matches = resolution.MatchValues;
        var contactId = resolution.Contact?.Id;
        var tables = new List<TableDeletionPreview>();

        if (resolution.Contact is { } contact)
        {
            tables.Add(new("Contacts", 1, [contact.FullName]));
            var activityCount = await db.ContactActivities.AsNoTracking().CountAsync(x => x.ContactId == contact.Id, cancellationToken);
            if (activityCount > 0) tables.Add(new("ContactActivities", activityCount, []));
            var dealCount = await db.Deals.AsNoTracking().CountAsync(x => x.ContactId == contact.Id, cancellationToken);
            if (dealCount > 0) tables.Add(new("Deals", dealCount, []));
            var ticketSubjects = await db.SupportTickets.AsNoTracking().Where(x => x.ContactId == contact.Id)
                .Select(x => x.Subject).ToListAsync(cancellationToken);
            if (ticketSubjects.Count > 0) tables.Add(new("SupportTickets", ticketSubjects.Count, ticketSubjects.Take(5).ToList()));
            var tokenCount = await db.ClientPortalLoginTokens.AsNoTracking().CountAsync(x => x.ContactId == contact.Id, cancellationToken);
            if (tokenCount > 0) tables.Add(new("ClientPortalLoginTokens", tokenCount, []));
            var enrollmentCount = await db.EmailCampaignEnrollments.AsNoTracking().CountAsync(x => x.ContactId == contact.Id, cancellationToken);
            if (enrollmentCount > 0) tables.Add(new("EmailCampaignEnrollments", enrollmentCount, []));
        }

        var bookingNames = await db.Bookings.AsNoTracking()
            .Where(x => (contactId.HasValue && x.ContactId == contactId) || matches.Contains(x.AttendeeEmail))
            .Select(x => x.AttendeeName).ToListAsync(cancellationToken);
        if (bookingNames.Count > 0) tables.Add(new("Bookings", bookingNames.Count, bookingNames.Take(5).ToList()));

        var commentAuthors = await db.Comments.AsNoTracking().Where(x => matches.Contains(x.AuthorEmail))
            .Select(x => x.AuthorName).ToListAsync(cancellationToken);
        if (commentAuthors.Count > 0) tables.Add(new("Comments", commentAuthors.Count, commentAuthors.Take(5).ToList()));

        var aiRunCount = await db.SentinelAiRuns.AsNoTracking().CountAsync(x => matches.Contains(x.CreatedBy), cancellationToken);
        if (aiRunCount > 0) tables.Add(new("SentinelAiRuns", aiRunCount, []));

        var listenCount = await db.PodcastListenProgresses.AsNoTracking().CountAsync(x => matches.Contains(x.Username), cancellationToken);
        if (listenCount > 0) tables.Add(new("PodcastListenProgresses", listenCount, []));

        if (resolution.User is { } user)
            tables.Add(new("AppUsers", 1, [user.Username]));

        var invoiceCount = contactId.HasValue
            ? await db.Invoices.AsNoTracking().CountAsync(x => x.ContactId == contactId, cancellationToken)
            : 0;

        return new SubjectDeletionPreview(true, tables, invoiceCount, ErasureNotScannedNote);
    }

    public async Task<SubjectDeletionSummary> DeleteSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await FindRequestAsync(requestId, cancellationToken);
        if (request.RequestType != PrivacyRequestTypes.Erasure)
            throw new InvalidOperationException("Only Erasure requests can be run through automated deletion.");
        if (request.IdentityVerifiedAt is null)
            throw new InvalidOperationException("Identity must be verified before deletion can run.");

        var resolution = await SubjectResolver.ResolveAsync(db, request.SubjectIdentifier, cancellationToken);
        if (resolution.IsEmpty)
            throw new InvalidOperationException(
                "The subject identifier did not resolve to any AppUser or Contact - nothing to delete. Investigate before retrying.");

        if (resolution.User is { Role: AppRoles.Admin, IsActive: true })
        {
            var activeAdminCount = await db.AppUsers.CountAsync(u => u.Role == AppRoles.Admin && u.IsActive, cancellationToken);
            if (activeAdminCount <= 1)
                throw new InvalidOperationException("Cannot delete the last active admin account through an erasure request.");
        }

        string backupPath;
        try
        {
            backupPath = await backupOperations.CreateBackupAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "A fresh backup could not be created; erasure was aborted before any data was deleted.", ex);
        }

        var matches = resolution.MatchValues;
        var contactId = resolution.Contact?.Id;
        var results = new List<TableDeletionResult>();

        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        try
        {
            if (resolution.Contact is { } contact)
            {
                var ticketIds = await db.SupportTickets.Where(x => x.ContactId == contact.Id)
                    .Select(x => x.Id).ToListAsync(cancellationToken);
                if (ticketIds.Count > 0)
                {
                    var messageIds = await db.SupportTicketMessages.Where(x => ticketIds.Contains(x.TicketId))
                        .Select(x => x.Id).ToListAsync(cancellationToken);
                    if (messageIds.Count > 0)
                    {
                        var attachments = await db.SupportTicketAttachments.Where(x => messageIds.Contains(x.MessageId)).ToListAsync(cancellationToken);
                        if (attachments.Count > 0) { db.SupportTicketAttachments.RemoveRange(attachments); results.Add(new("SupportTicketAttachments", attachments.Count)); }
                        var messages = await db.SupportTicketMessages.Where(x => messageIds.Contains(x.Id)).ToListAsync(cancellationToken);
                        db.SupportTicketMessages.RemoveRange(messages);
                        results.Add(new("SupportTicketMessages", messages.Count));
                    }
                    var tickets = await db.SupportTickets.Where(x => ticketIds.Contains(x.Id)).ToListAsync(cancellationToken);
                    db.SupportTickets.RemoveRange(tickets);
                    results.Add(new("SupportTickets", tickets.Count));
                }

                var deals = await db.Deals.Where(x => x.ContactId == contact.Id).ToListAsync(cancellationToken);
                if (deals.Count > 0) { db.Deals.RemoveRange(deals); results.Add(new("Deals", deals.Count)); }

                var activities = await db.ContactActivities.Where(x => x.ContactId == contact.Id).ToListAsync(cancellationToken);
                if (activities.Count > 0) { db.ContactActivities.RemoveRange(activities); results.Add(new("ContactActivities", activities.Count)); }

                var tokens = await db.ClientPortalLoginTokens.Where(x => x.ContactId == contact.Id).ToListAsync(cancellationToken);
                if (tokens.Count > 0) { db.ClientPortalLoginTokens.RemoveRange(tokens); results.Add(new("ClientPortalLoginTokens", tokens.Count)); }

                var enrollmentIds = await db.EmailCampaignEnrollments.Where(x => x.ContactId == contact.Id)
                    .Select(x => x.Id).ToListAsync(cancellationToken);
                if (enrollmentIds.Count > 0)
                {
                    var sendLogs = await db.EmailCampaignSendLogs.Where(x => enrollmentIds.Contains(x.EnrollmentId)).ToListAsync(cancellationToken);
                    if (sendLogs.Count > 0) { db.EmailCampaignSendLogs.RemoveRange(sendLogs); results.Add(new("EmailCampaignSendLogs", sendLogs.Count)); }
                    var enrollments = await db.EmailCampaignEnrollments.Where(x => enrollmentIds.Contains(x.Id)).ToListAsync(cancellationToken);
                    db.EmailCampaignEnrollments.RemoveRange(enrollments);
                    results.Add(new("EmailCampaignEnrollments", enrollments.Count));
                }
            }

            var bookings = await db.Bookings
                .Where(x => (contactId.HasValue && x.ContactId == contactId) || matches.Contains(x.AttendeeEmail))
                .ToListAsync(cancellationToken);
            if (bookings.Count > 0) { db.Bookings.RemoveRange(bookings); results.Add(new("Bookings", bookings.Count)); }

            var comments = await db.Comments.Where(x => matches.Contains(x.AuthorEmail)).ToListAsync(cancellationToken);
            if (comments.Count > 0) { db.Comments.RemoveRange(comments); results.Add(new("Comments", comments.Count)); }

            var aiRuns = await db.SentinelAiRuns.Where(x => matches.Contains(x.CreatedBy)).ToListAsync(cancellationToken);
            if (aiRuns.Count > 0) { db.SentinelAiRuns.RemoveRange(aiRuns); results.Add(new("SentinelAiRuns", aiRuns.Count)); }

            var listens = await db.PodcastListenProgresses.Where(x => matches.Contains(x.Username)).ToListAsync(cancellationToken);
            if (listens.Count > 0) { db.PodcastListenProgresses.RemoveRange(listens); results.Add(new("PodcastListenProgresses", listens.Count)); }

            if (resolution.User is { } user)
            {
                db.AppUsers.Remove(user);
                results.Add(new("AppUsers", 1));
            }

            if (resolution.Contact is { } contactToRemove)
            {
                // Invoice/InvoiceLineItem have a real OnDelete(Cascade) FK to Contact - removing
                // the Contact row would silently cascade-delete them at the database level even
                // though nothing here ever loaded or touched them, contradicting the decision to
                // exclude Invoices from automated deletion. Leave the Contact row (and its
                // Invoices) in place when any exist; everything else about the subject is still
                // erased above.
                var hasInvoices = await db.Invoices.AnyAsync(x => x.ContactId == contactToRemove.Id, cancellationToken);
                if (!hasInvoices)
                {
                    db.Contacts.Remove(contactToRemove);
                    results.Add(new("Contacts", 1));
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            // Mirrors WikiService's DuplicatePageAsync rollback handling - a Blazor circuit can
            // retain this scoped DbContext after the failed action, and EF's entity states are
            // not rewound by a database rollback on their own.
            if (db is DbContext efContext) efContext.ChangeTracker.Clear();
            throw;
        }

        var executedAt = UtcNow;
        var summary = new SubjectDeletionSummary(executedAt, backupPath, results);
        request.DeletionExecutedAt = executedAt;
        request.DeletionSummaryJson = JsonSerializer.Serialize(summary);
        request.UpdatedAt = executedAt;
        request.UpdatedBy = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // The deletion itself already committed above (and is irreversible) - an audit-write
        // failure past this point must never surface as "the operation could not be completed"
        // (PrivacyOperations.razor's RunAsync shows exactly that generic message on any
        // exception), which would tell the admin the erasure failed when the subject's data is
        // already gone, and a retry would then just fail again with "nothing to delete". Logged
        // at Critical instead - a missing audit trail for an erasure needs real eyes on it, but
        // must never mask that the erasure itself succeeded.
        try
        {
            foreach (var result in results)
            {
                await AuditAsync("ErasureTableDeleted", request.Id, new Dictionary<string, string?>
                {
                    ["table"] = result.TableName,
                    ["deletedCount"] = result.DeletedCount.ToString()
                }, cancellationToken);
            }
            await AuditAsync("ErasureExecuted", request.Id, new Dictionary<string, string?>
            {
                ["requestNumber"] = request.RequestNumber,
                ["totalDeleted"] = results.Sum(x => x.DeletedCount).ToString(),
                ["tablesTouched"] = results.Count.ToString()
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Security audit event(s) for erasure of PrivacyRequest {RequestId} failed to record after the deletion already succeeded.",
                request.Id);
        }

        return summary;
    }

    public async Task<SecurityIncidentView> CreateIncidentAsync(CreateSecurityIncident input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new ArgumentException("Incident title is required.");
        var awareness = input.PersonalDataInvolved ? input.BreachAwarenessAt : null;
        var actor = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        var entity = new SecurityIncident
        {
            IncidentNumber = $"INC-{UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Title = input.Title.Trim(), Summary = input.Summary.Trim(), Severity = input.Severity,
            DetectedAt = input.DetectedAt, PersonalDataInvolved = input.PersonalDataInvolved,
            EphiInvolved = input.EphiInvolved, BreachAwarenessAt = awareness,
            RegulatorNotificationDueAt = awareness?.AddHours(72), Owner = input.Owner.Trim(), CreatedBy = actor
        };
        db.SecurityIncidents.Add(entity); await db.SaveChangesAsync(cancellationToken);
        await AddIncidentUpdateInternalAsync(entity.Id, "Detected", "Incident record opened.", actor, cancellationToken);
        await AuditAsync("SecurityIncidentCreated", entity.Id,
            new Dictionary<string, string?> { ["severity"] = entity.Severity, ["personalData"] = entity.PersonalDataInvolved.ToString() }, cancellationToken);
        return Map(entity, []);
    }

    public async Task AddIncidentUpdateAsync(Guid incidentId, string updateType, string notes, CancellationToken cancellationToken = default)
    {
        if (!await db.SecurityIncidents.AnyAsync(x => x.Id == incidentId, cancellationToken)) throw new KeyNotFoundException("Incident not found.");
        if (string.IsNullOrWhiteSpace(notes)) throw new ArgumentException("Update notes are required.");
        await AddIncidentUpdateInternalAsync(incidentId, updateType.Trim(), notes.Trim(), await currentUser.GetCurrentUsernameAsync(cancellationToken), cancellationToken);
        await AuditAsync("SecurityIncidentUpdated", incidentId, new Dictionary<string, string?> { ["updateType"] = updateType }, cancellationToken);
    }

    public async Task UpdateIncidentAssessmentAsync(Guid incidentId, string riskAssessment, bool regulatorNotificationRequired, DateTimeOffset? regulatorNotifiedAt, string status, CancellationToken cancellationToken = default)
    {
        if (!IncidentStatuses.All.Contains(status)) throw new ArgumentException("Unsupported incident status.");
        var entity = await db.SecurityIncidents.SingleOrDefaultAsync(x => x.Id == incidentId, cancellationToken) ?? throw new KeyNotFoundException("Incident not found.");
        entity.RiskAssessment = riskAssessment.Trim(); entity.RegulatorNotificationRequired = regulatorNotificationRequired;
        entity.RegulatorNotifiedAt = regulatorNotifiedAt; entity.Status = status; entity.UpdatedAt = UtcNow;
        entity.UpdatedBy = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        if (status == IncidentStatuses.Contained && entity.ContainedAt is null) entity.ContainedAt = UtcNow;
        if (status == IncidentStatuses.Resolved) entity.ResolvedAt = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("BreachAssessmentUpdated", incidentId, new Dictionary<string, string?> { ["risk"] = entity.RiskAssessment, ["notificationRequired"] = regulatorNotificationRequired.ToString(), ["status"] = status }, cancellationToken);
    }

    public async Task UpdateRetentionPolicyAsync(Guid policyId, int retentionDays, string legalBasis, bool enabled, bool automationApproved, CancellationToken cancellationToken = default)
    {
        if (retentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (string.IsNullOrWhiteSpace(legalBasis)) throw new ArgumentException("Legal basis is required.");
        var entity = await db.PrivacyRetentionPolicies.SingleOrDefaultAsync(x => x.Id == policyId, cancellationToken) ?? throw new KeyNotFoundException("Retention policy not found.");
        if (entity.DataCategory == "Security audit" && automationApproved)
            throw new InvalidOperationException("Security audit evidence cannot be approved for automated deletion.");
        entity.RetentionDays = retentionDays; entity.LegalBasis = legalBasis.Trim(); entity.IsEnabled = enabled;
        entity.AutomationApproved = automationApproved; entity.LastReviewedAt = UtcNow; entity.UpdatedAt = UtcNow;
        entity.UpdatedBy = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("RetentionPolicyChanged", policyId, new Dictionary<string, string?> { ["category"] = entity.DataCategory, ["days"] = retentionDays.ToString(), ["automationApproved"] = automationApproved.ToString() }, cancellationToken);
    }

    private async Task EnsureRetentionPoliciesAsync(CancellationToken cancellationToken)
    {
        if (await db.PrivacyRetentionPolicies.AnyAsync(cancellationToken)) return;
        db.PrivacyRetentionPolicies.AddRange(
            Policy("Web analytics", "Privacy-minimized first-party visit events.", 400, "Legitimate interests; aggregate measurement"),
            Policy("Form submissions", "Public form submissions that may contain personal data.", 730, "Consent or requested service"),
            Policy("Comments", "Public comment identity and content.", 730, "Consent and legitimate moderation interests"),
            Policy("Security audit", "Security and access evidence; protected from automated deletion.", 2190, "Security, accountability, and HIPAA documentation"));
        await db.SaveChangesAsync(cancellationToken);
    }

    private PrivacyRetentionPolicy Policy(string category, string description, int days, string basis) =>
        new() { DataCategory = category, Description = description, RetentionDays = days, LegalBasis = basis, AutomationApproved = false, IsEnabled = true, CreatedBy = "system" };

    public async Task<int> PurgeEligibleRecordsAsync(CancellationToken cancellationToken = default)
    {
        var policies = await db.PrivacyRetentionPolicies.AsNoTracking()
            .Where(x => x.IsEnabled && x.AutomationApproved)
            .ToListAsync(cancellationToken);

        var totalDeleted = 0;
        foreach (var policy in policies)
        {
            var cutoff = UtcNow.AddDays(-policy.RetentionDays);
            var deleted = await PurgeCategoryAsync(policy.DataCategory, cutoff, cancellationToken);
            if (deleted == 0) continue;

            totalDeleted += deleted;
            await AuditAsync("RetentionPurgeExecuted", policy.Id, new Dictionary<string, string?>
            {
                ["category"] = policy.DataCategory,
                ["deletedCount"] = deleted.ToString(),
                ["retentionDays"] = policy.RetentionDays.ToString(),
                ["cutoff"] = cutoff.ToString("O")
            }, cancellationToken);
        }

        return totalDeleted;
    }

    private async Task<int> PurgeCategoryAsync(string category, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        switch (category)
        {
            case "Web analytics":
                // WebAnalyticsEvent has an indexed OccurredAtUnixSeconds shadow column
                // specifically because SQLite/EF Core can't translate a server-side range
                // filter on a DateTimeOffset column - see the other categories below for the
                // materialize-then-filter fallback this table doesn't need.
                var cutoffUnix = cutoff.ToUnixTimeSeconds();
                var expiredEvents = await db.WebAnalyticsEvents
                    .Where(x => x.OccurredAtUnixSeconds < cutoffUnix)
                    .ToListAsync(cancellationToken);
                if (expiredEvents.Count == 0) return 0;
                db.WebAnalyticsEvents.RemoveRange(expiredEvents);
                await db.SaveChangesAsync(cancellationToken);
                return expiredEvents.Count;

            case "Form submissions":
                return await PurgeByCreatedAtAsync(db.FormSubmissions, cutoff, cancellationToken);

            case "Comments":
                return await PurgeByCreatedAtAsync(db.Comments, cutoff, cancellationToken);

            default:
                // "Security audit" (and any future category) never reaches here for automated
                // deletion: UpdateRetentionPolicyAsync refuses to set AutomationApproved for
                // "Security audit", and PurgeEligibleRecordsAsync only calls this for
                // automation-approved policies.
                return 0;
        }
    }

    // SQLite/EF Core can't translate a server-side range filter on a DateTimeOffset column, and
    // these two categories have no Unix-seconds shadow column (unlike WebAnalyticsEvent) - so,
    // same as CountEligibleAsync below, this materializes CreatedAt to filter client-side, then
    // deletes only the matching rows by Id rather than re-fetching/removing full entities twice.
    private async Task<int> PurgeByCreatedAtAsync<T>(
        Microsoft.EntityFrameworkCore.DbSet<T> set, DateTimeOffset cutoff, CancellationToken cancellationToken)
        where T : GwsBusinessSuite.Domain.Common.AuditableEntity
    {
        var candidates = await set.AsNoTracking()
            .Select(x => new { x.Id, x.CreatedAt })
            .ToListAsync(cancellationToken);
        var expiredIds = candidates.Where(x => x.CreatedAt < cutoff).Select(x => x.Id).ToHashSet();
        if (expiredIds.Count == 0) return 0;

        var expiredRows = await set.Where(x => expiredIds.Contains(x.Id)).ToListAsync(cancellationToken);
        set.RemoveRange(expiredRows);
        await db.SaveChangesAsync(cancellationToken);
        return expiredRows.Count;
    }

    private async Task<int> CountEligibleAsync(PrivacyRetentionPolicy policy, CancellationToken cancellationToken)
    {
        var cutoff = UtcNow.AddDays(-policy.RetentionDays);
        return policy.DataCategory switch
        {
            "Web analytics" => (await db.WebAnalyticsEvents.AsNoTracking().Select(x => x.CreatedAt).ToListAsync(cancellationToken)).Count(x => x < cutoff),
            "Form submissions" => (await db.FormSubmissions.AsNoTracking().Select(x => x.CreatedAt).ToListAsync(cancellationToken)).Count(x => x < cutoff),
            "Comments" => (await db.Comments.AsNoTracking().Select(x => x.CreatedAt).ToListAsync(cancellationToken)).Count(x => x < cutoff),
            "Security audit" => (await db.SecurityAuditEvents.AsNoTracking().Select(x => x.CreatedAt).ToListAsync(cancellationToken)).Count(x => x < cutoff),
            _ => 0
        };
    }

    private async Task<PrivacyRequest> FindRequestAsync(Guid id, CancellationToken cancellationToken) =>
        await db.PrivacyRequests.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException("Privacy request not found.");

    private async Task AddIncidentUpdateInternalAsync(Guid id, string type, string notes, string actor, CancellationToken cancellationToken)
    {
        db.SecurityIncidentUpdates.Add(new() { SecurityIncidentId = id, UpdateType = type, Notes = notes, CreatedBy = actor });
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task AuditAsync(string action, Guid id, IReadOnlyDictionary<string, string?>? details, CancellationToken cancellationToken) =>
        securityAudit.RecordAsync(new(SecurityAuditCategories.SecurityOperations, action, SecurityAuditOutcomes.Succeeded,
            SecurityAuditSeverities.High, "PrivacyOperations", id.ToString(), details), cancellationToken);

    private static PrivacyRequestView Map(PrivacyRequest x) => new(x.Id, x.RequestNumber, x.RequestType, x.SubjectIdentifier, x.Status, x.ReceivedAt, x.DueAt, x.IdentityVerifiedAt, x.CompletedAt, x.DecisionNotes, x.DeletionExecutedAt);
    private static SecurityIncidentView Map(SecurityIncident x, IEnumerable<SecurityIncidentUpdate> updates) => new(x.Id, x.IncidentNumber, x.Title, x.Summary, x.Severity, x.Status, x.DetectedAt, x.BreachAwarenessAt, x.PersonalDataInvolved, x.EphiInvolved, x.RiskAssessment, x.RegulatorNotificationRequired, x.RegulatorNotificationDueAt, x.RegulatorNotifiedAt, x.ContainedAt, x.ResolvedAt, x.Owner, updates.Select(u => new IncidentUpdateView(u.CreatedAt, u.UpdateType, u.Notes, u.CreatedBy)).ToList());
}
