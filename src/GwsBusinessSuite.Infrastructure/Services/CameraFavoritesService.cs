using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors SentinelWorkspaceService's find-or-create + username-normalization shape for its own
// per-user favorites (SentinelNavigationEntry).
public sealed class CameraFavoritesService(IAppDbContext dbContext, TimeProvider timeProvider) : ICameraFavoritesService
{
    public async Task<IReadOnlyList<CameraFeed>> GetFavoritesAsync(string username, CancellationToken cancellationToken = default)
    {
        // SQLite can't translate an ORDER BY on DateTimeOffset - materialize first, then sort
        // client-side (see this codebase's own established SQLite+DateTimeOffset pattern).
        var normalizedUser = NormalizeUsername(username);
        var favorites = await dbContext.CameraFavorites.AsNoTracking()
            .Where(f => f.Username == normalizedUser)
            .ToListAsync(cancellationToken);
        return favorites
            .OrderByDescending(f => f.CreatedAt)
            .Select(ToCameraFeed)
            .ToList();
    }

    public async Task<bool> IsFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        return await dbContext.CameraFavorites.AsNoTracking()
            .AnyAsync(f => f.Username == normalizedUser && f.CameraId == cameraId, cancellationToken);
    }

    public async Task AddFavoriteAsync(string username, CameraFeed camera, CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var existing = await dbContext.CameraFavorites.FirstOrDefaultAsync(
            f => f.Username == normalizedUser && f.CameraId == camera.Id, cancellationToken);
        if (existing is not null) return;

        var favorite = new CameraFavorite
        {
            Username = normalizedUser,
            CameraId = camera.Id,
            Name = camera.Name,
            Latitude = camera.Latitude,
            Longitude = camera.Longitude,
            StreamUrl = camera.StreamUrl,
            StreamKind = camera.StreamKind.ToString(),
            SourceName = camera.SourceName,
            SourceAttributionUrl = camera.SourceAttributionUrl,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = normalizedUser
        };
        await dbContext.CameraFavorites.AddAsync(favorite, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var existing = await dbContext.CameraFavorites.FirstOrDefaultAsync(
            f => f.Username == normalizedUser && f.CameraId == cameraId, cancellationToken);
        if (existing is null) return;

        dbContext.CameraFavorites.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static CameraFeed ToCameraFeed(CameraFavorite favorite) => new(
        favorite.CameraId,
        favorite.Name,
        favorite.Latitude,
        favorite.Longitude,
        favorite.StreamUrl,
        Enum.Parse<CameraStreamKind>(favorite.StreamKind),
        favorite.SourceName,
        favorite.SourceAttributionUrl);

    private static string NormalizeUsername(string username) =>
        string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant();
}
