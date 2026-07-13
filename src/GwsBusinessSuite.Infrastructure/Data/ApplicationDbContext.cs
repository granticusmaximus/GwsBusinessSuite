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
    public DbSet<CmsPageCategory> CmsPageCategories => Set<CmsPageCategory>();
    public DbSet<GlobalBlock> GlobalBlocks => Set<GlobalBlock>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<DockerHealthAlert> DockerHealthAlerts => Set<DockerHealthAlert>();
    public DbSet<DockerActionLog> DockerActionLogs => Set<DockerActionLog>();
    public DbSet<DigitalOceanSettings> DigitalOceanSettings => Set<DigitalOceanSettings>();
    public DbSet<CmsPageRevision> CmsPageRevisions => Set<CmsPageRevision>();
    public DbSet<AffiliateOffer> AffiliateOffers => Set<AffiliateOffer>();
    public DbSet<SeoArticleDraft> SeoArticleDrafts => Set<SeoArticleDraft>();
    public DbSet<SeoArticleAffiliatePlacement> SeoArticleAffiliatePlacements => Set<SeoArticleAffiliatePlacement>();
    public DbSet<SeoArticleAffiliateInteraction> SeoArticleAffiliateInteractions => Set<SeoArticleAffiliateInteraction>();
    public DbSet<SeoArticleWorkflowEvent> SeoArticleWorkflowEvents => Set<SeoArticleWorkflowEvent>();
    public DbSet<SeoArticleDraftRevision> SeoArticleDraftRevisions => Set<SeoArticleDraftRevision>();
    public DbSet<CjConnectorSettings> CjConnectorSettings => Set<CjConnectorSettings>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<ArticleCategory> ArticleCategories => Set<ArticleCategory>();
    public DbSet<ArticleAffiliatePlacement> ArticleAffiliatePlacements => Set<ArticleAffiliatePlacement>();
    public DbSet<ArticleAffiliateSuggestion> ArticleAffiliateSuggestions => Set<ArticleAffiliateSuggestion>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<WatchedTopic> WatchedTopics => Set<WatchedTopic>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();
    public DbSet<PodcastShow> PodcastShows => Set<PodcastShow>();
    public DbSet<PodcastEpisode> PodcastEpisodes => Set<PodcastEpisode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WikiPage>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<CmsSite>().HasIndex(x => x.Slug).IsUnique();
        // Slugs are unique per parent, not per site — /services/pricing and
        // /products/pricing can coexist since their full paths differ.
        modelBuilder.Entity<CmsPage>().HasIndex(x => new { x.SiteId, x.ParentPageId, x.Slug }).IsUnique();
        modelBuilder.Entity<CmsPage>().HasIndex(x => x.CategoryId);
        modelBuilder.Entity<CmsPage>()
            .HasOne<CmsPageCategory>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CmsPageCategory>().HasIndex(x => new { x.SiteId, x.Slug }).IsUnique();
        modelBuilder.Entity<GlobalBlock>().HasIndex(x => new { x.SiteId, x.Kind, x.Name });
        modelBuilder.Entity<FormSubmission>().HasIndex(x => new { x.PageId, x.CreatedAt });
        modelBuilder.Entity<Comment>().HasIndex(x => new { x.ArticleId, x.Status });
        modelBuilder.Entity<Comment>()
            .HasOne<Comment>()
            .WithMany()
            .HasForeignKey(x => x.ParentCommentId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<DockerHealthAlert>().HasIndex(x => new { x.ContainerName, x.IsRead });
        modelBuilder.Entity<DockerActionLog>().HasIndex(x => new { x.ContainerName, x.CreatedAt });
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
        modelBuilder.Entity<SeoArticleDraftRevision>()
            .HasOne(x => x.Draft)
            .WithMany(x => x.Revisions)
            .HasForeignKey(x => x.SeoArticleDraftId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SeoArticleDraftRevision>()
            .HasIndex(x => new { x.SeoArticleDraftId, x.VersionNumber })
            .IsUnique();

        modelBuilder.Entity<Article>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<Article>().HasIndex(x => x.Status);
        modelBuilder.Entity<Article>().HasIndex(x => x.PublishedAt);
        modelBuilder.Entity<Article>().HasIndex(x => x.CategoryId);
        modelBuilder.Entity<Article>()
            .HasOne<ArticleCategory>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ArticleCategory>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<ArticleAffiliatePlacement>()
            .HasOne(x => x.Article)
            .WithMany(x => x.AffiliatePlacements)
            .HasForeignKey(x => x.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ArticleAffiliatePlacement>().HasIndex(x => new { x.ArticleId, x.SortOrder });

        modelBuilder.Entity<ArticleAffiliateSuggestion>()
            .HasOne(x => x.Article)
            .WithMany()
            .HasForeignKey(x => x.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ArticleAffiliateSuggestion>().HasIndex(x => new { x.ArticleId, x.Status });

        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(x => x.Role);

        modelBuilder.Entity<NewsItem>().HasIndex(x => new { x.TopicId, x.FetchedAt });
        modelBuilder.Entity<NewsItem>().HasIndex(x => x.Url);

        modelBuilder.Entity<PodcastShow>().HasIndex(x => x.Category);
        modelBuilder.Entity<PodcastShow>().HasIndex(x => x.ItunesId);
        modelBuilder.Entity<PodcastShow>().HasIndex(x => x.FeedUrl);
        modelBuilder.Entity<PodcastEpisode>()
            .HasOne<PodcastShow>()
            .WithMany()
            .HasForeignKey(x => x.PodcastShowId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PodcastEpisode>().HasIndex(x => new { x.PodcastShowId, x.PublishedAt });
        modelBuilder.Entity<PodcastEpisode>().HasIndex(x => new { x.PodcastShowId, x.ExternalId });
    }
}
