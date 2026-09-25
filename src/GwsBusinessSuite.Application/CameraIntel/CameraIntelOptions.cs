namespace GwsBusinessSuite.Application.CameraIntel;

// All three keys are free to self-register for (WSDOT: https://wsdot.wa.gov/traffic/api/,
// Windy: https://api.windy.com/webcams/dev, Datumfeed: POST https://datumfeed.com/api/keys with
// {"email":"..."}) but require the account holder's own registration - there is no shared/
// bundled key this app can ship with. Same "optional, absent disables the feature rather than
// failing" convention as OllamaWeb__ApiKey (see Program.cs) for WSDOT/Windy - a provider with no
// key configured just returns an empty camera list for its source instead of throwing.
//
// DatumfeedApiKey is different: Datumfeed's anonymous tier (60 req/h per IP) already works with
// no key at all, covering ~9,000 real cameras across Austin TX, California (Caltrans), Ontario,
// Ottawa, Toronto, London, and (whenever WsdotAccessCode is left empty) Washington State's own
// cameras too, re-published through Datumfeed - see DatumfeedCameraProvider. The key only raises
// that rate limit to 3600 req/h, it doesn't gate the feature on/off. GDOT (Georgia) needs no key
// at all, ever - see GdotTrafficCameraProvider. A fresh deployment with none of these three keys
// configured still shows real cameras in Georgia and everywhere Datumfeed covers, including
// Washington - registering for WSDOT's own key is genuinely optional, not required for that
// coverage, only for switching to WSDOT's own direct (non-Datumfeed-mediated) feed.
public sealed class CameraIntelOptions
{
    public const string SectionName = "CameraIntel";

    public string WsdotAccessCode { get; set; } = "";
    public string WindyApiKey { get; set; } = "";
    public string DatumfeedApiKey { get; set; } = "";

    // Free, self-registered access token from a Mapillary developer account
    // (mapillary.com/developer) - same optional, absent-disables-the-feature shape as the three
    // above. KartaView (formerly OpenStreetCam) needs no key at all - see KartaViewProvider.
    public string MapillaryAccessToken { get; set; } = "";
}
