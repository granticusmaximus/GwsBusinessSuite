using System.Net;

namespace GwsBusinessSuite.Application.ThreatIntel;

// Thin seam around System.Net.Dns purely so DomainIntelService's DNS lookup can be exercised in
// tests without a real network call - the real implementation (SystemDnsResolver, Infrastructure)
// is a one-line wrapper around Dns.GetHostAddressesAsync.
public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken = default);
}
