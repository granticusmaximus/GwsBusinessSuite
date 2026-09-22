using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CameraFavoritesServiceTests
{
    private static readonly CameraFeed TestCamera = new(
        "gdot-123", "I-85 @ Exit 12", 33.75, -84.39,
        "https://511ga.org/cameras/gdot-123.jpg", CameraStreamKind.Snapshot,
        "GDOT", "https://511ga.org");

    [Fact]
    public async Task AddFavoriteAsync_ThenGetFavoritesAsync_ShouldRoundTripTheCamera()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new CameraFavoritesService(db, TimeProvider.System);

        await service.AddFavoriteAsync("Grant", TestCamera);
        var favorites = await service.GetFavoritesAsync("grant");

        favorites.Should().ContainSingle();
        favorites[0].Id.Should().Be(TestCamera.Id);
        favorites[0].Name.Should().Be(TestCamera.Name);
        favorites[0].Latitude.Should().Be(TestCamera.Latitude);
        favorites[0].Longitude.Should().Be(TestCamera.Longitude);
        favorites[0].StreamUrl.Should().Be(TestCamera.StreamUrl);
        favorites[0].StreamKind.Should().Be(CameraStreamKind.Snapshot);
        favorites[0].SourceName.Should().Be(TestCamera.SourceName);
    }

    [Fact]
    public async Task AddFavoriteAsync_CalledTwiceForTheSameCamera_ShouldNotDuplicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new CameraFavoritesService(db, TimeProvider.System);

        await service.AddFavoriteAsync("grant", TestCamera);
        await service.AddFavoriteAsync("grant", TestCamera);

        var favorites = await service.GetFavoritesAsync("grant");
        favorites.Should().ContainSingle();
    }

    [Fact]
    public async Task IsFavoriteAsync_ShouldReturnTrueOnlyAfterAdding()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new CameraFavoritesService(db, TimeProvider.System);

        (await service.IsFavoriteAsync("grant", TestCamera.Id)).Should().BeFalse();

        await service.AddFavoriteAsync("grant", TestCamera);
        (await service.IsFavoriteAsync("grant", TestCamera.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveFavoriteAsync_ShouldDeleteIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new CameraFavoritesService(db, TimeProvider.System);

        await service.AddFavoriteAsync("grant", TestCamera);
        await service.RemoveFavoriteAsync("grant", TestCamera.Id);

        (await service.IsFavoriteAsync("grant", TestCamera.Id)).Should().BeFalse();
        (await service.GetFavoritesAsync("grant")).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveFavoriteAsync_ForACameraThatWasNeverFavorited_ShouldNotThrow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new CameraFavoritesService(db, TimeProvider.System);

        var act = async () => await service.RemoveFavoriteAsync("grant", "never-favorited");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Favorites_ShouldBeIsolatedPerUsername()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new CameraFavoritesService(db, TimeProvider.System);

        await service.AddFavoriteAsync("grant", TestCamera);

        (await service.GetFavoritesAsync("someone-else")).Should().BeEmpty();
        (await service.IsFavoriteAsync("someone-else", TestCamera.Id)).Should().BeFalse();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
