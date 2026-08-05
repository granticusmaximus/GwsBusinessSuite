using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Privacy;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class PrivacyOperationsService(
    IAppDbContext db,
    ICurrentUserAccessor currentUser,
    ISecurityAuditService securityAudit,
    TimeProvider timeProvider) : IPrivacyOperationsService
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
        bool erasureDataDeletionConfirmed = false, CancellationToken cancellationToken = default)
    {
        if (status is not (PrivacyRequestStatuses.Fulfilled or PrivacyRequestStatuses.Denied or PrivacyRequestStatuses.InReview))
            throw new ArgumentException("Unsupported completion status.");
        var entity = await FindRequestAsync(requestId, cancellationToken);
        if (entity.IdentityVerifiedAt is null) throw new InvalidOperationException("Identity must be verified before a request can be decided.");
        if (status == PrivacyRequestStatuses.Denied && string.IsNullOrWhiteSpace(decisionNotes))
            throw new ArgumentException("A denial reason is required.");
        // Erasure has no real cascading-deletion implementation anywhere in this app - deletion
        // happens manually, off-platform. Without this gate, "Fulfilled" was assertable from the
        // same generic status dropdown used for Access/Correction/Restriction, so the compliance
        // record could claim data was erased when nothing had actually been deleted.
        if (entity.RequestType == PrivacyRequestTypes.Erasure && status == PrivacyRequestStatuses.Fulfilled && !erasureDataDeletionConfirmed)
        {
            throw new InvalidOperationException(
                "Erasure requests can only be marked Fulfilled after confirming the subject's data has actually been deleted across all systems.");
        }
        entity.Status = status; entity.DecisionNotes = decisionNotes.Trim();
        entity.CompletedAt = status is PrivacyRequestStatuses.Fulfilled or PrivacyRequestStatuses.Denied ? UtcNow : null;
        entity.UpdatedAt = UtcNow; entity.UpdatedBy = await currentUser.GetCurrentUsernameAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var auditDetails = new Dictionary<string, string?> { ["status"] = status };
        if (entity.RequestType == PrivacyRequestTypes.Erasure)
            auditDetails["erasureDataDeletionConfirmed"] = erasureDataDeletionConfirmed.ToString();
        await AuditAsync("PrivacyRequestStatusChanged", entity.Id, auditDetails, cancellationToken);
    }

    public async Task<SubjectDataExport> ExportSubjectDataAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await FindRequestAsync(requestId, cancellationToken);
        if (request.RequestType != PrivacyRequestTypes.Access || request.IdentityVerifiedAt is null)
            throw new InvalidOperationException("Only identity-verified access requests can be exported.");
        var subject = request.SubjectIdentifier;
        var users = await db.AppUsers.AsNoTracking().Where(x => x.Username == subject)
            .Select(x => new { x.Id, x.Username, x.Role, x.IsActive, x.MfaEnabled, x.MfaEnrolledAt, x.CreatedAt, x.UpdatedAt }).ToListAsync(cancellationToken);
        var contacts = await db.Contacts.AsNoTracking().Where(x => x.Email == subject)
            .Select(x => new { x.Id, x.FullName, x.Email, x.Company, x.Status, x.CreatedAt, x.UpdatedAt }).ToListAsync(cancellationToken);
        var comments = await db.Comments.AsNoTracking().Where(x => x.AuthorEmail == subject)
            .Select(x => new { x.Id, x.ArticleId, x.AuthorName, x.AuthorEmail, x.Body, x.Status, x.CreatedAt }).ToListAsync(cancellationToken);
        var aiRuns = await db.SentinelAiRuns.AsNoTracking().Where(x => x.CreatedBy == subject)
            .Select(x => new { x.Id, x.ConversationId, x.Action, x.Instruction, x.Output, x.Status, x.Model, x.CreatedAt }).ToListAsync(cancellationToken);
        var listening = await db.PodcastListenProgresses.AsNoTracking().Where(x => x.Username == subject)
            .Select(x => new { x.Id, x.EpisodeId, x.PositionSeconds, x.IsCompleted, x.LastPlayedAt }).ToListAsync(cancellationToken);
        var auditEvents = await db.SecurityAuditEvents.AsNoTracking().Where(x => x.ActorUsername == subject || x.TargetId == subject)
            .Select(x => new { x.Id, x.OccurredAtUnixSeconds, x.Category, x.Action, x.Outcome, x.TargetType, x.TargetId }).ToListAsync(cancellationToken);
        var payload = new { generatedAt = UtcNow, request = request.RequestNumber, subject, users, contacts, comments, sentinelGpt = aiRuns, podcastProgress = listening, securityEvents = auditEvents };
        var content = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
        await AuditAsync("SubjectDataExported", request.Id,
            new Dictionary<string, string?> { ["requestNumber"] = request.RequestNumber }, cancellationToken);
        return new($"gws-subject-export-{request.RequestNumber}.json", content);
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

    private static PrivacyRequestView Map(PrivacyRequest x) => new(x.Id, x.RequestNumber, x.RequestType, x.SubjectIdentifier, x.Status, x.ReceivedAt, x.DueAt, x.IdentityVerifiedAt, x.CompletedAt, x.DecisionNotes);
    private static SecurityIncidentView Map(SecurityIncident x, IEnumerable<SecurityIncidentUpdate> updates) => new(x.Id, x.IncidentNumber, x.Title, x.Summary, x.Severity, x.Status, x.DetectedAt, x.BreachAwarenessAt, x.PersonalDataInvolved, x.EphiInvolved, x.RiskAssessment, x.RegulatorNotificationRequired, x.RegulatorNotificationDueAt, x.RegulatorNotifiedAt, x.ContainedAt, x.ResolvedAt, x.Owner, updates.Select(u => new IncidentUpdateView(u.CreatedAt, u.UpdateType, u.Notes, u.CreatedBy)).ToList());
}
