namespace GwsBusinessSuite.Application.Geocoding;

// Overwatch Grid's location search. Both providers are free and need no API key/registration -
// kept server-side (not fetched straight from the browser) for two different reasons per
// provider: Photon's docs ask for a descriptive identifying User-Agent, which a browser fetch()
// can never set (same reason NwsAlertsService is server-side); the Census geocoder simply isn't
// CORS-open at all (confirmed live - no access-control-allow-origin header), so a direct browser
// fetch() would just fail outright regardless of headers.
//
// Photon (photon.komoot.io) is tried first - free, public, OSM-based, and it covers places,
// landmarks, and international queries well. It replaced Nominatim (the original provider for
// this feature) after OSM Foundation's usage-policy enforcement started returning "Access
// denied" for every request this app made, both here and for the tile server this page used to
// use for its basemap.
//
// OSM-based geocoders are volunteer-mapped and frequently have poor coverage of exact
// residential street addresses in the US, even for real, current addresses - a real, confirmed
// gap (a live account's own home address came back empty), not a hypothetical one. The US
// Census Bureau's own geocoder is built specifically for exact US address matching against
// TIGER/Line reference data and is tried as a fallback whenever Photon comes up empty.
public sealed class GeocodingService(IPhotonGeocoder photon, ICensusGeocoder census) : IGeocodingService
{
    public async Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return await photon.GeocodeAsync(query, cancellationToken)
            ?? await census.GeocodeAsync(query, cancellationToken);
    }
}
