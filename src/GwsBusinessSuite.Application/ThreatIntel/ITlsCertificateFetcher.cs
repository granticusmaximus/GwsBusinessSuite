using System.Security.Cryptography.X509Certificates;

namespace GwsBusinessSuite.Application.ThreatIntel;

// Thin seam around a live SslStream handshake purely so DomainIntelService's TLS-certificate
// inspection can be exercised in tests without a real network call - the real implementation
// (SslStreamTlsCertificateFetcher, Infrastructure) connects to port 443 and returns whatever
// certificate the site presents, without validating it (validity itself is a finding, not a
// precondition for inspecting the cert).
public interface ITlsCertificateFetcher
{
    Task<X509Certificate2?> FetchAsync(string hostName, CancellationToken cancellationToken = default);
}
