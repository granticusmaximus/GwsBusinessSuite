using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<BusinessApp> BusinessApps { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<WikiPage> WikiPages { get; }
    DbSet<CmsSite> CmsSites { get; }
    DbSet<CmsPage> CmsPages { get; }
    DbSet<AffiliateOffer> AffiliateOffers { get; }
    DbSet<DeploymentTarget> DeploymentTargets { get; }
    DbSet<SeoArticleDraft> SeoArticleDrafts { get; }
    DbSet<SeoArticleAffiliatePlacement> SeoArticleAffiliatePlacements { get; }
    DbSet<SeoArticleAffiliateInteraction> SeoArticleAffiliateInteractions { get; }
    DbSet<SeoArticleWorkflowEvent> SeoArticleWorkflowEvents { get; }
    DbSet<CjConnectorSettings> CjConnectorSettings { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
