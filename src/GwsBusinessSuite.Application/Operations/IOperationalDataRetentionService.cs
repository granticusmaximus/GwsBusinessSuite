namespace GwsBusinessSuite.Application.Operations;

// Separate from IPrivacyOperationsService.PurgeEligibleRecordsAsync on purpose: that one
// purges compliance-driven, admin-configurable "privacy categories" (web analytics, form
// submissions, comments) governed by PrivacyRetentionPolicy rows. The tables here are plain
// operational/log-style history (automation runs, social-post alerts, live show recordings,
// app-generation transcripts, news headlines, affiliate commission records, podcast resume
// positions) that just grow forever with no admin-facing retention concept at all - fixed,
// configuration-driven defaults are the right fit, not another PrivacyRetentionPolicy row
// per table.
public interface IOperationalDataRetentionService
{
    Task<int> PurgeExpiredRecordsAsync(CancellationToken cancellationToken = default);
}
