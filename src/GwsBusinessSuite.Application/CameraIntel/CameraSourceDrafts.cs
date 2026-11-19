namespace GwsBusinessSuite.Application.CameraIntel;

// Whether a camera source is switched off (CameraSourceReview.IsOff). Read on every view, so an
// implementation answers from memory.
public interface ICameraSourceSwitch
{
    bool IsOff(string sourceName);
}

// What each camera source's terms say, as drafted for Grant to confirm (19-11-26, his choice:
// "I draft, you confirm"). Only what the integration notes in each provider's code record is
// written here; every other source says its terms haven't been read yet. The terms link is
// where to read them. Nothing here is a legal finding until Grant confirms it on the
// Camera sources page.
public static class CameraSourceDrafts
{
    public sealed record Draft(string Basis, string? TermsUrl);

    public const string NotRead = "Not read yet. Public camera images published by the agency; read its terms before confirming.";

    private static readonly Dictionary<string, Draft> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Transport for NSW"] = new("Open data, Creative Commons Attribution (CC BY), per the integration notes.",
            "https://opendata.transport.nsw.gov.au/dataset/live-traffic-cameras"),
        ["City of Toronto"] = new("Open data, Open Government Licence - Toronto, per the integration notes.",
            "https://open.toronto.ca/dataset/traffic-cameras/"),
        ["City of Calgary"] = new(NotRead, "https://data.calgary.ca/Transportation-Transit/Traffic-Cameras/k7p9-kppz"),
        ["ALDOT"] = new(NotRead, "https://www.algotraffic.com"),
        ["Caltrans"] = new(NotRead, "https://cwwp2.dot.ca.gov"),
        ["CDOT"] = new(NotRead, "https://www.cotrip.org"),
        ["Datumfeed"] = new("Aggregator: each camera's registry entry carries its own licence link; read Datumfeed's terms too.", "https://datumfeed.com"),
        ["DelDOT"] = new(NotRead, "https://deldot.gov"),
        ["DriveBC"] = new(NotRead, "https://drivebc.ca"),
        ["Estonia Tark Tee"] = new(NotRead, "https://tarktee.ee/"),
        ["Fintraffic"] = new(NotRead, "https://www.digitraffic.fi/en/"),
        ["FL511"] = new(NotRead, "https://fl511.com"),
        ["GDOT"] = new(NotRead, "https://511ga.org"),
        ["HDOT"] = new(NotRead, "https://hidot.hawaii.gov"),
        ["HK Transport Department"] = new(NotRead, "https://data.gov.hk"),
        ["Vegagerðin"] = new(NotRead, "https://www.vegagerdin.is"),
        ["IDOT"] = new(NotRead, "https://www.travelmidwest.com"),
        ["Iowa DOT"] = new(NotRead, "https://iowadot.gov"),
        ["KYTC"] = new(NotRead, "https://goky.ky.gov"),
        ["CITA"] = new(NotRead, "https://www.cita.lu"),
        ["Madrid Traffic"] = new(NotRead, "https://informo.madrid.es"),
        ["MDOT MiDrive"] = new(NotRead, "https://mdotjboss.state.mi.us/MiDrive/"),
        ["MoDOT"] = new(NotRead, "https://www.modot.org"),
        ["NMRoads"] = new(NotRead, "https://www.nmroads.com"),
        ["NDDOT"] = new(NotRead, "https://travel.dot.nd.gov"),
        ["Statens vegvesen"] = new(NotRead, "https://www.vegvesen.no/trafikk/"),
        ["NYC DOT"] = new(NotRead, "https://webcams.nyctmc.org"),
        ["NZTA"] = new(NotRead, "https://www.trafficnz.info"),
        ["ODOT"] = new(NotRead, "https://tripcheck.com"),
        ["QLD Traffic"] = new(NotRead, "https://www.qldtraffic.qld.gov.au"),
        ["RIDOT"] = new(NotRead, "https://www.dot.ri.gov"),
        ["Singapore LTA"] = new(NotRead, "https://data.gov.sg"),
        ["SCDOT"] = new(NotRead, "https://www.scdot.org"),
        ["TxDOT"] = new(NotRead, "https://drivetexas.org"),
        ["VDOT"] = new(NotRead, "https://www.511virginia.org"),
        ["WV511"] = new(NotRead, "https://wv511.org"),
        ["WSDOT"] = new(NotRead, "https://wsdot.wa.gov/traffic/"),
    };

    // A source with no entry (a 511 or Iteris region, Windy) still gets a draft saying it's unread.
    public static Draft For(string sourceName) => Known.TryGetValue(sourceName, out var draft) ? draft : new(NotRead, null);
}
