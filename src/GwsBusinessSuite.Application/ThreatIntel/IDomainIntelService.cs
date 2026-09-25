namespace GwsBusinessSuite.Application.ThreatIntel;

public interface IDomainIntelService
{
    // Accepts either a domain name or an IP address (v4/v6) and fans out to whichever of
    // RDAP/DNS/TLS/Focsec apply. Never throws - each section fails independently (a domain with
    // no live web server just gets no TlsCertificate, not a failed whole request) and every
    // partial failure is recorded in Errors as a plain, user-facing message.
    Task<DomainIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default);
}

public sealed record DomainIntelResult(
    string Target,
    bool IsIpAddress,
    RegistrationInfo? Registration,
    IReadOnlyList<DnsRecordInfo> DnsRecords,
    TlsCertificateInfo? TlsCertificate,
    IpReputationInfo? IpReputation,
    IReadOnlyList<string> Errors);

public sealed record RegistrationInfo(
    string? Registrar,
    DateTimeOffset? Registered,
    DateTimeOffset? Expires,
    IReadOnlyList<string> Nameservers,
    IReadOnlyList<string> Statuses);

public sealed record DnsRecordInfo(string RecordType, string Value);

public sealed record TlsCertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> SubjectAlternativeNames);

// Focsec's own field names; left nullable throughout since a request with no configured
// FocsecApiKey returns null for this whole record rather than a half-populated one.
public sealed record IpReputationInfo(bool IsVpn, bool IsProxy, bool IsTor, bool IsBot, string? CountryCode);
