using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<WikiPage> WikiPages => Set<WikiPage>();
    public DbSet<CmsSite> CmsSites => Set<CmsSite>();
    public DbSet<CmsPage> CmsPages => Set<CmsPage>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<CmsPageRevision> CmsPageRevisions => Set<CmsPageRevision>();
    public DbSet<AffiliateOffer> AffiliateOffers => Set<AffiliateOffer>();
    public DbSet<SeoArticleDraft> SeoArticleDrafts => Set<SeoArticleDraft>();
    public DbSet<SeoArticleAffiliatePlacement> SeoArticleAffiliatePlacements => Set<SeoArticleAffiliatePlacement>();
    public DbSet<SeoArticleAffiliateInteraction> SeoArticleAffiliateInteractions => Set<SeoArticleAffiliateInteraction>();
    public DbSet<SeoArticleWorkflowEvent> SeoArticleWorkflowEvents => Set<SeoArticleWorkflowEvent>();
    public DbSet<CjConnectorSettings> CjConnectorSettings => Set<CjConnectorSettings>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<ArticleAffiliatePlacement> ArticleAffiliatePlacements => Set<ArticleAffiliatePlacement>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<WatchedTopic> WatchedTopics => Set<WatchedTopic>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WikiPage>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<CmsSite>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<CmsPage>().HasIndex(x => new { x.SiteId, x.Slug }).IsUnique();
        modelBuilder.Entity<FormSubmission>().HasIndex(x => new { x.PageId, x.CreatedAt });
        modelBuilder.Entity<CmsPageRevision>().HasIndex(x => new { x.PageId, x.RevisionNumber }).IsUnique();
        modelBuilder.Entity<SeoArticleDraft>().HasIndex(x => x.Status);
        modelBuilder.Entity<SeoArticleAffiliatePlacement>()
            .HasOne(x => x.Draft)
            .WithMany(x => x.AffiliatePlacements)
            .HasForeignKey(x => x.SeoArticleDraftId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SeoArticleAffiliatePlacement>().HasIndex(x => new { x.SeoArticleDraftId, x.SortOrder });
        modelBuilder.Entity<SeoArticleAffiliateInteraction>().HasIndex(x => new { x.AdvertiserId, x.EventType });
        modelBuilder.Entity<SeoArticleAffiliateInteraction>().HasIndex(x => new { x.SeoArticleDraftId, x.SlotToken, x.CreatedAt });
        modelBuilder.Entity<SeoArticleWorkflowEvent>()
            .HasOne(x => x.Draft)
            .WithMany(x => x.WorkflowEvents)
            .HasForeignKey(x => x.SeoArticleDraftId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SeoArticleWorkflowEvent>().HasIndex(x => new { x.SeoArticleDraftId, x.CreatedAt });

        modelBuilder.Entity<Article>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<Article>().HasIndex(x => x.Status);
        modelBuilder.Entity<Article>().HasIndex(x => x.PublishedAt);
        modelBuilder.Entity<ArticleAffiliatePlacement>()
            .HasOne(x => x.Article)
            .WithMany(x => x.AffiliatePlacements)
            .HasForeignKey(x => x.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ArticleAffiliatePlacement>().HasIndex(x => new { x.ArticleId, x.SortOrder });

        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(x => x.Role);

        modelBuilder.Entity<NewsItem>().HasIndex(x => new { x.TopicId, x.FetchedAt });
        modelBuilder.Entity<NewsItem>().HasIndex(x => x.Url);
    }
}
