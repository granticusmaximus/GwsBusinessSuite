namespace GwsBusinessSuite.Application.Campaigns;

public sealed record EmailCampaignStepView(Guid Id, int StepOrder, string Subject, string Body, int DelayDays);

public sealed record EmailCampaignView(
    Guid Id,
    string Name,
    string Description,
    string Status,
    IReadOnlyList<EmailCampaignStepView> Steps,
    int ActiveEnrollmentCount,
    DateTimeOffset CreatedAt);

public sealed class EmailCampaignStepEditorModel
{
    public Guid? Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int DelayDays { get; set; }
}

public sealed class EmailCampaignEditorModel
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<EmailCampaignStepEditorModel> Steps { get; set; } = [];
}

public sealed record EmailCampaignEnrollmentView(
    Guid Id,
    Guid ContactId,
    string ContactName,
    string Status,
    int NextStepIndex,
    DateTimeOffset? NextSendAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset EnrolledAt);

public interface IEmailCampaignService
{
    Task<IReadOnlyList<EmailCampaignView>> ListCampaignsAsync(CancellationToken cancellationToken = default);
    Task<EmailCampaignView?> GetCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default);
    Task<EmailCampaignView> SaveCampaignAsync(EmailCampaignEditorModel editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default);
    Task<EmailCampaignView> SetCampaignStatusAsync(Guid campaignId, string status, string performedBy, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmailCampaignEnrollmentView>> ListEnrollmentsAsync(Guid campaignId, CancellationToken cancellationToken = default);

    // False (a no-op, not an error) if the contact is already enrolled, has globally
    // unsubscribed, or the campaign isn't Active - true only when a new enrollment was
    // actually created.
    Task<bool> EnrollContactAsync(Guid campaignId, Guid contactId, CancellationToken cancellationToken = default);

    // False for an unrecognized/tampered token. Sets Contact.UnsubscribedFromCampaignsAt and
    // cancels every active enrollment for that contact across every campaign.
    Task<bool> UnsubscribeByTokenAsync(string token, CancellationToken cancellationToken = default);

    // Sends every enrollment whose NextSendAt is due, advances or completes it, and logs the
    // result - called by the background sweep. Returns how many sends were attempted.
    Task<int> ProcessDueSendsAsync(CancellationToken cancellationToken = default);
}

public interface IEmailCampaignEmailSender
{
    Task SendStepAsync(string toEmail, string subject, string body, string unsubscribeUrl, CancellationToken cancellationToken = default);
}
