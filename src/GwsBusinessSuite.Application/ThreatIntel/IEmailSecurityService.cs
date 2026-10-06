namespace GwsBusinessSuite.Application.ThreatIntel;

// SPF / DKIM / DMARC / MTA-STS / TLS-RPT posture of a mail domain, from public DNS (over HTTPS)
// and the MTA-STS policy file.
public interface IEmailSecurityService
{
    Task<EmailSecurityReport> CheckAsync(string domain, CancellationToken cancellationToken = default);
}

public sealed record EmailSecurityReport(
    string Domain,
    string Grade,
    IReadOnlyList<string> MxHosts,
    string? SpfRecord,
    string? DmarcRecord,
    string? DmarcPolicy,
    IReadOnlyList<string> DkimSelectorsFound,
    string? MtaStsMode,
    string? TlsRptRecord,
    IReadOnlyList<EmailSecurityIssue> Issues,
    IReadOnlyList<string> Errors);

public sealed record EmailSecurityIssue(string Code, string Severity, string Title, string Detail);
