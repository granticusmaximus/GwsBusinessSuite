using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Fans out a domain or IP to four independent, free data sources - each fails on its own
// (recorded in DomainIntelResult.Errors) rather than failing the whole investigation:
//
//   1. RDAP (registration data) via rdap.org - a free, no-key, no-registration redirecting
//      bootstrap service confirmed directly against both a domain (redirects to Verisign's own
//      RDAP server for .com) and an IP (redirects to ARIN's). Chosen over hand-rolling IANA's
//      own TLD-to-registry bootstrap file, since rdap.org already does exactly that server-side
//      - HttpClient's default AllowAutoRedirect follows it with no extra code. Some TLDs'
//      registries aren't wired into rdap.org's redirect table (confirmed directly: .io returns
//      a real 404, not a redirect) - a real, accepted gap, not a bug here. Also confirmed
//      directly (a real, non-obvious finding, not a guess): rdap.org's Cloudflare front returns
//      a 403 for a request with no User-Agent header at all - the .NET HttpClient default - so
//      the DI registration for this service's HttpClient sets one explicitly.
//   2. DNS (A/AAAA only) via System.Net.Dns - genuinely built into the runtime, zero
//      dependency. Note: .NET's Dns class only resolves hostnames to addresses; it has no public
//      API for arbitrary record types (MX/TXT/etc.) without a third-party resolver library
//      (e.g. DnsClient.NET), which is deliberately not added here - a real scope narrowing from
//      the original plan's assumption, not an oversight.
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
    ILogger<DomainIntelService> logger) : IDomainIntelService
{
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
            using var response = await httpClient.GetAsync($"https://rdap.org/{path}", ct);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"RDAP lookup returned {(int)response.StatusCode} {response.StatusCode}");
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
        try
        {
            var addresses = await dnsResolver.ResolveAsync(domain, ct);
            return addresses
                .Select(a => new DnsRecordInfo(
                    a.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A",
                    a.ToString()))
                .ToList();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            errors.Add($"DNS lookup failed: {ex.Message}");
            return [];
        }
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
