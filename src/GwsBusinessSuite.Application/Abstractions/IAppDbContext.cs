using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Abstractions;

public interface IAppDbContext : IAsyncDisposable
{
    DbSet<Contact> Contacts { get; }
    DbSet<WikiPage> WikiPages { get; }
    DbSet<CmsSite> CmsSites { get; }
    DbSet<CmsPage> CmsPages { get; }
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
    DbSet<CjConnectorSettings> CjConnectorSettings { get; }
    DbSet<SiteSettings> SiteSettings { get; }
    DbSet<Article> Articles { get; }
    DbSet<ArticleAffiliatePlacement> ArticleAffiliatePlacements { get; }
    DbSet<AppUser> AppUsers { get; }
    DbSet<WatchedTopic> WatchedTopics { get; }
    DbSet<NewsItem> NewsItems { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
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
