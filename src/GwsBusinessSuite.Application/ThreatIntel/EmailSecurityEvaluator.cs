using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.ThreatIntel;

// Pure grading of what DNS returned, kept separate from the lookups so every rule is unit-tested.
public static class EmailSecurityEvaluator
{
    // DKIM keys live at <selector>._domainkey.<domain> and selectors can't be listed, so only
    // the common ones (Google Workspace, Microsoft 365, Zoho, Proton, Fastmail, generic) are tried.
    public static readonly string[] CommonDkimSelectors =
        ["google", "selector1", "selector2", "default", "dkim", "mail", "k1", "s1", "s2", "zoho", "protonmail", "fm1", "smtp"];

    public sealed record Inputs(
        string Domain,
        IReadOnlyList<string> MxHosts,
        IReadOnlyList<string> RootTxt,
        IReadOnlyList<string> DmarcTxt,
        IReadOnlyList<string> DkimSelectorsFound,
        IReadOnlyList<string> MtaStsTxt,
        string? MtaStsPolicy,
        IReadOnlyList<string> TlsRptTxt);

    public static EmailSecurityReport Evaluate(Inputs input, IReadOnlyList<string> errors)
    {
        var issues = new List<EmailSecurityIssue>();
        var sendsMail = input.MxHosts.Count > 0;

        var spfRecords = input.RootTxt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        var spf = spfRecords.FirstOrDefault();
        if (spfRecords.Count == 0)
        {
            issues.Add(new("spf-missing", sendsMail ? ThreatFindingSeverities.High : ThreatFindingSeverities.Medium, "No SPF record",
                sendsMail
                    ? "Anyone can send mail claiming to be from this domain. Publish a TXT record such as \"v=spf1 include:<your provider> -all\"."
                    : "This domain has no MX records. Publish \"v=spf1 -all\" so nobody can send mail as it."));
        }
        else if (spfRecords.Count > 1)
        {
            issues.Add(new("spf-multiple", ThreatFindingSeverities.High, "More than one SPF record",
                "Receivers treat multiple v=spf1 records as a permanent error, so SPF fails for every message. Merge them into one."));
        }
        else
        {
            var mechanisms = spf!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mechanisms.Contains("+all", StringComparer.OrdinalIgnoreCase) || mechanisms.Contains("all", StringComparer.OrdinalIgnoreCase))
                issues.Add(new("spf-pass-all", ThreatFindingSeverities.Critical, "SPF allows every server",
                    "The record ends in +all, which authorizes any server on the internet to send as this domain. Use -all or ~all."));
            else if (mechanisms.Contains("?all", StringComparer.OrdinalIgnoreCase))
                issues.Add(new("spf-neutral", ThreatFindingSeverities.Medium, "SPF is neutral (?all)",
                    "?all tells receivers SPF says nothing about unlisted servers. Use ~all or -all."));
            else if (!mechanisms.Any(m => m.EndsWith("all", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new("spf-no-all", ThreatFindingSeverities.Low, "SPF has no \"all\" mechanism",
                    "Without a final -all or ~all, mail from unlisted servers isn't flagged."));
        }

        var dmarc = input.DmarcTxt.FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        var tags = ParseTags(dmarc);
        var policy = tags.GetValueOrDefault("p")?.ToLowerInvariant();
        if (dmarc is null)
        {
            issues.Add(new("dmarc-missing", ThreatFindingSeverities.High, "No DMARC record",
                "Receivers have no instruction for mail that fails SPF/DKIM, so spoofed mail is often delivered. Publish _dmarc TXT \"v=DMARC1; p=quarantine; rua=mailto:...\"."));
        }
        else
        {
            if (policy == "none")
                issues.Add(new("dmarc-none", ThreatFindingSeverities.Medium, "DMARC policy is \"none\" (monitor only)",
                    "Spoofed mail is reported but still delivered. Once reports look clean, move to p=quarantine, then p=reject."));
            else if (policy is not ("quarantine" or "reject"))
                issues.Add(new("dmarc-invalid", ThreatFindingSeverities.High, "DMARC record has no valid policy",
                    "The p= tag must be none, quarantine or reject; receivers ignore the record otherwise."));
            if (int.TryParse(tags.GetValueOrDefault("pct"), out var pct) && pct < 100 && policy is "quarantine" or "reject")
                issues.Add(new("dmarc-pct", ThreatFindingSeverities.Low, $"DMARC applies to only {pct}% of mail",
                    "The rest of failing mail is treated as p=none. Raise pct to 100 when ready."));
            if (!tags.ContainsKey("rua"))
                issues.Add(new("dmarc-no-reports", ThreatFindingSeverities.Low, "DMARC asks for no reports",
                    "Without rua=mailto:..., you never see who is sending as this domain."));
        }

        if (sendsMail && input.DkimSelectorsFound.Count == 0)
        {
            issues.Add(new("dkim-not-found", ThreatFindingSeverities.Low, "No DKIM key found on common selectors",
                $"Checked {string.Join(", ", CommonDkimSelectors)}. If your provider uses another selector this may be fine; otherwise turn on DKIM signing."));
        }

        var stsTxt = input.MtaStsTxt.FirstOrDefault(t => t.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase));
        var stsMode = ParsePolicyMode(input.MtaStsPolicy);
        if (sendsMail)
        {
            if (stsTxt is null)
                issues.Add(new("mta-sts-missing", ThreatFindingSeverities.Info, "No MTA-STS",
                    "Without MTA-STS, mail sent to this domain can be downgraded to unencrypted delivery by an attacker on the path."));
            else if (stsMode is null)
                issues.Add(new("mta-sts-policy-unreachable", ThreatFindingSeverities.Medium, "MTA-STS policy file missing",
                    $"_mta-sts is published but https://mta-sts.{input.Domain}/.well-known/mta-sts.txt couldn't be read, so senders ignore it."));
            else if (stsMode == "testing")
                issues.Add(new("mta-sts-testing", ThreatFindingSeverities.Info, "MTA-STS is in testing mode", "Switch mode to enforce once TLS reports are clean."));

            if (!input.TlsRptTxt.Any(t => t.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new("tls-rpt-missing", ThreatFindingSeverities.Info, "No TLS-RPT reporting",
                    "Publish _smtp._tls TXT \"v=TLSRPTv1; rua=mailto:...\" to hear about delivery encryption failures."));
        }

        return new EmailSecurityReport(
            input.Domain,
            Grade(issues),
            input.MxHosts,
            spf,
            dmarc,
            policy,
            input.DkimSelectorsFound,
            stsMode,
            input.TlsRptTxt.FirstOrDefault(),
            issues,
            errors);
    }

    // A: nothing above info. B: low only. C: one medium. D: one high or several mediums. F: critical or 2+ high.
    public static string Grade(IReadOnlyList<EmailSecurityIssue> issues)
    {
        int Count(string severity) => issues.Count(i => i.Severity == severity);
        if (Count(ThreatFindingSeverities.Critical) > 0 || Count(ThreatFindingSeverities.High) >= 2) return "F";
        if (Count(ThreatFindingSeverities.High) == 1 || Count(ThreatFindingSeverities.Medium) >= 2) return "D";
        if (Count(ThreatFindingSeverities.Medium) == 1) return "C";
        if (Count(ThreatFindingSeverities.Low) > 0) return "B";
        return "A";
    }

    public static Dictionary<string, string> ParseTags(string? record)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (record ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) tags[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return tags;
    }

    public static string? ParsePolicyMode(string? policy)
    {
        foreach (var line in (policy ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("mode:", StringComparison.OrdinalIgnoreCase)) return trimmed[5..].Trim().ToLowerInvariant();
        }
        return null;
    }
}
