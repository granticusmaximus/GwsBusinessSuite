using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Read-only numbers for the agent (05-11-26, the role coverage pass): the site's visitors from
// Growth, and how the server's containers are doing, through the same services as the Growth and
// Docker pages (both AdminOnly).
public sealed class InsightsAgentTools(IGrowthAnalyticsService growth, IDockerHealthService docker, TimeProvider time) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int TopRows = 5;
    private const int AlertsShown = 10;

    public SentinelAgentArea Area { get; } = new("insights", "site visitors and traffic (Growth), and the server's health", ["visitor", "traffic", "analytics", "growth", "page view", "health", "container", "server", "docker", "uptime"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "growth_summary",
            "The public site's traffic over the last N days (7 by default, up to 90): visitors, page views, bounce rate, each against the period before, plus the top pages and sources.",
            SentinelAgentJson.Schema(("days", "integer", "How many days back", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at the site's traffic",
            GrowthAsync),
        new SentinelAgentTool(
            "system_health",
            "How the server is doing: each container's state and health, restarts, and the unread health alerts.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Read, Roles,
            _ => "Checking the server's health",
            HealthAsync)
    ];

    private async Task<SentinelAgentToolResult> GrowthAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var days = (int)Math.Clamp(args.OptionalNumber("days") ?? 7, 1, 90);
        var to = time.GetUtcNow();
        var dashboard = await growth.GetDashboardAsync(to.AddDays(-days), to, breakdownLimit: TopRows, cancellationToken: ct);
        return SentinelAgentToolResult.Of(new
        {
            days,
            visitors = new { now = dashboard.Visitors, before = dashboard.VisitorsComparison.PreviousValue, changePercent = dashboard.VisitorsComparison.ChangePercent },
            pageViews = new { now = dashboard.PageViews, before = dashboard.PageViewsComparison.PreviousValue, changePercent = dashboard.PageViewsComparison.ChangePercent },
            bounceRatePercent = new { now = dashboard.BounceRate, before = dashboard.BounceRateComparison.PreviousValue },
            topPages = dashboard.TopPages.Take(TopRows).Select(row => new { page = row.Label, row.Visitors, row.Views }),
            topSources = dashboard.TopSources.Take(TopRows).Select(row => new { source = row.Label, row.Visitors })
        });
    }

    private async Task<SentinelAgentToolResult> HealthAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var list = await docker.ListContainersAsync(ct);
        var alerts = await docker.ListAlertsAsync(unreadOnly: true, ct);
        return SentinelAgentToolResult.Of(new
        {
            available = list.Available,
            unavailable = list.UnavailableReason,
            containers = list.Containers.Select(container => new { container.Name, container.State, container.Health, container.Status, container.RestartCount, problem = container.IsError }),
            unreadAlerts = alerts.Count,
            latestAlerts = alerts.OrderByDescending(alert => alert.CreatedAt).Take(AlertsShown).Select(alert => new
            {
                alert.ContainerName, alert.Severity, alert.Message, at = alert.CreatedAt.ToLocalTime().ToString("dd-MM-yy HH:mm")
            })
        });
    }
}
