using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Fans out a domain or IP to four independent, free data sources - each fails on its own
// (recorded in DomainIntelResult.Errors) rather than failing the whole investigation:
//
//   1. RDAP (registration data): the registry's own RDAP server, looked up in IANA's bootstrap
//      files (data.iana.org/rdap/dns.json, ipv4.json, ipv6.json - cached for a day), with rdap.org
//      as the fallback when IANA has no entry or the registry's server fails. Some TLDs publish
//      no RDAP at all (.io and .co have no IANA entry and rdap.org 404s them; .io has no WHOIS
//      server either, checked 2026-10-05) - those say so rather than showing nothing.
//      rdap.org's Cloudflare front returns a 403 for a request with no User-Agent header, so
//      the DI registration for this service's HttpClient sets one explicitly.
//   2. DNS: A/AAAA via System.Net.Dns, plus CNAME/MX/NS/TXT/CAA via DNS-over-HTTPS
//      (IDnsRecordLookup) - .NET's Dns class can't query other record types.
//   3. Live TLS certificate inspection via SslStream against port 443 - the certificate
//      validation callback always accepts, since the goal is to inspect whatever certificate a
//      site presents (including an expired/self-signed/mismatched one, which is itself a
//      meaningful finding for an investigation tool) rather than to trust it for real data
//      exchange; nothing but the handshake itself happens over this connection.
//   4. Focsec (api.focsec.com) for IP reputation - a real, free-tier, self-registered API,
//      confirmed directly (a keyless request returns a real 401) along with its exact
//      documented request/response shape (Authorization: <key> header; is_vpn/is_proxy/is_tor/
//      is_bot/iso_code response fields) fetched from docs.focsec.com.
public sealed class DomainIntelService(
    HttpClient httpClient,
    IDnsResolver dnsResolver,
    ITlsCertificateFetcher tlsCertificateFetcher,
    IOptions<ThreatIntelOptions> options,
    ILogger<DomainIntelService> logger,
    IDnsRecordLookup recordLookup,
    IMemoryCache cache) : IDomainIntelService
{
    private static readonly TimeSpan BootstrapCacheDuration = TimeSpan.FromHours(24);

    public async Task<DomainIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default)
    {
        target = target.Trim();
        var errors = new List<string>();
        var isIp = IPAddress.TryParse(target, out _);

        var registration = await TryGetRegistrationAsync(target, isIp, errors, cancellationToken);
        var dnsRecords = isIp ? [] : await TryGetDnsRecordsAsync(target, errors, cancellationToken);
        var tlsCertificate = isIp ? null : await TryGetTlsCertificateAsync(target, errors, cancellationToken);
        var ipReputation = isIp ? await TryGetIpReputationAsync(target, errors, cancellationToken) : null;

        return new DomainIntelResult(target, isIp, registration, dnsRecords, tlsCertificate, ipReputation, errors);
    }

    private async Task<RegistrationInfo?> TryGetRegistrationAsync(string target, bool isIp, List<string> errors, CancellationToken ct)
    {
        try
        {
            var path = isIp ? $"ip/{Uri.EscapeDataString(target)}" : $"domain/{Uri.EscapeDataString(target)}";
            var registryBase = await TryGetRegistryRdapBaseAsync(target, isIp, ct);
            if (registryBase is not null)
            {
                var direct = await TryFetchRdapAsync(registryBase + path, ct);
                if (direct is not null) return direct;
            }

            using var response = await httpClient.GetAsync($"https://rdap.org/{path}", ct);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add(registryBase is null && !isIp && response.StatusCode == HttpStatusCode.NotFound
                    ? $"The .{target[(target.LastIndexOf('.') + 1)..]} registry doesn't publish registration data over RDAP"
                    : $"RDAP lookup returned {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(json)?.AsObject();
            return root is null ? null : ParseRdap(root);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "RDAP lookup failed for {Target}", target);
            errors.Add("RDAP lookup failed");
            return null;
        }
    }

    private async Task<RegistrationInfo?> TryFetchRdapAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
            return root is null ? null : ParseRdap(root);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "Registry RDAP lookup {Url} failed; falling back to rdap.org", url);
            return null;
        }
    }

    // The registry's RDAP base URL (always ending in "/") from IANA's bootstrap files.
    private async Task<string?> TryGetRegistryRdapBaseAsync(string target, bool isIp, CancellationToken ct)
    {
        try
        {
            if (!isIp)
            {
                var tld = target.TrimEnd('.')[(target.TrimEnd('.').LastIndexOf('.') + 1)..].ToLowerInvariant();
                var services = await GetBootstrapAsync("dns", ct);
                return services.FirstOrDefault(service => service.Keys.Contains(tld, StringComparer.OrdinalIgnoreCase)).BaseUrl;
            }

            var ip = IPAddress.Parse(target);
            var file = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            foreach (var service in await GetBootstrapAsync(file, ct))
            {
                if (service.Keys.Any(cidr => IPNetwork.TryParse(cidr, out var network) && network.Contains(ip))) return service.BaseUrl;
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            logger.LogDebug(ex, "IANA RDAP bootstrap lookup failed for {Target}", target);
            return null;
        }
    }

    private async Task<IReadOnlyList<(IReadOnlyList<string> Keys, string? BaseUrl)>> GetBootstrapAsync(string file, CancellationToken ct)
    {
        var cacheKey = $"threat-intel:rdap-bootstrap:{file}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<(IReadOnlyList<string>, string?)>? cached) && cached is not null) return cached;

        var root = JsonNode.Parse(await httpClient.GetStringAsync($"https://data.iana.org/rdap/{file}.json", ct));
        var services = ParseBootstrap(root);
        cache.Set(cacheKey, services, BootstrapCacheDuration);
        return services;
    }

    // services: [ [ [keys...], [urls...] ], ... ] - prefer an https URL.
    public static IReadOnlyList<(IReadOnlyList<string> Keys, string? BaseUrl)> ParseBootstrap(JsonNode? root)
    {
        var result = new List<(IReadOnlyList<string>, string?)>();
        foreach (var service in root?["services"]?.AsArray() ?? [])
        {
            if (service is not JsonArray { Count: >= 2 } pair) continue;
            var keys = (pair[0] as JsonArray)?.Select(AsString).OfType<string>().ToList() ?? [];
            var urls = (pair[1] as JsonArray)?.Select(AsString).OfType<string>().ToList() ?? [];
            var url = urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
            if (url is not null && !url.EndsWith('/')) url += "/";
            result.Add((keys, url));
        }
        return result;
    }

    private static RegistrationInfo ParseRdap(JsonObject root)
    {
        string? registrar = null;
        if (root["entities"] is JsonArray entities)
        {
            foreach (var entityNode in entities)
            {
                if (entityNode is not JsonObject entity) continue;
                var roles = (entity["roles"] as JsonArray)?
                    .Select(AsString)
                    .Where(r => r is not null)
                    .ToList() ?? [];

                if (roles.Contains("registrar"))
                {
                    registrar = ExtractVCardFn(entity) ?? registrar;
                    break;
                }
                if (roles.Contains("registrant") && registrar is null)
                {
                    registrar = ExtractVCardFn(entity);
                }
            }
        }
        // IP RDAP responses (ARIN/RIPE/etc.) carry the allocated org's name directly rather
        // than via a "registrar"-roled entity.
        registrar ??= AsString(root["name"]);

        DateTimeOffset? registered = null;
        DateTimeOffset? expires = null;
        if (root["events"] is JsonArray events)
        {
            foreach (var eventNode in events)
            {
                if (eventNode is not JsonObject ev) continue;
                var action = AsString(ev["eventAction"]);
                var dateRaw = AsString(ev["eventDate"]);
                if (dateRaw is null || !DateTimeOffset.TryParse(dateRaw, out var date)) continue;

                if (action == "registration") registered = date;
                else if (action == "expiration") expires = date;
            }
        }

        var nameservers = (root["nameservers"] as JsonArray)?
            .Select(ns => ns is JsonObject nsObj ? AsString(nsObj["ldhName"]) : null)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList() ?? [];

        var statuses = (root["status"] as JsonArray)?
            .Select(AsString)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList() ?? [];

        return new RegistrationInfo(registrar, registered, expires, nameservers, statuses);
    }

    // vcardArray shape: ["vcard", [["version",{},"text","4.0"], ["fn",{},"text","Some Name"], ...]]
    private static string? ExtractVCardFn(JsonObject entity)
    {
        if (entity["vcardArray"] is not JsonArray { Count: >= 2 } vcardArray) return null;
        if (vcardArray[1] is not JsonArray fields) return null;

        foreach (var fieldNode in fields)
        {
            if (fieldNode is not JsonArray { Count: >= 4 } field) continue;
            if (AsString(field[0]) == "fn")
            {
                var name = AsString(field[3]);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<DnsRecordInfo>> TryGetDnsRecordsAsync(string domain, List<string> errors, CancellationToken ct)
    {
        var records = new List<DnsRecordInfo>();
        try
        {
            var addresses = await dnsResolver.ResolveAsync(domain, ct);
            records.AddRange(addresses.Select(a => new DnsRecordInfo(
                a.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A",
                a.ToString())));
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            errors.Add($"DNS lookup failed: {ex.Message}");
        }

        var lookups = DnsRecordTypes.Investigated
            .Select(async type => (Type: type, Values: await recordLookup.QueryAsync(domain, type, ct)))
            .ToList();
        try
        {
            foreach (var (type, values) in await Task.WhenAll(lookups))
            {
                records.AddRange(values.Select(value => new DnsRecordInfo(type, value)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Keep whichever record types did answer.
            foreach (var lookup in lookups.Where(l => l.IsCompletedSuccessfully))
            {
                records.AddRange(lookup.Result.Values.Select(value => new DnsRecordInfo(lookup.Result.Type, value)));
            }
            errors.Add("Some DNS record types couldn't be looked up (DNS-over-HTTPS unavailable)");
        }

        return records;
    }

    private async Task<TlsCertificateInfo?> TryGetTlsCertificateAsync(string domain, List<string> errors, CancellationToken ct)
    {
        try
        {
            var cert = await tlsCertificateFetcher.FetchAsync(domain, ct);
            if (cert is null) return null;

            return new TlsCertificateInfo(
                cert.Subject,
                cert.Issuer,
                cert.NotBefore.ToUniversalTime(),
                cert.NotAfter.ToUniversalTime(),
                ExtractSubjectAlternativeNames(cert));
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            errors.Add($"TLS certificate fetch failed: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractSubjectAlternativeNames(X509Certificate2 cert)
    {
        foreach (var extension in cert.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension sanExtension)
            {
                return sanExtension.EnumerateDnsNames().ToList();
            }
        }
        return [];
    }

    private async Task<IpReputationInfo?> TryGetIpReputationAsync(string ip, List<string> errors, CancellationToken ct)
    {
        var apiKey = options.Value.FocsecApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.focsec.com/v1/ip/{Uri.EscapeDataString(ip)}");
            request.Headers.TryAddWithoutValidation("Authorization", apiKey);

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"Focsec lookup returned {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null) return null;

            return new IpReputationInfo(
                IsVpn: AsBool(root["is_vpn"]),
                IsProxy: AsBool(root["is_proxy"]),
                IsTor: AsBool(root["is_tor"]),
                IsBot: AsBool(root["is_bot"]),
                CountryCode: AsString(root["iso_code"]));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Focsec lookup failed for {Ip}", ip);
            errors.Add("Focsec lookup failed");
            return null;
        }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static bool AsBool(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False && value.TryGetValue(out bool b) && b;
}
