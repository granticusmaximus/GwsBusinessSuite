namespace GwsBusinessSuite.Application.ThreatIntel;

// Any DNS record type (MX, TXT, NS, CAA, CNAME...). System.Net.Dns only resolves addresses, so
// this goes through DNS-over-HTTPS JSON (Cloudflare, Google as fallback) - no resolver library.
public interface IDnsRecordLookup
{
    // Presentation-format answers for the type, e.g. "10 alt3.aspmx.l.google.com." for MX.
    // Empty when the name has no such records; throws HttpRequestException when no resolver answers.
    Task<IReadOnlyList<string>> QueryAsync(string name, string recordType, CancellationToken cancellationToken = default);
}

public static class DnsRecordTypes
{
    // Shown on the investigation panel, in this order, after the A/AAAA addresses.
    public static readonly string[] Investigated = ["CNAME", "MX", "NS", "TXT", "CAA"];
}
