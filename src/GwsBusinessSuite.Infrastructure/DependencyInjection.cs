using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AppRegistry;
using GwsBusinessSuite.Application.Blog;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.CmsKnowledge;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.CjAds;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Deployments;
using GwsBusinessSuite.Application.SanityPublishing;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=gws-suite.db";
        services.AddDataProtection();
        services.Configure<ContentStudioOptions>(configuration.GetSection(ContentStudioOptions.SectionName));
        services.Configure<CmsBuilderOptions>(configuration.GetSection(CmsBuilderOptions.SectionName));
        services.Configure<SanityOptions>(configuration.GetSection(SanityOptions.SectionName));
        services.Configure<ExternalServicesOptions>(configuration.GetSection(ExternalServicesOptions.SectionName));
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
        services.AddHttpClient<ICjAffiliateService, CjAffiliateService>();
        services.AddHttpClient<ISanityPublisher, SanityPublisher>();
        services.AddHttpClient<ISanityReader, SanityReader>();
        services.AddHttpClient<ICloudflareService, CloudflareService>();
        services.AddHttpClient<IDigitalOceanService, DigitalOceanService>();
        services.AddHttpClient<IOllamaService, OllamaService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContentStudioOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? ContentStudioOptions.DefaultBaseUrl
                : options.BaseUrl;
            var timeoutMinutes = options.GenerationTimeoutMinutes <= 0
                ? ContentStudioOptions.DefaultGenerationTimeoutMinutes
                : options.GenerationTimeoutMinutes;

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
        });
        services.AddScoped<IDockerDeploymentService, DockerDeploymentService>();
        services.AddScoped<IAppRegistryService, AppRegistryService>();
        services.AddScoped<ICjAdsService, CjAdsService>();
        services.AddScoped<IAffiliateOfferScoringService, AffiliateOfferScoringService>();
        services.AddScoped<ICmsBuilderService, CmsBuilderService>();
        services.AddScoped<IReactPageBuilderService, ReactPageBuilderService>();
        services.AddScoped<ICmsKnowledgeService, CmsKnowledgeService>();
        services.AddScoped<IContentStudioService, ContentStudioService>();
        services.AddScoped<ICrmService, CrmService>();
        services.AddScoped<IDeploymentWorkspaceService, DeploymentWorkspaceService>();
        services.AddScoped<ISanityPublisherWorkspaceService, SanityPublisherWorkspaceService>();
        services.AddScoped<IWikiService, WikiService>();
        services.AddScoped<ISanityImportService, SanityImportService>();

        return services;
    }
}
