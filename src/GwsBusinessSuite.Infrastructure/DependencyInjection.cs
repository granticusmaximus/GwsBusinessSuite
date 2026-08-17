using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Application.BusinessIntelligence;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.AffiliateRotations;
using GwsBusinessSuite.Application.AppGeneration;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.CmsKnowledge;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Application.CjAds;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.LiveShow;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Application.Podcasts;
using GwsBusinessSuite.Application.Privacy;
using GwsBusinessSuite.Application.Resume;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.SshTerminal;
using GwsBusinessSuite.Application.Users;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;

namespace GwsBusinessSuite.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=gws-suite.db";

        // Persist Data Protection keys to the data volume so encrypted values
        // (like the CJ developer key) survive Docker container rebuilds on deploy.
        var dpKeysPath = new System.IO.DirectoryInfo(
            configuration["DataProtection:KeysPath"] ?? "/app/data/dp-keys");
        services.AddDataProtection()
            .PersistKeysToFileSystem(dpKeysPath)
            .SetApplicationName("GwsBusinessSuite");

        services.Configure<ContentStudioOptions>(configuration.GetSection(ContentStudioOptions.SectionName));
        services.AddOptions<StripeBillingOptions>()
            .Bind(configuration.GetSection(StripeBillingOptions.SectionName));
        services.AddOptions<AnalyticsGeoIpOptions>()
            .Bind(configuration.GetSection(AnalyticsGeoIpOptions.SectionName));
        services.AddOptions<OllamaWebOptions>()
            .Bind(configuration.GetSection(OllamaWebOptions.SectionName))
            .Validate(options =>
                Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps,
                "OllamaWeb:BaseUrl must be an absolute HTTPS URL.")
            .Validate(options => options.MaxResults is >= 1 and <= 10,
                "OllamaWeb:MaxResults must be between 1 and 10.")
            .ValidateOnStart();
        services.AddOptions<SemanticSearchOptions>()
            .Bind(configuration.GetSection(SemanticSearchOptions.SectionName))
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Model),
                "SemanticSearch:Model is required when semantic search is enabled.")
            .Validate(options => options.BatchSize is >= 1 and <= 50,
                "SemanticSearch:BatchSize must be between 1 and 50.")
            .Validate(options => options.ReconciliationMinutes is >= 1 and <= 1440,
                "SemanticSearch:ReconciliationMinutes must be between 1 and 1440.")
            .Validate(options => options.SimilarityThreshold is >= -1 and <= 1,
                "SemanticSearch:SimilarityThreshold must be between -1 and 1.")
            .ValidateOnStart();

        services.TryAddSingleton<IPublicContentCacheInvalidator, NoOpPublicContentCacheInvalidator>();
        services.AddSingleton<PublicContentCacheInvalidationInterceptor>();
        services.AddSingleton<SemanticIndexQueue>();
        services.AddSingleton<SemanticIndexSaveChangesInterceptor>();
        services.AddDbContextFactory<ApplicationDbContext>((serviceProvider, options) => options
            .UseSqlite(connectionString)
            .AddInterceptors(
                serviceProvider.GetRequiredService<PublicContentCacheInvalidationInterceptor>(),
                serviceProvider.GetRequiredService<SemanticIndexSaveChangesInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddScoped<IAppDbContextFactory, AppDbContextFactory>();
        services.AddScoped<IAdminPortalSummaryService, AdminPortalSummaryService>();
        services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
        // Non-web hosts and startup probes do not have an authenticated HTTP/circuit
        // accessor. The web app registers its CurrentUserAccessor afterward and becomes
        // the effective resolution there; background/minimal hosts safely use "unknown".
        services.TryAddScoped<ICurrentUserAccessor>(_ => FixedCurrentUserAccessor.Unknown);
        services.AddHttpClient<ICjAffiliateService, CjAffiliateService>(client =>
        {
            // Was previously unconfigured (silently inheriting HttpClient's 100s default)
            // rather than the ~20s bound used elsewhere in this file for third-party calls.
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<OllamaWorkloadScheduler>();
        services.AddSingleton<OllamaPerformanceTracker>();
        services.AddHttpClient<IOllamaService, OllamaService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContentStudioOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? ContentStudioOptions.DefaultBaseUrl
                : options.BaseUrl;

            client.BaseAddress = new Uri(baseUrl);
            // This is a generous outer safety net only. The actual per-call timeout can be
            // overridden per-site via Settings > AI without an app restart, so it's enforced
            // with a linked CancellationTokenSource around each generation call instead
            // (see ContentStudioService.GetEffectiveTimeoutAsync).
            client.Timeout = TimeSpan.FromHours(2);
        }).AddResilienceHandler("ollama-transient-retry", builder =>
        {
            // Only retries connection-level failures (Ollama not yet up, dropped socket,
            // DNS blip) that fail fast. A generation that's genuinely just slow runs for
            // up to client.Timeout above and is deliberately NOT retried here, since
            // retrying a multi-minute timeout would silently double the user's wait.
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is HttpRequestException),
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1)
            });
        });
        services.AddHostedService<OllamaModelWarmupBackgroundService>();
        services.AddHttpClient<IOllamaWebSearchService, OllamaWebSearchService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<OllamaWebOptions>>().Value;
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(options.BaseUrl)
                ? OllamaWebOptions.DefaultBaseUrl
                : options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(45);
        });
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<NewsRefreshState>();
        services.AddHttpClient<ITrendResearchService, TrendResearchService>(client =>
        {
            // Was previously unconfigured (silently inheriting HttpClient's 100s default)
            // rather than the ~20s bound used elsewhere in this file for third-party calls.
            client.Timeout = TimeSpan.FromSeconds(20);

            // dev.to's API returns 403 for any request with no User-Agent header at all -
            // confirmed against the live API (curl with the header stripped entirely also
            // gets 403; adding any real UA gets 200). HttpClient sends no default User-Agent,
            // unlike curl/browsers, so every dev.to call from this service was silently
            // failing with 403 (caught and logged as a warning, contributing zero signals)
            // regardless of focus area - not the narrow-filter issue the HN/dev.to fallback
            // logic elsewhere in TrendResearchService addresses.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GwsBusinessSuite-ContentStudio/1.0");
        });
        services.AddScoped<IDockerDeploymentService, DockerDeploymentService>();
        services.AddScoped<ICjAdsService, CjAdsService>();
        services.AddScoped<ISiteSettingsService, SiteSettingsService>();
        services.AddScoped<IAffiliateOfferScoringService, AffiliateOfferScoringService>();
        services.AddScoped<ICmsBuilderService, CmsBuilderService>();
        services.AddScoped<IGlobalBlockService, GlobalBlockService>();
        services.AddScoped<GlobalBlockResolver>();
        services.AddScoped<IMediaLibraryService, MediaLibraryService>();
        services.AddScoped<IFormSubmissionService, FormSubmissionService>();
        services.AddScoped<ICommentService, CommentService>();
        services.AddScoped<IDockerHealthService, DockerHealthService>();
        services.AddSingleton<DockerHealthNotifier>();
        services.AddHostedService<DockerHealthMonitorBackgroundService>();
        services.AddHttpClient<IDigitalOceanService, DigitalOceanService>(client =>
        {
            client.BaseAddress = new Uri("https://api.digitalocean.com/v2/");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddScoped<ISshTerminalService, SshTerminalService>();
        services.AddScoped<IPageRevisionService, PageRevisionService>();
        services.AddScoped<ICmsKnowledgeService, CmsKnowledgeService>();
        services.AddScoped<IContentStudioService, ContentStudioService>();
        services.AddScoped<ICrmService, CrmService>();
        services.AddScoped<IStripeInvoicingClient, StripeInvoicingClient>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddOptions<GwsBusinessSuite.Application.Support.SupportNotificationOptions>()
            .Bind(configuration.GetSection(GwsBusinessSuite.Application.Support.SupportNotificationOptions.SectionName));
        services.AddScoped<GwsBusinessSuite.Application.Support.ISupportTicketService, SupportTicketService>();
        services.AddOptions<BookingEmailOptions>()
            .Bind(configuration.GetSection(BookingEmailOptions.SectionName));
        services.AddSingleton<GwsBusinessSuite.Application.Scheduling.IBookingEmailSender, BookingEmailSender>();
        services.AddScoped<GwsBusinessSuite.Application.Scheduling.IBookingService, BookingService>();
        services.AddScoped<GwsBusinessSuite.Application.Scoring.IDealScoringService, DealScoringService>();
        services.AddOptions<EmailCampaignEmailOptions>()
            .Bind(configuration.GetSection(EmailCampaignEmailOptions.SectionName));
        services.AddSingleton<GwsBusinessSuite.Application.Campaigns.IEmailCampaignEmailSender, EmailCampaignEmailSender>();
        services.AddScoped<GwsBusinessSuite.Application.Campaigns.IEmailCampaignService, EmailCampaignService>();
        services.AddHostedService<EmailCampaignBackgroundService>();
        services.AddScoped<GwsBusinessSuite.Application.SeoAudit.ISeoAuditService, SeoAuditService>();
        services.AddScoped<GwsBusinessSuite.Application.Localization.IContentLocalizationService, ContentLocalizationService>();
        services.AddOptions<SlackOAuthOptions>()
            .Bind(configuration.GetSection(SlackOAuthOptions.SectionName));
        services.AddHttpClient<GwsBusinessSuite.Application.Automation.ISlackOAuthService, SlackOAuthService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddOptions<GoogleOAuthOptions>()
            .Bind(configuration.GetSection(GoogleOAuthOptions.SectionName));
        services.AddHttpClient<GwsBusinessSuite.Application.Automation.IGoogleOAuthService, GoogleOAuthService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<GwsBusinessSuite.Application.MissionControl.IMissionControlService, MissionControlService>();
        services.AddScoped<GwsBusinessSuite.Application.Mobile.IMobilePushRegistrationService, MobilePushRegistrationService>();
        services.AddScoped<GwsBusinessSuite.Application.Mobile.IMobileApprovalService, MobileApprovalService>();
        services.AddScoped<GwsBusinessSuite.Application.Mobile.IPushNotificationSender, NoOpPushNotificationSender>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<ISecurityAuditService, SecurityAuditService>();
        services.AddScoped<IPrivacyOperationsService, PrivacyOperationsService>();
        services.AddHostedService<PrivacyRetentionPurgeBackgroundService>();
        services.AddOptions<OperationalDataRetentionOptions>()
            .Bind(configuration.GetSection(OperationalDataRetentionOptions.SectionName));
        services.AddScoped<GwsBusinessSuite.Application.Operations.IOperationalDataRetentionService, OperationalDataRetentionService>();
        services.AddHostedService<OperationalDataRetentionBackgroundService>();
        services.AddScoped<IResumePdfService, ResumePdfService>();
        services.AddScoped<IAffiliateSuggestionService, AffiliateSuggestionService>();
        services.AddScoped<IAffiliateAnalyticsService, AffiliateAnalyticsService>();
        services.AddScoped<IBusinessIntelligenceService, BusinessIntelligenceService>();
        services.AddScoped<IDeveloperApiKeyService, DeveloperApiKeyService>();
        services.AddScoped<IDeveloperApiResourceService, DeveloperApiResourceService>();
        services.AddSingleton<IAnalyticsGeoLocationResolver, AnalyticsGeoLocationResolver>();
        services.AddScoped<IGrowthAnalyticsService, GrowthAnalyticsService>();
        services.AddOptions<GrowthReportEmailOptions>()
            .Bind(configuration.GetSection(GrowthReportEmailOptions.SectionName));
        services.AddSingleton<IGrowthReportEmailSender, GrowthReportEmailSender>();
        services.AddOptions<FormNotificationOptions>()
            .Bind(configuration.GetSection(FormNotificationOptions.SectionName));
        services.AddOptions<ClientPortalEmailOptions>()
            .Bind(configuration.GetSection(ClientPortalEmailOptions.SectionName));
        services.AddSingleton<GwsBusinessSuite.Application.ClientPortal.IClientPortalEmailSender, ClientPortalEmailSender>();
        services.AddScoped<GwsBusinessSuite.Application.ClientPortal.IClientPortalAuthService, ClientPortalAuthService>();
        services.AddOptions<OperationalAlertOptions>()
            .Bind(configuration.GetSection(OperationalAlertOptions.SectionName));
        services.AddSingleton<GwsBusinessSuite.Application.Operations.IOperationalAlertService, OperationalAlertService>();
        services.AddScoped<IGrowthReportService, GrowthReportService>();
        services.AddHostedService<GrowthReportBackgroundService>();
        services.AddHttpClient<ISocialPublishingService, SocialPublishingService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GwsBusinessSuite-SocialPublisher/1.0");
        });
        services.AddSingleton<SocialPublishingNotifier>();
        services.AddHostedService<SocialPublishingBackgroundService>();
        services.AddScoped<IAffiliateRotationService, AffiliateRotationService>();
        services.AddScoped<IAppGenerationService, AppGenerationService>();
        services.AddHttpClient<IAutomationHttpClient, AutomationHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GwsBusinessSuite-WorkflowAutomation/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // See AutomationDestinationValidator's own doc comment for why this - in short,
            // a one-time pre-check on the request URL doesn't cover redirects or survive
            // DNS-rebinding; validating inside ConnectCallback (on every real TCP connection
            // this handler makes) does.
            ConnectCallback = AutomationDestinationValidator.ConnectAsync,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        });
        services.AddScoped<IAutomationNodeRegistry, AutomationNodeRegistry>();
        services.AddScoped<IAutomationCredentialService, AutomationCredentialService>();
        services.AddScoped<IAutomationWorkflowService, AutomationWorkflowService>();
        services.AddScoped<IAutomationExecutionService, AutomationExecutionService>();
        services.AddScoped<IAutomationTriggerService, AutomationTriggerService>();
        services.AddScoped<IAutomationTemplateService, AutomationTemplateService>();
        services.AddHostedService<AutomationScheduleBackgroundService>();
        services.AddHostedService<AutomationResumeBackgroundService>();
        services.AddHostedService<AutomationCredentialRefreshBackgroundService>();
        services.AddHostedService<CmsScheduledPublishBackgroundService>();
        services.AddScoped<IWikiSyncedBlockService, WikiSyncedBlockService>();
        services.AddScoped<IWikiService, WikiService>();
        services.AddScoped<IQuickNoteService, QuickNoteService>();
        services.AddScoped<IWikiDatabaseService, WikiDatabaseService>();
        services.AddScoped<ISentinelTemplateService, SentinelTemplateService>();
        services.AddScoped<ISentinelWorkspaceImportService, SentinelWorkspaceImportService>();
        services.AddScoped<ISentinelWorkspaceService, SentinelWorkspaceService>();
        services.AddScoped<GwsBusinessSuite.Application.SuiteSearch.ISuiteSearchService, SuiteSearchService>();
        services.AddScoped<IHybridSearchService, HybridSemanticSearchService>();
        services.AddHostedService<SemanticIndexBackgroundService>();
        services.AddScoped<ISentinelCollaborationService, SentinelCollaborationService>();
        services.AddScoped<ISentinelAccessService, SentinelAccessService>();
        services.AddScoped<ISentinelAiService, SentinelAiService>();
        services.AddSingleton<SentinelCollaborationNotifier>();
        services.AddSingleton<SentinelPresenceTracker>();
        services.AddSingleton<SentinelCursorTracker>();
        services.AddScoped<ISentinelPresenceService, SentinelPresenceService>();
        services.AddHttpClient<INotionService, NotionService>(client =>
        {
            client.BaseAddress = new Uri("https://api.notion.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.Configure<NotionOAuthOptions>(
            configuration.GetSection(NotionOAuthOptions.SectionName));
        services.AddHttpClient<INotionOAuthService, NotionOAuthService>(client =>
        {
            client.BaseAddress = new Uri("https://api.notion.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<INotionSyncService, NotionSyncService>();
        services.AddScoped<INotionWebhookService, NotionWebhookService>();
        services.AddSingleton<NotionSyncBackgroundService>();
        services.AddSingleton<INotionSyncCoordinator>(provider =>
            provider.GetRequiredService<NotionSyncBackgroundService>());
        services.AddHostedService(provider =>
            provider.GetRequiredService<NotionSyncBackgroundService>());
        var liveShowRecordingsPath = configuration["LiveShow:RecordingsPath"] ?? "/app/data/live-show-recordings";
        services.AddScoped<ILiveShowService>(sp => new LiveShowService(
            sp.GetRequiredService<IAppDbContext>(),
            liveShowRecordingsPath,
            sp.GetService<ICurrentUserAccessor>()));
        services.AddHttpClient<INewsIntelligenceService, NewsIntelligenceService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; GWSuite/1.0; +https://grantwatson.dev)");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient<IGovernmentIntelligenceService, GovernmentIntelligenceService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; GWSuite/1.0; +https://grantwatson.dev)");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHostedService<GovernmentIntelligenceRefreshBackgroundService>();
        services.AddSingleton<ILocalEventsScraperService, LocalEventsScraperService>();
        services.AddHostedService<LocalEventsRefreshBackgroundService>();
        var congressApiKey = configuration["CongressApi:ApiKey"] is { Length: > 0 } key ? key : "DEMO_KEY";
        services.AddSingleton(new CongressApiSettings(congressApiKey));
        services.AddHttpClient<IFederalCivicFeedService, FederalCivicFeedService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; GWSuite/1.0; +https://grantwatson.dev)");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHostedService<FederalCivicRefreshBackgroundService>();
        services.AddHttpClient<IPodcastDirectoryService, PodcastDirectoryService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; GWSuite/1.0; +https://grantwatson.dev)");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHostedService<NewsRefreshBackgroundService>();
        services.AddHostedService<TopNewsRefreshBackgroundService>();
        services.AddHostedService<CjAdsSyncBackgroundService>();
        services.AddScoped<IPodcastListenProgressService, PodcastListenProgressService>();

        return services;
    }
}
