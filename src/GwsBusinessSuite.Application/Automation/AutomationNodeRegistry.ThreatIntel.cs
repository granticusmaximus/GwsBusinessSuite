using System.Text.Json;
using GwsBusinessSuite.Application.ThreatIntel;

namespace GwsBusinessSuite.Application.Automation;

// Threat-intelligence nodes over the same free sources as the Threat Intelligence page. All
// read-only. Pair "Known Exploited Vulnerabilities" with a Schedule Trigger + If + Notify to get
// an email whenever CISA adds a CVE. Services resolve lazily from the scope, like wiki.findPages.
public sealed partial class AutomationNodeRegistry
{
    private T RequireThreatIntelService<T>(AutomationNodeSnapshot node) where T : class =>
        serviceProvider?.GetService(typeof(T)) as T
            ?? throw new InvalidOperationException($"{node.Name}: threat intelligence is not available to the automation engine.");

    private async Task<AutomationNodeRunResult> ExecuteKnownExploitedAsync(
        AutomationNodeSnapshot node, JsonElement input, CancellationToken cancellationToken)
    {
        var service = RequireThreatIntelService<IVulnerabilityIntelService>(node);
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var sinceDays = Math.Clamp(parameters["sinceDays"]?.GetValue<int>() ?? 1, 0, 3650);
        var limit = Math.Clamp(parameters["limit"]?.GetValue<int>() ?? 25, 1, 500);

        // dateAdded is a calendar date in CISA's catalog; sinceDays 0 = added today (UTC).
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-sinceDays);
        var entries = (await service.GetKnownExploitedAsync(500, cancellationToken))
            .Where(entry => entry.DateAdded >= cutoff)
            .Take(limit)
            .ToList();

        var output = RequireObject(input, node.Name).DeepClone().AsObject();
        output["vulnerabilities"] = JsonSerializer.SerializeToNode(entries.Select(entry => new
        {
            cveId = entry.CveId,
            vendor = entry.Vendor,
            product = entry.Product,
            name = entry.Name,
            description = entry.ShortDescription,
            requiredAction = entry.RequiredAction,
            dateAdded = entry.DateAdded.ToString("yyyy-MM-dd"),
            dueDate = entry.DueDate?.ToString("yyyy-MM-dd"),
            knownRansomwareUse = entry.KnownRansomwareUse,
            url = $"https://nvd.nist.gov/vuln/detail/{entry.CveId}"
        }));
        output["vulnerabilityCount"] = entries.Count;
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private async Task<AutomationNodeRunResult> ExecuteLookupCveAsync(
        AutomationNodeSnapshot node, JsonElement input, IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName, CancellationToken cancellationToken)
    {
        var service = RequireThreatIntelService<IVulnerabilityIntelService>(node);
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var cveId = ResolveText(parameters["cveId"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);

        var detail = await service.GetCveDetailAsync(cveId, cancellationToken)
            ?? throw new InvalidOperationException($"{node.Name}: '{cveId}' is not a CVE id (expected e.g. CVE-2024-3400).");

        var output = RequireObject(input, node.Name).DeepClone().AsObject();
        output["cve"] = JsonSerializer.SerializeToNode(new
        {
            cveId = detail.CveId,
            description = detail.Description,
            published = detail.Published,
            cvssScore = detail.CvssScore,
            cvssSeverity = detail.CvssSeverity,
            epssScore = detail.EpssScore,
            epssPercentile = detail.EpssPercentile,
            knownExploited = detail.KnownExploited is not null,
            knownRansomwareUse = detail.KnownExploited?.KnownRansomwareUse ?? false,
            references = detail.References,
            errors = detail.Errors
        });
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }

    private async Task<AutomationNodeRunResult> ExecuteCheckIndicatorAsync(
        AutomationNodeSnapshot node, JsonElement input, IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName, CancellationToken cancellationToken)
    {
        var service = RequireThreatIntelService<IExposureIntelService>(node);
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var target = ResolveText(parameters["target"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException($"{node.Name}: target is empty - set it to a domain or IP address.");

        var result = await service.InvestigateAsync(target, cancellationToken);
        var output = RequireObject(input, node.Name).DeepClone().AsObject();
        output["indicator"] = JsonSerializer.SerializeToNode(new
        {
            target = result.Target,
            isIpAddress = result.IsIpAddress,
            openPorts = result.IpExposure?.Ports ?? [],
            knownCves = result.IpExposure?.Vulnerabilities ?? [],
            isTorExitNode = result.IsTorExitNode,
            onSpamhausDrop = result.SpamhausDrop is not null,
            spamhausSblId = result.SpamhausDrop?.SblId,
            certificateHostnames = result.CertificateHostnames,
            recentScans = result.RecentScans.Select(scan => new { scannedAt = scan.ScannedAt, url = scan.PageUrl, result = scan.ResultUrl }),
            firstArchived = result.Archive?.FirstSnapshot,
            lastArchived = result.Archive?.LastSnapshot,
            errors = result.Errors
        });
        return SingleOutput("main", JsonSerializer.SerializeToElement(output));
    }
}
