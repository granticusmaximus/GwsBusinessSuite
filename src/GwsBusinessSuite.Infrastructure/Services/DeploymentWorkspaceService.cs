using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DeploymentWorkspaceService(
    IAppDbContext dbContext,
    IHostEnvironment hostEnvironment,
    IOptions<CmsBuilderOptions> cmsBuilderOptions,
    IOptions<ExternalServicesOptions> externalServicesOptions) : IDeploymentWorkspaceService
{
    public async Task<DeploymentWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var apps = await dbContext.BusinessApps
            .AsNoTracking()
            .OrderBy(app => app.Name)
            .ToListAsync(cancellationToken);

        var targets = await dbContext.DeploymentTargets
            .AsNoTracking()
            .OrderBy(target => target.Provider)
            .ThenBy(target => target.Name)
            .ToListAsync(cancellationToken);

        var dockerfilePath = Path.Combine(GetRepositoryRoot(), "Dockerfile");
        var dockerComposePath = Path.Combine(GetRepositoryRoot(), "docker-compose.yml");
        var hasDockerfile = File.Exists(dockerfilePath);
        var hasDockerComposeFile = File.Exists(dockerComposePath);
        var baseDomain = externalServicesOptions.Value.BaseDomain?.Trim() ?? string.Empty;
        var tunnelId = externalServicesOptions.Value.CloudflareTunnelId?.Trim() ?? string.Empty;
        var resolvedReactPath = ResolveReactAppRelativePath();

        var appStatuses = apps
            .Select(app => BuildAppStatus(app, baseDomain, tunnelId))
            .ToList();

        return new DeploymentWorkspaceSnapshot
        {
            Summary = new DeploymentSummary
            {
                TotalApps = apps.Count,
                ActiveApps = apps.Count(app => string.Equals(app.Status, "Active", StringComparison.OrdinalIgnoreCase)),
                RouteReadyApps = appStatuses.Count(app => string.Equals(app.ReadinessLabel, "Ready", StringComparison.OrdinalIgnoreCase)),
                AppsMissingPort = appStatuses.Count(app => string.Equals(app.ReadinessLabel, "Missing Port", StringComparison.OrdinalIgnoreCase))
            },
            Docker = BuildDockerStatus(hasDockerfile, hasDockerComposeFile),
            Cloudflare = BuildCloudflareStatus(baseDomain, tunnelId),
            DigitalOcean = BuildDigitalOceanStatus(targets),
            ReactAppRelativePath = resolvedReactPath,
            HasDockerComposeFile = hasDockerComposeFile,
            HasDockerfile = hasDockerfile,
            Apps = appStatuses,
            Targets = targets.Select(target => new DeploymentTargetSummary
            {
                TargetId = target.Id,
                Provider = target.Provider,
                Name = target.Name,
                Host = target.Host ?? string.Empty,
                Notes = target.Notes ?? string.Empty
            }).ToList()
        };
    }

    private DeploymentAppStatus BuildAppStatus(
        Domain.Entities.BusinessApp app,
        string baseDomain,
        string tunnelId)
    {
        var localUrl = app.Port is > 0 ? $"http://localhost:{app.Port}" : string.Empty;
        var publicUrl = BuildPublicUrl(app.Subdomain, baseDomain);
        var hasPort = app.Port is > 0;
        var hasSubdomain = !string.IsNullOrWhiteSpace(app.Subdomain);
        var hasCloudflareConfig = !string.IsNullOrWhiteSpace(baseDomain) && !string.IsNullOrWhiteSpace(tunnelId);

        var readinessLabel = hasPort switch
        {
            false => "Missing Port",
            true when !hasSubdomain => "Missing Subdomain",
            true when !hasCloudflareConfig => "Tunnel Config Needed",
            _ => "Ready"
        };

        var nextAction = readinessLabel switch
        {
            "Missing Port" => "Assign a local port in App Registry so the app can be routed and verified.",
            "Missing Subdomain" => "Add a subdomain in App Registry to create a stable public route.",
            "Tunnel Config Needed" => "Set ExternalServices:BaseDomain and ExternalServices:CloudflareTunnelId to route this app.",
            _ => "Ready for route creation and deployment handoff."
        };

        return new DeploymentAppStatus
        {
            AppId = app.Id,
            Name = app.Name,
            AppType = app.AppType,
            Status = app.Status,
            Subdomain = app.Subdomain,
            Port = app.Port,
            LocalUrl = localUrl,
            PublicUrl = publicUrl,
            ReadinessLabel = readinessLabel,
            NextAction = nextAction
        };
    }

    private static DeploymentIntegrationStatus BuildDockerStatus(bool hasDockerfile, bool hasDockerComposeFile)
    {
        var isConfigured = hasDockerfile || hasDockerComposeFile;
        var message = isConfigured
            ? $"Detected {(hasDockerfile ? "Dockerfile" : "no Dockerfile")} and {(hasDockerComposeFile ? "docker-compose.yml" : "no docker-compose.yml")} in the repository."
            : "No Docker deployment assets were found in the repository root.";

        return new DeploymentIntegrationStatus
        {
            Name = "Docker",
            IsConfigured = isConfigured,
            StatusLabel = isConfigured ? "Repo Ready" : "Not Prepared",
            Message = message
        };
    }

    private static DeploymentIntegrationStatus BuildCloudflareStatus(string baseDomain, string tunnelId)
    {
        var isConfigured = !string.IsNullOrWhiteSpace(baseDomain) && !string.IsNullOrWhiteSpace(tunnelId);
        var message = isConfigured
            ? $"Cloudflare routing can target subdomains on {baseDomain} through tunnel {tunnelId}."
            : "Set ExternalServices:BaseDomain and ExternalServices:CloudflareTunnelId to enable subdomain routing.";

        return new DeploymentIntegrationStatus
        {
            Name = "Cloudflare",
            IsConfigured = isConfigured,
            StatusLabel = isConfigured ? "Configured" : "Needs Config",
            Message = message
        };
    }

    private static DeploymentIntegrationStatus BuildDigitalOceanStatus(IReadOnlyCollection<Domain.Entities.DeploymentTarget> targets)
    {
        var doTargets = targets.Count(target =>
            string.Equals(target.Provider, "DigitalOcean", StringComparison.OrdinalIgnoreCase));

        var isConfigured = doTargets > 0;
        var message = isConfigured
            ? $"Tracking {doTargets} DigitalOcean deployment target{(doTargets == 1 ? string.Empty : "s")} in local persistence."
            : "No DigitalOcean deployment targets are registered yet.";

        return new DeploymentIntegrationStatus
        {
            Name = "DigitalOcean",
            IsConfigured = isConfigured,
            StatusLabel = isConfigured ? "Tracked" : "No Targets",
            Message = message
        };
    }

    private string ResolveReactAppRelativePath()
    {
        var repoRoot = GetRepositoryRoot();
        var configuredPath = NormalizePath(cmsBuilderOptions.Value.ReactAppRelativePath);
        var configuredAbsolutePath = Path.Combine(repoRoot, configuredPath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(configuredAbsolutePath))
        {
            return configuredPath;
        }

        foreach (var candidate in new[] { "apps/public-site", "app/public-site", "public-site" })
        {
            var absoluteCandidate = Path.Combine(repoRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(absoluteCandidate))
            {
                return candidate;
            }
        }

        return configuredPath;
    }

    private string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(hostEnvironment.ContentRootPath, "..", ".."));
    }

    private static string BuildPublicUrl(string? subdomain, string baseDomain)
    {
        if (string.IsNullOrWhiteSpace(subdomain) || string.IsNullOrWhiteSpace(baseDomain))
        {
            return string.Empty;
        }

        var safeBaseDomain = baseDomain.Trim().Trim('/');
        if (Uri.TryCreate(safeBaseDomain, UriKind.Absolute, out var baseUri) && !string.IsNullOrWhiteSpace(baseUri.Host))
        {
            return $"{baseUri.Scheme}://{subdomain.Trim()}.{baseUri.Host}";
        }

        return $"https://{subdomain.Trim()}.{safeBaseDomain}";
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? "apps/public-site"
            : path.Trim().Replace('\\', '/').Trim('/');
    }
}
