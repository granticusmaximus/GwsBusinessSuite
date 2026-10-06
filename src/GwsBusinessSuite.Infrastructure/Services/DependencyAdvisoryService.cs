using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// NuGet packages come from the running app's own <entry>.deps.json ("libraries" entries of type
// "package"), so the list is always what's actually deployed. Browser libraries can't be read
// from the build, so they're listed here; DependencyInventoryTests fails if a jsDelivr npm
// reference in src/ isn't in this list.
public sealed class RuntimeDependencyInventory(ILogger<RuntimeDependencyInventory> logger) : IDependencyInventory
{
    public static readonly IReadOnlyList<DependencyPackage> FrontEndPackages =
    [
        new("npm", "bootstrap", "5.3.3"),
        new("npm", "highlight.js", "11.10.0"),
        new("npm", "bootstrap-icons", "1.11.3"),
        new("npm", "cesium", "1.145.0"),
        new("npm", "easymde", "2.20.0"),
        new("npm", "hls.js", "1.5.17"),
        new("npm", "leaflet", "1.9.4"),
    ];

    private IReadOnlyList<DependencyPackage>? _cached;

    public IReadOnlyList<DependencyPackage> GetPackages()
    {
        if (_cached is not null) return _cached;
        var packages = new List<DependencyPackage>(ReadDepsJson());
        packages.AddRange(FrontEndPackages);
        _cached = packages.OrderBy(p => p.Ecosystem).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return _cached;
    }

    private IEnumerable<DependencyPackage> ReadDepsJson()
    {
        var entry = Assembly.GetEntryAssembly()?.GetName().Name;
        var path = entry is null ? null : Path.Combine(AppContext.BaseDirectory, $"{entry}.deps.json");
        if (path is null || !File.Exists(path))
        {
            logger.LogWarning("No .deps.json found at {Path}; NuGet dependencies can't be checked.", path);
            return [];
        }

        try
        {
            return ParseDepsJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Couldn't read {Path}", path);
            return [];
        }
    }

    // "libraries": { "MailKit/4.18.1": { "type": "package", ... }, "GwsBusinessSuite.Domain/1.0.0": { "type": "project" } }
    public static IReadOnlyList<DependencyPackage> ParseDepsJson(string json)
    {
        var libraries = JsonNode.Parse(json)?["libraries"]?.AsObject();
        if (libraries is null) return [];
        var packages = new List<DependencyPackage>();
        foreach (var (key, value) in libraries)
        {
            if (value?["type"]?.GetValue<string>() != "package") continue;
            var slash = key.IndexOf('/');
            if (slash <= 0 || slash == key.Length - 1) continue;
            packages.Add(new DependencyPackage("NuGet", key[..slash], key[(slash + 1)..]));
        }
        return packages;
    }
}

// OSV.dev - free, no key. /v1/querybatch answers with only vuln ids per query (verified
// 2026-10-05: Newtonsoft.Json 12.0.1 -> GHSA-5crp-9r3c-p9vr), so each id's detail is fetched
// from /v1/vulns/{id} and cached for a day. Severity comes from database_specific.severity
// (GitHub's LOW/MODERATE/HIGH/CRITICAL); the fixed version from affected[].ranges[].events[].fixed.
public sealed class DependencyAdvisoryService(HttpClient httpClient, IMemoryCache cache, ILogger<DependencyAdvisoryService> logger)
    : IDependencyAdvisoryService
{
    public async Task<IReadOnlyList<DependencyAdvisory>> FindAdvisoriesAsync(IReadOnlyList<DependencyPackage> packages, CancellationToken cancellationToken = default)
    {
        var advisories = new List<DependencyAdvisory>();
        foreach (var chunk in packages.Chunk(500))
        {
            var request = new
            {
                queries = chunk.Select(p => new { package = new { name = p.Name, ecosystem = p.Ecosystem }, version = p.Version }).ToArray()
            };
            using var response = await httpClient.PostAsJsonAsync("https://api.osv.dev/v1/querybatch", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["results"]?.AsArray() ?? [];
            for (var i = 0; i < results.Count && i < chunk.Length; i++)
            {
                foreach (var vuln in results[i]?["vulns"]?.AsArray() ?? [])
                {
                    var id = vuln?["id"]?.GetValue<string>();
                    if (id is null) continue;
                    var detail = await GetDetailAsync(id, cancellationToken);
                    advisories.Add(ToAdvisory(chunk[i], id, detail));
                }
            }
        }
        return advisories;
    }

    private async Task<JsonNode?> GetDetailAsync(string id, CancellationToken ct)
    {
        var cacheKey = $"threat-intel:osv:{id}";
        if (cache.TryGetValue(cacheKey, out JsonNode? cached)) return cached;
        try
        {
            var detail = JsonNode.Parse(await httpClient.GetStringAsync($"https://api.osv.dev/v1/vulns/{Uri.EscapeDataString(id)}", ct));
            cache.Set(cacheKey, detail, TimeSpan.FromHours(24));
            return detail;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "OSV detail lookup failed for {Id}", id);
            return null;
        }
    }

    public static DependencyAdvisory ToAdvisory(DependencyPackage package, string id, JsonNode? detail)
    {
        var severity = (detail?["database_specific"]?["severity"]?.GetValue<string>() ?? "").ToUpperInvariant() switch
        {
            "CRITICAL" => "critical",
            "HIGH" => "high",
            "MODERATE" or "MEDIUM" => "medium",
            "LOW" => "low",
            _ => "medium"
        };
        var aliases = detail?["aliases"]?.AsArray().Select(a => a?.GetValue<string>()).OfType<string>().ToList() ?? [];
        string? fixedVersion = null;
        foreach (var affected in detail?["affected"]?.AsArray() ?? [])
        {
            if (!string.Equals(affected?["package"]?["name"]?.GetValue<string>(), package.Name, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var range in affected?["ranges"]?.AsArray() ?? [])
            foreach (var ev in range?["events"]?.AsArray() ?? [])
            {
                if (ev?["fixed"]?.GetValue<string>() is { } fixedValue) fixedVersion = fixedValue;
            }
        }
        var url = id.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase)
            ? $"https://github.com/advisories/{id}"
            : $"https://osv.dev/vulnerability/{id}";
        return new DependencyAdvisory(package, id, detail?["summary"]?.GetValue<string>(), severity, aliases, fixedVersion, url);
    }
}
