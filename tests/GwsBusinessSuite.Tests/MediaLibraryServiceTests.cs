using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class MediaLibraryServiceTests
{
    // Minimal valid PNG signature plus padding — enough for the magic-byte sniff to
    // recognize it as image/png without needing a real decodable image.
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    [Fact]
    public async Task UploadAsync_ShouldStoreAndReturnRetrievableAsset()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        var uploaded = await service.UploadAsync("logo.png", PngBytes, "Site logo");

        uploaded.FileName.Should().Be("logo.png");
        uploaded.Url.Should().Be($"/media/{uploaded.Id}");

        var fetched = await service.GetContentAsync(uploaded.Id);
        fetched.Should().NotBeNull();
        fetched!.Value.ContentType.Should().Be("image/png");
        fetched.Value.Content.Should().BeEquivalentTo(PngBytes);
    }

    [Fact]
    public async Task UploadAsync_ShouldDetectContentTypeFromBytes_IgnoringAnyClaimedFileName()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        // A .png extension with non-image bytes must still be rejected — the file name
        // is purely cosmetic and is never trusted for the content-type decision.
        var disguised = "not actually an image"u8.ToArray();

        var action = async () => await service.UploadAsync("totally-a-photo.png", disguised, string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectContentWithoutARecognizedImageSignature()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        var action = async () => await service.UploadAsync("doc.pdf", [1, 2, 3], string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectSvg_EvenThoughItIsAnImageMimeType()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);
        var svg = "<svg onload=\"alert(1)\"></svg>"u8.ToArray();

        var action = async () => await service.UploadAsync("icon.svg", svg, string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectFilesOverTheSizeLimit()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);
        var tooLarge = new byte[9 * 1024 * 1024];

        var action = async () => await service.UploadAsync("big.png", tooLarge, string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnMostRecentlyUploadedFirst()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        await service.UploadAsync("first.png", PngBytes, string.Empty);
        await service.UploadAsync("second.png", PngBytes, string.Empty);

        var assets = await service.ListAsync();

        assets.Should().HaveCount(2);
        assets[0].FileName.Should().Be("second.png");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveAssetSoItCanNoLongerBeFetched()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);
        var uploaded = await service.UploadAsync("temp.png", PngBytes, string.Empty);

        await service.DeleteAsync(uploaded.Id);

        (await service.GetContentAsync(uploaded.Id)).Should().BeNull();
        (await service.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetContentAsync_ShouldReturnNull_ForUnknownId()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        (await service.GetContentAsync(Guid.NewGuid())).Should().BeNull();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
