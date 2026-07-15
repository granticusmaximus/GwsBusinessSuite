using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GwsBusinessSuite.Infrastructure.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options), IAppDbContext
{
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Database.BeginTransactionAsync(cancellationToken);

    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactActivity> ContactActivities => Set<ContactActivity>();
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
    public DbSet<ArticleAffiliateClick> ArticleAffiliateClicks => Set<ArticleAffiliateClick>();
    public DbSet<CjCommissionRecord> CjCommissionRecords => Set<CjCommissionRecord>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<WatchedTopic> WatchedTopics => Set<WatchedTopic>();
    public DbSet<NewsItem> NewsItems => Set<NewsItem>();
    public DbSet<PodcastShow> PodcastShows => Set<PodcastShow>();
    public DbSet<PodcastEpisode> PodcastEpisodes => Set<PodcastEpisode>();
    public DbSet<CmsKnowledgeSource> CmsKnowledgeSources => Set<CmsKnowledgeSource>();
    public DbSet<CmsKnowledgeEntry> CmsKnowledgeEntries => Set<CmsKnowledgeEntry>();
    public DbSet<AppGenerationRequest> AppGenerationRequests => Set<AppGenerationRequest>();
    public DbSet<AppGenerationMessage> AppGenerationMessages => Set<AppGenerationMessage>();
    public DbSet<LiveShowSession> LiveShowSessions => Set<LiveShowSession>();
    public DbSet<LiveShowRecording> LiveShowRecordings => Set<LiveShowRecording>();
    public DbSet<PodcastListenProgress> PodcastListenProgresses => Set<PodcastListenProgress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>().HasIndex(x => x.Status);
        modelBuilder.Entity<Contact>().HasIndex(x => x.TrashedAt);
        modelBuilder.Entity<ContactActivity>()
            .HasOne<Contact>()
            .WithMany()
            .HasForeignKey(x => x.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ContactActivity>().HasIndex(x => new { x.ContactId, x.CreatedAt });

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

        modelBuilder.Entity<ArticleAffiliateClick>()
            .HasOne<Article>()
            .WithMany()
            .HasForeignKey(x => x.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ArticleAffiliateClick>().HasIndex(x => new { x.PlacementId, x.CreatedAt });
        modelBuilder.Entity<ArticleAffiliateClick>().HasIndex(x => new { x.AdvertiserId, x.CreatedAt });

        modelBuilder.Entity<CjCommissionRecord>().HasIndex(x => x.ExternalId).IsUnique();
        modelBuilder.Entity<CjCommissionRecord>().HasIndex(x => x.AdvertiserId);

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

        modelBuilder.Entity<CmsKnowledgeSource>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<CmsKnowledgeEntry>().HasIndex(x => x.SourceId);
        SeedCmsKnowledge(modelBuilder);

        modelBuilder.Entity<AppGenerationRequest>().HasIndex(x => x.Status);
        modelBuilder.Entity<AppGenerationRequest>().HasIndex(x => x.TargetSiteId);
        modelBuilder.Entity<AppGenerationMessage>()
            .HasOne(x => x.Request)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.AppGenerationRequestId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AppGenerationMessage>().HasIndex(x => new { x.AppGenerationRequestId, x.CreatedAt });

        modelBuilder.Entity<LiveShowSession>().HasIndex(x => x.Status);
        modelBuilder.Entity<LiveShowSession>().HasIndex(x => x.InviteToken).IsUnique();
        modelBuilder.Entity<LiveShowRecording>()
            .HasOne<LiveShowSession>()
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LiveShowRecording>().HasIndex(x => x.SessionId);

        modelBuilder.Entity<PodcastListenProgress>()
            .HasOne<PodcastEpisode>()
            .WithMany()
            .HasForeignKey(x => x.EpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PodcastListenProgress>().HasIndex(x => new { x.Username, x.EpisodeId }).IsUnique();
    }

    // Migrates the module's original hardcoded in-memory reference data (2 sources, 5
    // entries) into the database as the initial seed, so wiring it up to real
    // persistence doesn't lose the existing content. CreatedAt/CreatedBy must be
    // literal (not DateTimeOffset.UtcNow) since HasData snapshots values at migration-
    // generation time.
    private static void SeedCmsKnowledge(ModelBuilder modelBuilder)
    {
        var seededAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string seededBy = "seed";

        var wpSourceId = new Guid("11111111-1111-1111-1111-111111111101");
        var elementorSourceId = new Guid("11111111-1111-1111-1111-111111111102");

        modelBuilder.Entity<CmsKnowledgeSource>().HasData(
            new CmsKnowledgeSource
            {
                Id = wpSourceId,
                Key = "wp-clean-room",
                Name = "WordPress Workflow Reference (Clean Room)",
                LicenseNotes = "Do not copy source code or proprietary assets. Reimplement behavior only.",
                UsageGuidance = "Use as product behavior inspiration for workflows, content modeling, and admin UX.",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeSource
            {
                Id = elementorSourceId,
                Key = "elementor-clean-room",
                Name = "Elementor Workflow Reference (Clean Room)",
                LicenseNotes = "Do not clone protected UI/brand assets. Build original controls and layouts.",
                UsageGuidance = "Use as inspiration for visual editing flow, section nesting, and style controls.",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            });

        modelBuilder.Entity<CmsKnowledgeEntry>().HasData(
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222201"),
                SourceId = wpSourceId,
                Capability = "Template hierarchy and routing",
                WorkflowSummary = "Resolve route to the best matching template with fallback layers.",
                ImplementationHint = "Model template precedence in application logic and store template metadata separately from page content.",
                SuggestedBlocksCsv = "template-slot,dynamic-region,route-layout",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222202"),
                SourceId = wpSourceId,
                Capability = "Content revision workflow",
                WorkflowSummary = "Draft, review, approve, and publish content versions with audit history.",
                ImplementationHint = "Store immutable revisions and transition events so publish rollback remains safe.",
                SuggestedBlocksCsv = "revision-timeline,approval-gate,publish-status",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222203"),
                SourceId = elementorSourceId,
                Capability = "Visual section/column composition",
                WorkflowSummary = "Construct pages from nested sections, columns, and widget blocks.",
                ImplementationHint = "Use JSON schema versioning for block trees and validate depth/width constraints.",
                SuggestedBlocksCsv = "section,column,widget-container",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222204"),
                SourceId = elementorSourceId,
                Capability = "Responsive style controls",
                WorkflowSummary = "Define per-breakpoint spacing, typography, and visibility controls.",
                ImplementationHint = "Store style settings as breakpoint maps with a deterministic fallback chain.",
                SuggestedBlocksCsv = "responsive-style,breakpoint-rule,visibility-toggle",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222205"),
                SourceId = wpSourceId,
                Capability = "Plugin-like extension points",
                WorkflowSummary = "Allow modular feature packs to register capabilities without core rewrites.",
                ImplementationHint = "Add capability registration contracts and sandbox execution boundaries.",
                SuggestedBlocksCsv = "extension-hook,capability-registration,feature-toggle",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            });

        SeedMoreCmsKnowledge(modelBuilder, wpSourceId, elementorSourceId, seededAt, seededBy);
    }

    // Second seeding pass (Phase 5 "Big Vision" scoping round) - more clean-room behavioral
    // notes on commonly-requested WordPress/Elementor capabilities this app doesn't have yet,
    // covering gaps identified when picking "dynamic content widgets" as the first one to
    // actually build (see the posts-grid widget in CmsBlockHtmlRenderer.cs). Same rules as
    // the original 5: describe behavior/workflow only, never copy source or proprietary
    // assets - see each source's LicenseNotes.
    private static void SeedMoreCmsKnowledge(
        ModelBuilder modelBuilder, Guid wpSourceId, Guid elementorSourceId, DateTimeOffset seededAt, string seededBy)
    {
        modelBuilder.Entity<CmsKnowledgeEntry>().HasData(
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222206"),
                SourceId = wpSourceId,
                Capability = "Dynamic content loops",
                WorkflowSummary = "Pull a filtered, ordered, live list of content items into a page instead of hand-placing each one.",
                ImplementationHint = "Parameterize source/filter/sort/limit on the widget and re-query at render time so the block always reflects current data - never bake the list into stored page content.",
                SuggestedBlocksCsv = "posts-grid,query-loop,content-source-picker",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222207"),
                SourceId = elementorSourceId,
                Capability = "Popup builder with trigger rules",
                WorkflowSummary = "A modal with its own mini layout tree, shown based on a trigger condition (page load delay, exit intent, click, scroll depth) rather than being embedded inline in the page.",
                ImplementationHint = "Model a popup as its own small layout document plus a separate trigger-rule record, and render it through the same block renderer used for regular pages so widget support stays in sync automatically.",
                SuggestedBlocksCsv = "popup,trigger-rule,modal-overlay",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222208"),
                SourceId = wpSourceId,
                Capability = "Custom fields / structured metadata",
                WorkflowSummary = "Attach arbitrary typed key-value fields to a content item beyond its fixed schema, without a database migration per field.",
                ImplementationHint = "Store as a JSON dictionary column with a lightweight per-content-type field-definition list, so new fields stay both renderable and editable without ad hoc columns.",
                SuggestedBlocksCsv = "custom-field,field-schema,meta-panel",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-222222222209"),
                SourceId = elementorSourceId,
                Capability = "Theme builder: reusable header/footer templates",
                WorkflowSummary = "Define a header and footer once per site, edited like any other block layout, and have every page pull from it instead of each page carrying its own nav rendering.",
                ImplementationHint = "Model header/footer as their own special-purpose layout documents stored per-site and merged into the page at render time, ahead of/after the page's own sections.",
                SuggestedBlocksCsv = "site-header,site-footer,template-part",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-22222222020a"),
                SourceId = wpSourceId,
                Capability = "Widget areas / sidebars",
                WorkflowSummary = "Named, swappable content regions that live outside the main page flow (e.g. a blog sidebar) and can hold any widget independent of the page's own content.",
                ImplementationHint = "Define named regions per template/site, and let the same widget vocabulary used for page content be dropped into a region - don't invent a parallel widget system just for regions.",
                SuggestedBlocksCsv = "widget-area,sidebar-region,region-slot",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            },
            new CmsKnowledgeEntry
            {
                Id = new Guid("22222222-2222-2222-2222-22222222020b"),
                SourceId = elementorSourceId,
                Capability = "Conditional display rules",
                WorkflowSummary = "Show or hide a section/widget based on device size, logged-in role, or a date range, rather than it always rendering.",
                ImplementationHint = "Attach a small rule set to a layout node evaluated at render time, defaulting to \"always visible\" so existing content renders unchanged when no rule is set.",
                SuggestedBlocksCsv = "display-condition,role-visibility,scheduled-visibility",
                CreatedAt = seededAt,
                CreatedBy = seededBy
            });
    }
}
