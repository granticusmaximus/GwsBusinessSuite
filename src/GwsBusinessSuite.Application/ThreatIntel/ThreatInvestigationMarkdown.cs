using System.Text;

namespace GwsBusinessSuite.Application.ThreatIntel;

// The Sentinel page an exported investigation becomes. Malicious values are written defanged
// (hxxp, [.]) so nothing on the page is a live link to attacker infrastructure.
public static class ThreatInvestigationMarkdown
{
    public static string Build(string query, string summary, DateTimeOffset createdAt, string createdBy, ThreatInvestigationSnapshot snapshot)
    {
        var md = new StringBuilder();
        md.AppendLine($"Investigation of `{Defang(query)}` by {createdBy} on {createdAt:yyyy-MM-dd HH:mm} UTC.");
        md.AppendLine();
        md.AppendLine($"**Summary:** {summary}");
        md.AppendLine();

        if (snapshot.Matches.Count > 0)
        {
            md.AppendLine("## Threat feed matches");
            foreach (var match in snapshot.Matches)
                md.AppendLine($"- **{match.Feed}**: `{Defang(match.MatchedValue)}` - {match.Description}{(match.SeenAt is { } seen ? $" (seen {seen:yyyy-MM-dd})" : "")} - [report]({match.ReferenceUrl})");
            md.AppendLine();
        }

        if (snapshot.Domain is { } domain)
        {
            if (domain.Registration is { } reg)
            {
                md.AppendLine("## Registration");
                md.AppendLine($"- Registrar / org: {reg.Registrar ?? "Unknown"}");
                if (reg.Registered is { } registered) md.AppendLine($"- Registered: {registered:yyyy-MM-dd}");
                if (reg.Expires is { } expires) md.AppendLine($"- Expires: {expires:yyyy-MM-dd}");
                if (reg.Nameservers.Count > 0) md.AppendLine($"- Nameservers: {string.Join(", ", reg.Nameservers)}");
                md.AppendLine();
            }

            if (domain.DnsRecords.Count > 0)
            {
                md.AppendLine("## DNS records");
                foreach (var record in domain.DnsRecords) md.AppendLine($"- {record.RecordType}: `{record.Value}`");
                md.AppendLine();
            }

            if (domain.TlsCertificate is { } tls)
            {
                md.AppendLine("## TLS certificate");
                md.AppendLine($"- Subject: {tls.Subject}");
                md.AppendLine($"- Issuer: {tls.Issuer}");
                md.AppendLine($"- Valid: {tls.NotBefore:yyyy-MM-dd} to {tls.NotAfter:yyyy-MM-dd}");
                md.AppendLine();
            }
        }

        if (snapshot.Exposure is { } exposure)
        {
            if (exposure.IpExposure is { } ip)
            {
                md.AppendLine("## Exposure");
                md.AppendLine($"- Open ports: {(ip.Ports.Count == 0 ? "none seen" : string.Join(", ", ip.Ports))}");
                if (ip.Vulnerabilities.Count > 0) md.AppendLine($"- Known CVEs: {string.Join(", ", ip.Vulnerabilities)}");
                if (exposure.IsTorExitNode is { } tor) md.AppendLine($"- Tor exit node: {(tor ? "yes" : "no")}");
                if (exposure.SpamhausDrop is { } drop) md.AppendLine($"- On Spamhaus DROP: {drop.Cidr} ({drop.SblId})");
                md.AppendLine();
            }

            if (exposure.CertificateHostnames.Count > 0)
            {
                md.AppendLine($"## Hostnames in certificate logs ({exposure.CertificateHostnames.Count})");
                md.AppendLine(string.Join(", ", exposure.CertificateHostnames.Take(100).Select(h => $"`{h}`")));
                md.AppendLine();
            }
        }

        if (snapshot.Cve is { } cve)
        {
            md.AppendLine($"## {cve.CveId}");
            if (cve.CvssScore is { } score) md.AppendLine($"- CVSS {cve.CvssVersion} {score:0.0} {cve.CvssSeverity}");
            if (cve.EpssScore is { } epss) md.AppendLine($"- EPSS {epss:P1}");
            md.AppendLine($"- Known exploited (CISA KEV): {(cve.KnownExploited is null ? "no" : "yes")}");
            if (!string.IsNullOrWhiteSpace(cve.Description)) { md.AppendLine(); md.AppendLine(cve.Description); }
            md.AppendLine();
        }

        return md.ToString().TrimEnd() + "\n";
    }

    public static string Defang(string value) =>
        value.Replace("http://", "hxxp://", StringComparison.OrdinalIgnoreCase)
             .Replace("https://", "hxxps://", StringComparison.OrdinalIgnoreCase)
             .Replace(".", "[.]");
}
