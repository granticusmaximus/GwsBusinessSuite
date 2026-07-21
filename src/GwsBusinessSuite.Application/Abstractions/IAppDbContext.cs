using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GwsBusinessSuite.Application.Abstractions;

public interface IAppDbContext : IAsyncDisposable
{
    DbSet<Contact> Contacts { get; }
    DbSet<ContactActivity> ContactActivities { get; }
    DbSet<WikiPage> WikiPages { get; }
    DbSet<WikiPageRevision> WikiPageRevisions { get; }
    DbSet<SentinelNavigationEntry> SentinelNavigationEntries { get; }
    DbSet<SentinelDiscussion> SentinelDiscussions { get; }
    DbSet<SentinelDiscussionComment> SentinelDiscussionComments { get; }
    DbSet<SentinelDiscussionReaction> SentinelDiscussionReactions { get; }
    DbSet<SentinelNotification> SentinelNotifications { get; }
    DbSet<WikiDatabase> WikiDatabases { get; }
    DbSet<WikiDatabaseProperty> WikiDatabaseProperties { get; }
    DbSet<WikiDatabaseRow> WikiDatabaseRows { get; }
    DbSet<WikiDatabaseView> WikiDatabaseViews { get; }
    DbSet<CmsSite> CmsSites { get; }
    DbSet<CmsPage> CmsPages { get; }
    DbSet<CmsPageCategory> CmsPageCategories { get; }
    DbSet<GlobalBlock> GlobalBlocks { get; }
    DbSet<MediaAsset> MediaAssets { get; }
    DbSet<FormSubmission> FormSubmissions { get; }
    DbSet<Comment> Comments { get; }
    DbSet<DockerHealthAlert> DockerHealthAlerts { get; }
    DbSet<DockerActionLog> DockerActionLogs { get; }
    DbSet<DigitalOceanSettings> DigitalOceanSettings { get; }
    DbSet<CmsPageRevision> CmsPageRevisions { get; }
    DbSet<AffiliateOffer> AffiliateOffers { get; }
    DbSet<SeoArticleDraft> SeoArticleDrafts { get; }
    DbSet<SeoArticleAffiliatePlacement> SeoArticleAffiliatePlacements { get; }
    DbSet<SeoArticleAffiliateInteraction> SeoArticleAffiliateInteractions { get; }
    DbSet<SeoArticleWorkflowEvent> SeoArticleWorkflowEvents { get; }
    DbSet<SeoArticleDraftRevision> SeoArticleDraftRevisions { get; }
    DbSet<CjConnectorSettings> CjConnectorSettings { get; }
    DbSet<NotionConnectorSettings> NotionConnectorSettings { get; }
    DbSet<SiteSettings> SiteSettings { get; }
    DbSet<Article> Articles { get; }
    DbSet<ArticleAffiliatePlacement> ArticleAffiliatePlacements { get; }
    DbSet<ArticleAffiliateRotation> ArticleAffiliateRotations { get; }
    DbSet<ArticleAffiliateSuggestion> ArticleAffiliateSuggestions { get; }
    DbSet<ArticleAffiliateClick> ArticleAffiliateClicks { get; }
    DbSet<CjCommissionRecord> CjCommissionRecords { get; }
    DbSet<AppUser> AppUsers { get; }
    DbSet<WatchedTopic> WatchedTopics { get; }
    DbSet<NewsItem> NewsItems { get; }
    DbSet<PodcastShow> PodcastShows { get; }
    DbSet<PodcastEpisode> PodcastEpisodes { get; }
    DbSet<CmsKnowledgeSource> CmsKnowledgeSources { get; }
    DbSet<CmsKnowledgeEntry> CmsKnowledgeEntries { get; }
    DbSet<AppGenerationRequest> AppGenerationRequests { get; }
    DbSet<AppGenerationMessage> AppGenerationMessages { get; }
    DbSet<LiveShowSession> LiveShowSessions { get; }
    DbSet<LiveShowRecording> LiveShowRecordings { get; }
    DbSet<PodcastListenProgress> PodcastListenProgresses { get; }
    DbSet<AutomationWorkflow> AutomationWorkflows { get; }
    DbSet<AutomationNode> AutomationNodes { get; }
    DbSet<AutomationConnection> AutomationConnections { get; }
    DbSet<AutomationWorkflowVersion> AutomationWorkflowVersions { get; }
    DbSet<AutomationCredential> AutomationCredentials { get; }
    DbSet<AutomationExecution> AutomationExecutions { get; }
    DbSet<AutomationNodeExecution> AutomationNodeExecutions { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    // For multi-step writes that must be all-or-nothing (e.g. AppGenerationService.ApproveAsync
    // creating several CmsPages) - most callers never need this, since a single SaveChangesAsync
    // is already atomic on its own.
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates standalone <see cref="IAppDbContext"/> instances that are not tied to the
/// caller's DI scope. Use this for work that spans a long-running external call (e.g. an
/// Ollama generation request) so a Blazor Server circuit disconnecting mid-call can't
/// dispose the context out from under a pending save.
/// </summary>
public interface IAppDbContextFactory
{
    Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}
