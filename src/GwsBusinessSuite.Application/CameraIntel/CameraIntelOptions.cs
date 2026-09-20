namespace GwsBusinessSuite.Application.CameraIntel;

// Both keys are free to self-register for (WSDOT: https://wsdot.wa.gov/traffic/api/, Windy:
// https://api.windy.com/webcams/dev) but require the account holder's own registration - there
// is no shared/bundled key this app can ship with. Same "optional, absent disables the feature
// rather than failing" convention as OllamaWeb__ApiKey (see Program.cs) - a provider with no key
// configured just returns an empty camera list for its source instead of throwing, so a fresh
// deployment boots cleanly with zero cameras until someone adds keys.
public sealed class CameraIntelOptions
{
    public const string SectionName = "CameraIntel";

    public string WsdotAccessCode { get; set; } = "";
    public string WindyApiKey { get; set; } = "";
}
