namespace GwsBusinessSuite.Application.CameraIntel;

// All three keys are free to self-register for (WSDOT: https://wsdot.wa.gov/traffic/api/,
// Windy: https://api.windy.com/webcams/dev, Datumfeed: POST https://datumfeed.com/api/keys with
// {"email":"..."}) but require the account holder's own registration - there is no shared/
// bundled key this app can ship with. Same "optional, absent disables the feature rather than
// failing" convention as OllamaWeb__ApiKey (see Program.cs) for WSDOT/Windy - a provider with no
// key configured just returns an empty camera list for its source instead of throwing.
//
// DatumfeedApiKey is different: Datumfeed's anonymous tier (60 req/h per IP) already works with
// no key at all, covering ~7,500 real cameras across Austin TX, California (Caltrans), Ontario,
// Ottawa, Toronto, and London (see DatumfeedCameraProvider) - the key only raises that limit to
// 3600 req/h for heavier use, it doesn't gate the feature on/off. GDOT (Georgia) needs no key at
// all, ever - see GdotTrafficCameraProvider. A fresh deployment with zero keys configured still
// shows real cameras in Georgia and (rate-limited) everywhere Datumfeed covers.
public sealed class CameraIntelOptions
{
    public const string SectionName = "CameraIntel";

    public string WsdotAccessCode { get; set; } = "";
    public string WindyApiKey { get; set; } = "";
    public string DatumfeedApiKey { get; set; } = "";
}
