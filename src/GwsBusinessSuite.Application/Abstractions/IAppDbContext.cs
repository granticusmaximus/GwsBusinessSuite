using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Abstractions;

public interface IAppDbContext : IAsyncDisposable
{
    DbSet<BusinessApp> BusinessApps { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<WikiPage> WikiPages { get; }
    DbSet<CmsSite> CmsSites { get; }
    DbSet<CmsPage> CmsPages { get; }
    DbSet<MediaAsset> MediaAssets { get; }
    DbSet<FormSubmission> FormSubmissions { get; }
    DbSet<CmsPageRevision> CmsPageRevisions { get; }
    DbSet<AffiliateOffer> AffiliateOffers { get; }
    DbSet<DeploymentTarget> DeploymentTargets { get; }
    DbSet<SeoArticleDraft> SeoArticleDrafts { get; }
    DbSet<SeoArticleAffiliatePlacement> SeoArticleAffiliatePlacements { get; }
    DbSet<SeoArticleAffiliateInteraction> SeoArticleAffiliateInteractions { get; }
    DbSet<SeoArticleWorkflowEvent> SeoArticleWorkflowEvents { get; }
    DbSet<CjConnectorSettings> CjConnectorSettings { get; }
    DbSet<Article> Articles { get; }
    DbSet<ArticleAffiliatePlacement> ArticleAffiliatePlacements { get; }
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
