namespace GwsBusinessSuite.Application.ThreatIntel;

// Same "optional, absent disables the feature rather than failing" convention as
// CameraIntelOptions - a fresh deployment with none of these set still shows real, live
// defend.network briefings (no key needed) and RDAP/DNS/TLS domain lookups (no key needed);
// only the OTX pulses list and Focsec's IP reputation classification go blank until their own
// free, self-registered key is added.
public sealed class ThreatIntelOptions
{
    public const string SectionName = "ThreatIntel";

    // Free, self-registered API key from an AlienVault OTX account (otx.alienvault.com).
    public string OtxApiKey { get; set; } = "";

    // Free-tier, self-registered API key from Focsec (focsec.com) - used only for the IP
    // reputation (VPN/proxy/Tor/bot) classification in the domain/IP investigation panel.
    public string FocsecApiKey { get; set; } = "";
}
