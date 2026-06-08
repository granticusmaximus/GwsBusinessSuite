using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Deployments;

public sealed class ExternalServicesOptions
{
    public const string SectionName = "ExternalServices";

    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";
    public string OllamaModel { get; init; } = "llama3.2";
    public string CloudflareTunnelId { get; init; } = string.Empty;
    public string BaseDomain { get; init; } = string.Empty;
}

public sealed class DeploymentWorkspaceSnapshot
{
    public DeploymentSummary Summary { get; init; } = new();
    public DeploymentIntegrationStatus Docker { get; init; } = new();
    public DeploymentIntegrationStatus Cloudflare { get; init; } = new();
    public DeploymentIntegrationStatus DigitalOcean { get; init; } = new();
    public string ReactAppRelativePath { get; init; } = string.Empty;
    public bool HasDockerComposeFile { get; init; }
    public bool HasDockerfile { get; init; }
    public IReadOnlyList<DeploymentAppStatus> Apps { get; init; } = Array.Empty<DeploymentAppStatus>();
    public IReadOnlyList<DeploymentTargetSummary> Targets { get; init; } = Array.Empty<DeploymentTargetSummary>();
}

public sealed class DeploymentSummary
{
    public int TotalApps { get; init; }
    public int ActiveApps { get; init; }
    public int RouteReadyApps { get; init; }
    public int AppsMissingPort { get; init; }
}

public sealed class DeploymentIntegrationStatus
{
    public string Name { get; init; } = string.Empty;
    public bool IsConfigured { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class DeploymentAppStatus
{
    public Guid AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string AppType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Subdomain { get; init; }
    public int? Port { get; init; }
    public string LocalUrl { get; init; } = string.Empty;
    public string PublicUrl { get; init; } = string.Empty;
    public string ReadinessLabel { get; init; } = string.Empty;
    public string NextAction { get; init; } = string.Empty;
}

public sealed class DeploymentTargetSummary
{
    public Guid TargetId { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}
