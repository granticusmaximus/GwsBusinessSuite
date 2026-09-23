namespace GwsBusinessSuite.Application.CameraIntel;

// Per-admin-account bookmarks for Overwatch cameras, so a specific camera can be returned
// to quickly regardless of whether it's currently in the globe's bbox-queried pin set. Returns
// CameraFeed (not the Domain CameraFavorite entity) so the Razor page can treat a favorite
// exactly like any other camera - same pin payload shape, same "open stream panel" flow.
public interface ICameraFavoritesService
{
    Task<IReadOnlyList<CameraFeed>> GetFavoritesAsync(string username, CancellationToken cancellationToken = default);

    Task<bool> IsFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default);

    Task AddFavoriteAsync(string username, CameraFeed camera, CancellationToken cancellationToken = default);

    Task RemoveFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default);
}
