using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.AffiliateRotations;
using GwsBusinessSuite.Application.AppGeneration;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.CmsKnowledge;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Application.CjAds;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Application.LiveShow;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Application.Podcasts;
using GwsBusinessSuite.Application.Resume;
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

        services.TryAddSingleton<IPublicContentCacheInvalidator, NoOpPublicContentCacheInvalidator>();
        services.AddSingleton<PublicContentCacheInvalidationInterceptor>();
        services.AddDbContextFactory<ApplicationDbContext>((serviceProvider, options) => options
            .UseSqlite(connectionString)
            .AddInterceptors(serviceProvider.GetRequiredService<PublicContentCacheInvalidationInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddScoped<IAppDbContextFactory, AppDbContextFactory>();
        services.AddScoped<IAdminPortalSummaryService, AdminPortalSummaryService>();
        services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
        services.AddHttpClient<ICjAffiliateService, CjAffiliateService>();
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
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<NewsRefreshState>();
        services.AddHttpClient<ITrendResearchService, TrendResearchService>();
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
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IResumePdfService, ResumePdfService>();
        services.AddScoped<IAffiliateSuggestionService, AffiliateSuggestionService>();
        services.AddScoped<IAffiliateAnalyticsService, AffiliateAnalyticsService>();
        services.AddScoped<IAffiliateRotationService, AffiliateRotationService>();
        services.AddScoped<IAppGenerationService, AppGenerationService>();
        services.AddHttpClient<IAutomationHttpClient, AutomationHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GwsBusinessSuite-WorkflowAutomation/1.0");
        });
        services.AddScoped<IAutomationNodeRegistry, AutomationNodeRegistry>();
        services.AddScoped<IAutomationCredentialService, AutomationCredentialService>();
        services.AddScoped<IAutomationWorkflowService, AutomationWorkflowService>();
        services.AddScoped<IAutomationExecutionService, AutomationExecutionService>();
        services.AddScoped<IAutomationTriggerService, AutomationTriggerService>();
        services.AddHostedService<AutomationScheduleBackgroundService>();
        services.AddHostedService<AutomationResumeBackgroundService>();
        services.AddScoped<IWikiService, WikiService>();
        services.AddScoped<IWikiDatabaseService, WikiDatabaseService>();
        services.AddScoped<ISentinelWorkspaceService, SentinelWorkspaceService>();
        services.AddHttpClient<INotionService, NotionService>(client =>
        {
            client.BaseAddress = new Uri("https://api.notion.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<INotionSyncService, NotionSyncService>();
        services.AddHostedService<NotionSyncBackgroundService>();
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
