using System.Net;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.ThreatIntel;

public enum IndicatorKind
{
    Unknown,
    IpAddress,
    Domain,
    Url,
    Cve,
    Md5,
    Sha1,
    Sha256
}

public sealed record ClassifiedIndicator(IndicatorKind Kind, string Value, string? Host)
{
    public bool IsHash => Kind is IndicatorKind.Md5 or IndicatorKind.Sha1 or IndicatorKind.Sha256;
}

// Works out what someone typed into the Threat Intelligence search box so it can be routed to the
// right lookups. Also accepts defanged input (hxxp://, example[.]com) since that's how indicators
// are usually pasted from reports.
public static partial class ThreatIndicatorClassifier
{
    public static ClassifiedIndicator Classify(string? input)
    {
        var value = Refang((input ?? string.Empty).Trim());
        if (value.Length == 0) return new(IndicatorKind.Unknown, value, null);

        if (CvePattern().IsMatch(value)) return new(IndicatorKind.Cve, value.ToUpperInvariant(), null);

        if (HexPattern().IsMatch(value))
        {
            var kind = value.Length switch { 32 => IndicatorKind.Md5, 40 => IndicatorKind.Sha1, 64 => IndicatorKind.Sha256, _ => IndicatorKind.Unknown };
            if (kind != IndicatorKind.Unknown) return new(kind, value.ToLowerInvariant(), null);
        }

        if (IPAddress.TryParse(value.Trim('[', ']'), out var ip)) return new(IndicatorKind.IpAddress, ip.ToString(), ip.ToString());

        if (value.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return new(IndicatorKind.Url, value, uri.Host.ToLowerInvariant());
        }

        var host = value.TrimEnd('.').ToLowerInvariant();
        if (DomainPattern().IsMatch(host)) return new(IndicatorKind.Domain, host, host);

        return new(IndicatorKind.Unknown, value, null);
    }

    public static string Refang(string value) => value
        .Replace("hxxps://", "https://", StringComparison.OrdinalIgnoreCase)
        .Replace("hxxp://", "http://", StringComparison.OrdinalIgnoreCase)
        .Replace("[.]", ".", StringComparison.Ordinal)
        .Replace("(.)", ".", StringComparison.Ordinal)
        .Replace("[:]", ":", StringComparison.Ordinal);

    [GeneratedRegex(@"^CVE-\d{4}-\d{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex CvePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]+$")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainPattern();
}
