namespace GwsBusinessSuite.Application.ThreatIntel;

// Public-exposure checks that complement IDomainIntelService's registration/DNS/TLS lookup:
//   IP     - open ports and known CVEs (Shodan InternetDB), Tor exit node (Tor Project's bulk
//            exit list), hijacked/criminal netblock (Spamhaus DROP).
//   Domain - certificate-transparency hostnames (SSLMate certspotter), recent public urlscan.io
//            scans, and first/last Wayback Machine snapshot.
// All free, no key. Never throws - each source fails on its own and is noted in Errors.
public interface IExposureIntelService
{
    Task<ExposureIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default);
}

public sealed record ExposureIntelResult(
    string Target,
    bool IsIpAddress,
    IpExposureInfo? IpExposure,
    bool? IsTorExitNode,
    SpamhausDropListing? SpamhausDrop,
    IReadOnlyList<string> CertificateHostnames,
    IReadOnlyList<UrlscanScan> RecentScans,
    ArchiveHistory? Archive,
    IReadOnlyList<string> Errors);

// Shodan InternetDB's own fields: ports seen open, CVE ids matched to the detected software,
// tags (e.g. "self-signed", "vpn"), CPEs and reverse/forward hostnames.
public sealed record IpExposureInfo(
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> Vulnerabilities,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Cpes,
    IReadOnlyList<string> Hostnames);

public sealed record SpamhausDropListing(string Cidr, string SblId);

public sealed record UrlscanScan(
    DateTimeOffset? ScannedAt,
    string PageUrl,
    string? Title,
    string? Ip,
    string? Country,
    string? Server,
    string ResultUrl);

public sealed record ArchiveHistory(DateTimeOffset? FirstSnapshot, DateTimeOffset? LastSnapshot, string HistoryUrl);
