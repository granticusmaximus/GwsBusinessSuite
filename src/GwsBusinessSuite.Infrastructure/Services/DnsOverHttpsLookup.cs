using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// DNS-over-HTTPS JSON API - free, no key, verified live 2026-10-05 for MX/TXT/NS/CAA/CNAME
// against both resolvers. Cloudflare needs "accept: application/dns-json"; Google's /resolve
// returns the same shape. Status 0 = NOERROR, 3 = NXDOMAIN (no records, not a failure).
// TXT answers arrive quoted and long ones are split into several quoted strings, which are joined
// back into one value here. Answers are cached for 5 minutes.
public sealed class DnsOverHttpsLookup(HttpClient httpClient, IMemoryCache cache, ILogger<DnsOverHttpsLookup> logger) : IDnsRecordLookup
{
    private static readonly string[] Resolvers =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve"
    ];

    private static readonly Dictionary<string, int> TypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, ["NS"] = 2, ["CNAME"] = 5, ["SOA"] = 6, ["MX"] = 15, ["TXT"] = 16, ["AAAA"] = 28, ["CAA"] = 257
    };

    public async Task<IReadOnlyList<string>> QueryAsync(string name, string recordType, CancellationToken cancellationToken = default)
    {
        name = name.Trim().TrimEnd('.').ToLowerInvariant();
        recordType = recordType.Trim().ToUpperInvariant();
        if (!TypeCodes.TryGetValue(recordType, out var typeCode)) throw new ArgumentException($"Unsupported DNS record type {recordType}.", nameof(recordType));

        var cacheKey = $"threat-intel:doh:{recordType}:{name}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null) return cached;

        Exception? lastError = null;
        foreach (var resolver in Resolvers)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{resolver}?name={Uri.EscapeDataString(name)}&type={recordType}");
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var answers = ParseAnswers(await response.Content.ReadAsStringAsync(cancellationToken), typeCode);
                cache.Set(cacheKey, answers, TimeSpan.FromMinutes(5));
                return answers;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastError = ex;
                logger.LogDebug(ex, "DNS-over-HTTPS query {Type} {Name} via {Resolver} failed", recordType, name, resolver);
            }
        }

        throw new HttpRequestException($"No DNS-over-HTTPS resolver answered for {recordType} {name}.", lastError);
    }

    internal static IReadOnlyList<string> ParseAnswers(string json, int typeCode)
    {
        var root = JsonNode.Parse(json);
        var status = root?["Status"]?.GetValue<int>() ?? 2;
        if (status == 3) return [];
        if (status != 0) throw new JsonException($"DNS resolver returned status {status}.");

        var results = new List<string>();
        foreach (var answer in root?["Answer"]?.AsArray() ?? [])
        {
            // A CNAME chain answers with the CNAME records first; keep only the type asked for.
            if (answer?["type"]?.GetValue<int>() != typeCode) continue;
            var data = answer["data"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(data)) continue;
            results.Add(typeCode == 16 ? JoinTxt(data) : data.Trim());
        }

        return results;
    }

    // "v=spf1 include:a" "nd more" -> v=spf1 include:and more
    private static string JoinTxt(string data)
    {
        var trimmed = data.Trim();
        if (!trimmed.StartsWith('"')) return trimmed;
        var parts = new List<string>();
        var inQuote = false;
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length) { current.Append(trimmed[++i]); continue; }
            if (c == '"')
            {
                if (inQuote) { parts.Add(current.ToString()); current.Clear(); }
                inQuote = !inQuote;
                continue;
            }
            if (inQuote) current.Append(c);
        }
        return string.Concat(parts);
    }
}
