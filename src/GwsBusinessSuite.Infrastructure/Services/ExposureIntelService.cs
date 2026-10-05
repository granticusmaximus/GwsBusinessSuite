using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Free, no-key exposure sources, each confirmed live 2026-10-04:
//   IP:     Shodan InternetDB (404 {"detail":"No information available"} = nothing seen, not an
//           error; it also answers for private addresses, so those are skipped here), the Tor
//           Project's bulk exit list (one IP per line), Spamhaus DROP v4/v6 (one JSON object per
//           line, last line is {"type":"metadata",...}).
//   Domain: SSLMate certspotter issuances (crt.sh was the first choice but returned 502 on
//           every attempt; certspotter's unauthenticated limit is small, so results are cached per
//           domain), urlscan.io search with page.domain: (plain domain: matches any page that
//           merely contacted the domain), Wayback Machine first snapshot (CDX, limit=1) and latest
//           snapshot (the availability API - CDX's limit=-1 scans the whole index and timed out
//           for github.com; availability answered in ~1s).
public sealed partial class ExposureIntelService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<ExposureIntelService> logger) : IExposureIntelService
{
    private static readonly TimeSpan ListCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan LookupCacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan ArchiveTimeout = TimeSpan.FromSeconds(15);

    private static readonly IPNetwork[] NonPublicNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/3"), IPNetwork.Parse("::/127"), IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("ff00::/8"), IPNetwork.Parse("2001:db8::/32"),
    ];

    public async Task<ExposureIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default)
    {
        target = NormalizeTarget(target);
        var errors = new List<string>();

        if (IPAddress.TryParse(target, out var ip))
        {
            if (IsNonPublic(ip))
            {
                errors.Add("Private or reserved address - public exposure sources don't cover it.");
                return new ExposureIntelResult(target, true, null, null, null, [], [], null, errors);
            }
            var exposureTask = TryGetInternetDbAsync(ip, errors, cancellationToken);
            var torTask = TryCheckTorExitAsync(ip, errors, cancellationToken);
            var dropTask = TryCheckSpamhausDropAsync(ip, errors, cancellationToken);
            await Task.WhenAll(exposureTask, torTask, dropTask);
            return new ExposureIntelResult(target, true, await exposureTask, await torTask, await dropTask, [], [], null, errors);
        }

        if (!DomainPattern().IsMatch(target))
        {
            errors.Add("Enter a domain name (example.com) or an IP address.");
            return new ExposureIntelResult(target, false, null, null, null, [], [], null, errors);
        }

        var hostnamesTask = TryGetCertificateHostnamesAsync(target, errors, cancellationToken);
        var scansTask = TryGetUrlscanScansAsync(target, errors, cancellationToken);
        var archiveTask = TryGetArchiveHistoryAsync(target, errors, cancellationToken);
        await Task.WhenAll(hostnamesTask, scansTask, archiveTask);
        return new ExposureIntelResult(target, false, null, null, null, await hostnamesTask, await scansTask, await archiveTask, errors);
    }

    // Accepts a pasted URL too ("https://example.com/path" -> "example.com").
    internal static string NormalizeTarget(string target)
    {
        target = target.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            target = uri.Host;
        return target.TrimEnd('.').ToLowerInvariant();
    }

    private static bool IsNonPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return NonPublicNetworks.Any(network => network.BaseAddress.AddressFamily == ip.AddressFamily && network.Contains(ip));
    }

    private async Task<IpExposureInfo?> TryGetInternetDbAsync(IPAddress ip, List<string> errors, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync($"https://internetdb.shodan.io/{ip}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new IpExposureInfo([], [], [], [], []);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"Shodan InternetDB returned {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }
            var row = await response.Content.ReadFromJsonSafeAsync<InternetDbRow>(ct);
            return row is null ? null : new IpExposureInfo(
                row.Ports?.OrderBy(port => port).ToList() ?? [],
                row.Vulns?.OrderByDescending(v => v, StringComparer.Ordinal).ToList() ?? [],
                row.Tags ?? [],
                row.Cpes ?? [],
                row.Hostnames ?? []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "InternetDB lookup failed for {Ip}", ip);
            errors.Add("Shodan InternetDB lookup failed");
            return null;
        }
    }

    private async Task<bool?> TryCheckTorExitAsync(IPAddress ip, List<string> errors, CancellationToken ct)
    {
        try
        {
            if (!cache.TryGetValue("threat-intel:tor-exits", out HashSet<string>? exits) || exits is null)
            {
                var text = await httpClient.GetStringAsync("https://check.torproject.org/torbulkexitlist", ct);
                exits = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith('#'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                cache.Set("threat-intel:tor-exits", exits, ListCacheDuration);
            }
            return exits.Contains(ip.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Tor exit list fetch failed");
            errors.Add("Tor exit list unavailable");
            return null;
        }
    }

    private async Task<SpamhausDropListing?> TryCheckSpamhausDropAsync(IPAddress ip, List<string> errors, CancellationToken ct)
    {
        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        var cacheKey = v6 ? "threat-intel:drop-v6" : "threat-intel:drop-v4";
        try
        {
            if (!cache.TryGetValue(cacheKey, out List<(IPNetwork Network, string SblId)>? ranges) || ranges is null)
            {
                var text = await httpClient.GetStringAsync(v6 ? "https://www.spamhaus.org/drop/drop_v6.json" : "https://www.spamhaus.org/drop/drop_v4.json", ct);
                ranges = ParseDropList(text);
                cache.Set(cacheKey, ranges, LookupCacheDuration);
            }
            var match = ranges.FirstOrDefault(range => range.Network.Contains(ip));
            return match.SblId is null ? null : new SpamhausDropListing(match.Network.ToString(), match.SblId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Spamhaus DROP fetch failed");
            errors.Add("Spamhaus DROP list unavailable");
            return null;
        }
    }

    internal static List<(IPNetwork Network, string SblId)> ParseDropList(string ndjson)
    {
        var ranges = new List<(IPNetwork, string)>();
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var row = JsonSerializer.Deserialize<DropRow>(line);
                if (row?.Cidr is not null && row.SblId is not null && IPNetwork.TryParse(row.Cidr, out var network))
                    ranges.Add((network, row.SblId));
            }
            catch (JsonException)
            {
                // One malformed line shouldn't discard the rest of the list.
            }
        }
        return ranges;
    }

    private async Task<IReadOnlyList<string>> TryGetCertificateHostnamesAsync(string domain, List<string> errors, CancellationToken ct)
    {
        var cacheKey = $"threat-intel:ct:{domain}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null) return cached;
        try
        {
            using var response = await httpClient.GetAsync(
                $"https://api.certspotter.com/v1/issuances?domain={Uri.EscapeDataString(domain)}&include_subdomains=true&expand=dns_names", ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                errors.Add("Certificate transparency lookups are rate-limited right now - try again in a few minutes");
                return [];
            }
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"Certificate transparency lookup returned {(int)response.StatusCode} {response.StatusCode}");
                return [];
            }
            var issuances = await response.Content.ReadFromJsonSafeAsync<List<CertIssuance>>(ct) ?? [];
            IReadOnlyList<string> hostnames = issuances
                .SelectMany(issuance => issuance.DnsNames ?? [])
                .Select(name => name.Trim().ToLowerInvariant())
                .Where(name => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name.TrimStart('*', '.'), StringComparer.Ordinal)
                .ToList();
            cache.Set(cacheKey, hostnames, LookupCacheDuration);
            return hostnames;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Certificate transparency lookup failed for {Domain}", domain);
            errors.Add("Certificate transparency lookup failed");
            return [];
        }
    }

    private async Task<IReadOnlyList<UrlscanScan>> TryGetUrlscanScansAsync(string domain, List<string> errors, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"https://urlscan.io/api/v1/search/?q={Uri.EscapeDataString($"page.domain:{domain}")}&size=10", ct);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"urlscan.io returned {(int)response.StatusCode} {response.StatusCode}");
                return [];
            }
            var payload = await response.Content.ReadFromJsonSafeAsync<UrlscanResponse>(ct);
            return (payload?.Results ?? [])
                .Where(result => result.Task?.Uuid is not null)
                .Select(result => new UrlscanScan(
                    DateTimeOffset.TryParse(result.Task!.Time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null,
                    result.Page?.Url ?? result.Task.Url ?? domain,
                    string.IsNullOrWhiteSpace(result.Page?.Title) ? null : result.Page.Title,
                    result.Page?.Ip,
                    result.Page?.Country,
                    result.Page?.Server,
                    $"https://urlscan.io/result/{result.Task.Uuid}/"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "urlscan.io search failed for {Domain}", domain);
            errors.Add("urlscan.io search failed");
            return [];
        }
    }

    private async Task<ArchiveHistory?> TryGetArchiveHistoryAsync(string domain, List<string> errors, CancellationToken ct)
    {
        var cacheKey = $"threat-intel:wayback:{domain}";
        if (cache.TryGetValue(cacheKey, out ArchiveHistory? cached) && cached is not null) return cached;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ArchiveTimeout);
        try
        {
            var firstTask = httpClient.GetStringAsync(
                $"https://web.archive.org/cdx/search/cdx?url={Uri.EscapeDataString(domain)}&output=json&fl=timestamp&limit=1", timeout.Token);
            var latestTask = httpClient.GetStringAsync($"https://archive.org/wayback/available?url={Uri.EscapeDataString(domain)}", timeout.Token);
            await Task.WhenAll(firstTask, latestTask);
            var history = new ArchiveHistory(ParseCdxTimestamp(await firstTask), ParseAvailabilityTimestamp(await latestTask),
                $"https://web.archive.org/web/*/{domain}");
            cache.Set(cacheKey, history, LookupCacheDuration);
            return history;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Wayback Machine lookup failed for {Domain}", domain);
            errors.Add("Wayback Machine didn't answer in time");
            return null;
        }
    }

    // [["timestamp"],["20201101000403"]] - header row, then one data row (or none).
    internal static DateTimeOffset? ParseCdxTimestamp(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var rows = JsonSerializer.Deserialize<List<List<string>>>(json);
        var value = rows is { Count: > 1 } && rows[1].Count > 0 ? rows[1][0] : null;
        return DateTimeOffset.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    // {"url":"x","archived_snapshots":{"closest":{"available":true,"timestamp":"20261005005558",...}}}
    // or {"archived_snapshots":{}} when nothing is archived.
    internal static DateTimeOffset? ParseAvailabilityTimestamp(string json)
    {
        var value = JsonNode.Parse(json)?["archived_snapshots"]?["closest"]?["timestamp"]?.GetValue<string>();
        return DateTimeOffset.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex DomainPattern();

    private sealed class InternetDbRow
    {
        [JsonPropertyName("ports")] public List<int>? Ports { get; set; }
        [JsonPropertyName("vulns")] public List<string>? Vulns { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("cpes")] public List<string>? Cpes { get; set; }
        [JsonPropertyName("hostnames")] public List<string>? Hostnames { get; set; }
    }

    private sealed class DropRow
    {
        [JsonPropertyName("cidr")] public string? Cidr { get; set; }
        [JsonPropertyName("sblid")] public string? SblId { get; set; }
    }

    private sealed class CertIssuance
    {
        [JsonPropertyName("dns_names")] public List<string>? DnsNames { get; set; }
    }

    private sealed class UrlscanResponse
    {
        [JsonPropertyName("results")] public List<UrlscanResult>? Results { get; set; }
    }

    private sealed class UrlscanResult
    {
        [JsonPropertyName("task")] public UrlscanTask? Task { get; set; }
        [JsonPropertyName("page")] public UrlscanPage? Page { get; set; }
    }

    private sealed class UrlscanTask
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        [JsonPropertyName("time")] public string? Time { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class UrlscanPage
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
    }
}
