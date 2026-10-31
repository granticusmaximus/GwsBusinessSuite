namespace GwsBusinessSuite.Application.Growth;

// Where a visitor's OK is needed before the public site stores a visitor id or loads an
// affiliate partner's script (31-10-26, js/consent.js): the EU/EEA (GDPR and the ePrivacy rules),
// the UK and Switzerland. Unknown - no location database, or a private address - asks too.
public static class ConsentRegions
{
    private static readonly HashSet<string> OptIn = new(StringComparer.OrdinalIgnoreCase)
    {
        // EU
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU",
        "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
        // EEA, UK, Switzerland
        "IS", "LI", "NO", "GB", "CH"
    };

    public static bool RequiresOptIn(string? countryCode) =>
        string.IsNullOrWhiteSpace(countryCode) || OptIn.Contains(countryCode.Trim());
}
