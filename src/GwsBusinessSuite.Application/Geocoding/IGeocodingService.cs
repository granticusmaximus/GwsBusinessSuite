namespace GwsBusinessSuite.Application.Geocoding;

public interface IGeocodingService
{
    Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default);

    // Powers the globe's hover-to-identify feature. Photon-only (delegated straight through) -
    // the Census fallback used for forward search has no named building/business data at all,
    // only address ranges, so it has nothing useful to add here.
    Task<PlaceInfo?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}

// One implementation per provider (mirrors ICameraFeedProvider/CameraDirectoryService) rather
// than a single class juggling two differently-configured HttpClients directly - the typed-client
// pattern already used everywhere else in this app (AddHttpClient<TInterface, TImpl>) only binds
// one HttpClient per class.
public interface IPhotonGeocoder
{
    Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default);

    Task<PlaceInfo?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}

public interface ICensusGeocoder
{
    Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default);
}
