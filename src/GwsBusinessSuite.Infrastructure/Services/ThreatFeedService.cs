using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using GwsBusinessSuite.Infrastructure.Services.Reach;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record NewCve(string Id, DateTimeOffset Published, string? Vendor, string? Product, string Summary, double? Score, string? Severity);

public sealed record MaliciousPackage(string Id, string Ecosystem, string Name, string Summary, DateTimeOffset? Published, string Url);

public sealed record CyberDisclosure(string Company, DateOnly Filed, string Url);

public sealed record CyberDisclosures(bool Configured, IReadOnlyList<CyberDisclosure> Filings, string? Note);

public sealed class SecEdgarOptions
{
    public const string SectionName = "SecEdgar";

    // SEC asks every automated request to name who's asking, with a contact address, in the
    // User-Agent (sec.gov/os/accessing-edgar-data). Until it's set, the 8-K feed stays off.
    public string? ContactEmail { get; set; }
}

// Three fresh feeds for Threat Intelligence (06-11-26, from the globalhackmap review):
//   - new CVEs from CIRCL's Vulnerability-Lookup (the CVE list, minutes after publication), last 72 h
//   - malicious packages from the GitHub Advisory Database (type=malware); Reach:GitHubToken, when set, raises the limit
//   - cyber-incident 8-K filings (Item 1.05) from SEC EDGAR full-text search, once SecEdgar:ContactEmail is set
public sealed class ThreatFeedService(
    HttpClient httpClient,
    IMemoryCache cache,
    TimeProvider time,
    IOptions<SecEdgarOptions> sec,
    IOptions<ReachOptions> reach,
    ILogger<ThreatFeedService> logger)
{
    private const string CirclUrl = "https://vulnerability.circl.lu/api/vulnerability/last/cvelistv5/100";
    private const string GitHubUrl = "https://api.github.com/advisories?type=malware&per_page=25";
    private const string EdgarUrl = "https://efts.sec.gov/LATEST/search-index";
    public static readonly TimeSpan NewWindow = TimeSpan.FromHours(72);
    public const int DisclosureDays = 45;

    public Task<IReadOnlyList<NewCve>> NewCvesAsync(CancellationToken ct = default) =>
        CachedAsync("threat:new-cves", TimeSpan.FromMinutes(10), async () =>
            ParseNewCves(await httpClient.GetStringAsync(CirclUrl, ct), time.GetUtcNow() - NewWindow));

    public Task<IReadOnlyList<MaliciousPackage>> MaliciousPackagesAsync(CancellationToken ct = default) =>
        CachedAsync("threat:malicious-packages", TimeSpan.FromMinutes(30), async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(reach.Value.GitHubToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reach.Value.GitHubToken.Trim());
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return ParseMaliciousPackages(await response.Content.ReadAsStringAsync(ct));
        });

    public async Task<CyberDisclosures> CyberDisclosuresAsync(CancellationToken ct = default)
    {
        var contact = sec.Value.ContactEmail?.Trim();
        if (string.IsNullOrEmpty(contact) || !contact.Contains('@'))
            return new CyberDisclosures(false, [], "SEC asks automated requests to name a contact address. Set SecEdgar__ContactEmail to turn this on.");
        var to = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-DisclosureDays);
        var filings = await CachedAsync("threat:sec-1-05", TimeSpan.FromHours(1), async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{EdgarUrl}?q=%22Item%201.05%22&forms=8-K&dateRange=custom&startdt={from:yyyy-MM-dd}&enddt={to:yyyy-MM-dd}");
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", $"GwsBusinessSuite ThreatIntel {contact}");
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return ParseDisclosures(await response.Content.ReadAsStringAsync(ct));
        });
        return new CyberDisclosures(true, filings, null);
    }

    // CVE 5 records, newest first, published inside the window. The score is the first CVSS the
    // CNA gave (v4, then v3.1, v3.0).
    public static IReadOnlyList<NewCve> ParseNewCves(string json, DateTimeOffset since)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<NewCve>();
        foreach (var record in doc.RootElement.EnumerateArray())
        {
            var meta = Obj(record, "cveMetadata");
            if (Str(meta, "cveId") is not { } id || Str(meta, "state") is "REJECTED") continue;
            if (!DateTimeOffset.TryParse(Str(meta, "datePublished"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) || published < since) continue;
            var cna = Obj(Obj(record, "containers"), "cna");
            var summary = cna.TryGetProperty("descriptions", out var descriptions) && descriptions.ValueKind == JsonValueKind.Array
                ? descriptions.EnumerateArray().Where(d => Str(d, "lang")?.StartsWith("en", StringComparison.OrdinalIgnoreCase) != false).Select(d => Str(d, "value")).FirstOrDefault(v => v is not null) : null;
            var affected = cna.TryGetProperty("affected", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().FirstOrDefault() : default;
            var (score, severity) = Cvss(cna);
            list.Add(new NewCve(id, published, Str(affected, "vendor") is { } v && v != "n/a" ? v : null, Str(affected, "product") is { } p && p != "n/a" ? p : null,
                Shorten(summary ?? "No description yet.", 300), score, severity));
        }
        return list.DistinctBy(c => c.Id).OrderByDescending(c => c.Published).ToList();
    }

    private static (double? Score, string? Severity) Cvss(JsonElement cna)
    {
        if (!cna.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var version in new[] { "cvssV4_0", "cvssV3_1", "cvssV3_0" })
            foreach (var metric in metrics.EnumerateArray())
                if (metric.TryGetProperty(version, out var cvss) && cvss.TryGetProperty("baseScore", out var score) && score.ValueKind == JsonValueKind.Number)
                    return (score.GetDouble(), Str(cvss, "baseSeverity")?.ToLowerInvariant());
        return (null, null);
    }

    public static IReadOnlyList<MaliciousPackage> ParseMaliciousPackages(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<MaliciousPackage>();
        foreach (var advisory in doc.RootElement.EnumerateArray())
        {
            if (Str(advisory, "withdrawn_at") is not null) continue;
            var package = advisory.TryGetProperty("vulnerabilities", out var v) && v.ValueKind == JsonValueKind.Array
                ? Obj(v.EnumerateArray().FirstOrDefault(), "package") : default;
            list.Add(new MaliciousPackage(Str(advisory, "ghsa_id") ?? "", Str(package, "ecosystem") ?? "?", Str(package, "name") ?? "?",
                Shorten(Str(advisory, "summary") ?? "", 200),
                DateTimeOffset.TryParse(Str(advisory, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                Str(advisory, "html_url") ?? "https://github.com/advisories"));
        }
        return list;
    }

    // EDGAR full-text search hits: "_id" is "<accession>:<document>", "_source" names the filer.
    public static IReadOnlyList<CyberDisclosure> ParseDisclosures(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<CyberDisclosure>();
        if (!doc.RootElement.TryGetProperty("hits", out var outer) || !outer.TryGetProperty("hits", out var hits)) return list;
        foreach (var hit in hits.EnumerateArray())
        {
            var source = Obj(hit, "_source");
            var id = Str(hit, "_id") ?? "";
            var accession = id.Split(':')[0];
            var document = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : "";
            var cik = source.TryGetProperty("ciks", out var ciks) && ciks.ValueKind == JsonValueKind.Array ? ciks.EnumerateArray().Select(c => c.GetString()).FirstOrDefault() : null;
            var company = source.TryGetProperty("display_names", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString()).FirstOrDefault() : null;
            if (company is null || !DateOnly.TryParse(Str(source, "file_date"), CultureInfo.InvariantCulture, out var filed)) continue;
            var url = cik is not null && accession.Length > 0
                ? $"https://www.sec.gov/Archives/edgar/data/{long.Parse(cik, CultureInfo.InvariantCulture)}/{accession.Replace("-", "", StringComparison.Ordinal)}/{document}"
                : "https://efts.sec.gov/LATEST/search-index?q=%22Item%201.05%22&forms=8-K";
            list.Add(new CyberDisclosure(System.Text.RegularExpressions.Regex.Replace(company, @"\s*\(CIK \d+\)\s*$", string.Empty), filed, url));
        }
        return list.DistinctBy(f => f.Url).OrderByDescending(f => f.Filed).ToList();
    }

    private async Task<IReadOnlyList<T>> CachedAsync<T>(string key, TimeSpan duration, Func<Task<IReadOnlyList<T>>> load)
    {
        if (cache.TryGetValue(key, out IReadOnlyList<T>? cached) && cached is not null) return cached;
        try
        {
            var loaded = await load();
            cache.Set(key, loaded, duration);
            return loaded;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or FormatException)
        {
            logger.LogWarning(ex, "Threat feed {Key} failed", key);
            return [];
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
