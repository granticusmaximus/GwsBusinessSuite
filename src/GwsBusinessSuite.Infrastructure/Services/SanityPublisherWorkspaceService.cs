using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.SanityPublishing;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SanityPublisherWorkspaceService(
    IAppDbContext dbContext,
    IOptions<SanityOptions> sanityOptions) : ISanityPublisherWorkspaceService
{
    public async Task<SanityPublisherWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var drafts = await dbContext.SeoArticleDrafts
            .AsNoTracking()
            .Select(draft => new
            {
                draft.Id,
                draft.Title,
                draft.Topic,
                draft.Status,
                draft.RevisionNumber,
                draft.CreatedAt,
                draft.UpdatedAt,
                draft.ApprovedAt
            })
            .ToListAsync(cancellationToken);

        var draftIds = drafts.Select(draft => draft.Id).ToList();

        var workflowEvents = draftIds.Count == 0
            ? new List<SeoArticleWorkflowEvent>()
            : await dbContext.SeoArticleWorkflowEvents
                .AsNoTracking()
                .Where(evt => draftIds.Contains(evt.SeoArticleDraftId))
                .ToListAsync(cancellationToken);

        var placementCounts = draftIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await dbContext.SeoArticleAffiliatePlacements
                .AsNoTracking()
                .Where(placement => draftIds.Contains(placement.SeoArticleDraftId))
                .GroupBy(placement => placement.SeoArticleDraftId)
                .Select(group => new { DraftId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.DraftId, item => item.Count, cancellationToken);

        var summaries = drafts
            .Select(draft =>
            {
                var lastPublishedEvent = workflowEvents
                    .Where(evt =>
                        evt.SeoArticleDraftId == draft.Id &&
                        IsSanityBackupEvent(evt.EventType))
                    .OrderByDescending(evt => evt.CreatedAt)
                    .FirstOrDefault();

                var isApproved = string.Equals(draft.Status, SeoArticleDraftStatuses.Approved, StringComparison.OrdinalIgnoreCase);
                var hasBeenPublished = lastPublishedEvent is not null;
                var effectiveUpdatedAt = draft.UpdatedAt ?? draft.CreatedAt;
                var needsRepublish = isApproved && (!hasBeenPublished || effectiveUpdatedAt > lastPublishedEvent!.CreatedAt);

                var publishState = !isApproved
                    ? "Blocked"
                    : needsRepublish
                        ? hasBeenPublished ? "Needs Backup" : "Ready"
                        : "Backed Up";

                var publishDetail = publishState switch
                {
                    "Blocked" => "Approve the draft in Content Studio before sending a backup copy to Sanity.",
                    "Needs Backup" => "Draft changed after the last Sanity backup and should be backed up again.",
                    "Ready" => "Approved and waiting for the first Sanity backup.",
                    _ => "Latest approved revision already has a matching Sanity backup."
                };

                return new SanityPublishingDraftSummary
                {
                    DraftId = draft.Id,
                    Title = draft.Title,
                    Topic = draft.Topic,
                    Status = draft.Status,
                    RevisionNumber = draft.RevisionNumber,
                    CreatedAt = draft.CreatedAt,
                    UpdatedAt = draft.UpdatedAt,
                    ApprovedAt = draft.ApprovedAt,
                    LastPublishedAt = lastPublishedEvent?.CreatedAt,
                    PublishState = publishState,
                    PublishStateDetail = publishDetail,
                    CanPublish = isApproved,
                    AffiliatePlacementCount = placementCounts.TryGetValue(draft.Id, out var count) ? count : 0
                };
            })
            .OrderByDescending(summary => summary.UpdatedAt ?? summary.CreatedAt)
            .ToList();

        return new SanityPublisherWorkspaceSnapshot
        {
            Configuration = BuildConfigurationStatus(sanityOptions.Value),
            TotalDrafts = summaries.Count,
            PublicationQueue = summaries
                .Where(summary =>
                    string.Equals(summary.PublishState, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(summary.PublishState, "Needs Backup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(summary => summary.ApprovedAt ?? summary.UpdatedAt ?? summary.CreatedAt)
                .ToList(),
            RecentlyPublished = summaries
                .Where(summary => summary.LastPublishedAt.HasValue)
                .OrderByDescending(summary => summary.LastPublishedAt)
                .Take(10)
                .ToList()
        };
    }

    private static SanityConfigurationStatus BuildConfigurationStatus(SanityOptions options)
    {
        var hasProjectId = !string.IsNullOrWhiteSpace(options.ProjectId);
        var hasDataset = !string.IsNullOrWhiteSpace(options.Dataset);
        var hasToken = !string.IsNullOrWhiteSpace(options.Token);
        var isReady = hasProjectId && hasDataset && hasToken && !string.IsNullOrWhiteSpace(options.DocumentType);

        var message = isReady
            ? "Sanity backup is configured and ready when you want an external copy of an approved draft."
            : "Set Sanity project ID, dataset, token, and document type in configuration before creating backups.";

        return new SanityConfigurationStatus
        {
            IsReady = isReady,
            ProjectId = options.ProjectId,
            Dataset = options.Dataset,
            DocumentType = options.DocumentType,
            DocumentIdPrefix = options.DocumentIdPrefix,
            AutoPublishOnApproval = options.AutoPublishOnApproval,
            Message = message
        };
    }

    private static bool IsSanityBackupEvent(string eventType)
    {
        return string.Equals(eventType, SeoArticleWorkflowEventTypes.BackedUpToSanity, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(eventType, SeoArticleWorkflowEventTypes.PublishedToSanity, StringComparison.OrdinalIgnoreCase);
    }
}
