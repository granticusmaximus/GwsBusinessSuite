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
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

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
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

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
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

        var action = async () => await service.UploadAsync("doc.pdf", [1, 2, 3], string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectSvg_EvenThoughItIsAnImageMimeType()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));
        var svg = "<svg onload=\"alert(1)\"></svg>"u8.ToArray();

        var action = async () => await service.UploadAsync("icon.svg", svg, string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectFilesOverTheSizeLimit()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));
        var tooLarge = new byte[9 * 1024 * 1024];

        var action = async () => await service.UploadAsync("big.png", tooLarge, string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnMostRecentlyUploadedFirst()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

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
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));
        var uploaded = await service.UploadAsync("temp.png", PngBytes, string.Empty);

        await service.DeleteAsync(uploaded.Id);

        (await service.GetContentAsync(uploaded.Id)).Should().BeNull();
        (await service.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAltTextAsync_ShouldPersistTrimmedAltText_AndReturnUpdatedSummary()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));
        var uploaded = await service.UploadAsync("logo.png", PngBytes, "Old alt text");

        var updated = await service.UpdateAltTextAsync(uploaded.Id, "  New alt text  ");

        updated.Should().NotBeNull();
        updated!.AltText.Should().Be("New alt text");

        var reloaded = (await service.ListAsync()).Single(a => a.Id == uploaded.Id);
        reloaded.AltText.Should().Be("New alt text");
    }

    [Fact]
    public async Task UpdateAltTextAsync_ShouldReturnNull_ForUnknownId()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

        (await service.UpdateAltTextAsync(Guid.NewGuid(), "Alt text")).Should().BeNull();
    }

    [Fact]
    public async Task GetContentAsync_ShouldReturnNull_ForUnknownId()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

        (await service.GetContentAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task GetThumbnailContentAsync_ShouldReturnASmallerImage_ForALargeUpload()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));
        var largeImage = CreateRealPng(width: 1200, height: 800);

        var uploaded = await service.UploadAsync("big-photo.png", largeImage, string.Empty);

        var thumbnail = await service.GetThumbnailContentAsync(uploaded.Id);
        thumbnail.Should().NotBeNull();
        thumbnail!.Value.ContentType.Should().Be("image/jpeg");
        thumbnail.Value.Content.Length.Should().BeLessThan(largeImage.Length);

        using var decoded = SkiaSharp.SKBitmap.Decode(thumbnail.Value.Content);
        decoded.Should().NotBeNull();
        decoded!.Width.Should().BeLessThanOrEqualTo(320);
        decoded.Height.Should().BeLessThanOrEqualTo(320);
    }

    [Fact]
    public async Task GetThumbnailContentAsync_ShouldFallBackToTheFullAsset_WhenOriginalIsAlreadySmall()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));
        var smallImage = CreateRealPng(width: 100, height: 100);

        var uploaded = await service.UploadAsync("small-icon.png", smallImage, string.Empty);

        var thumbnail = await service.GetThumbnailContentAsync(uploaded.Id);
        thumbnail.Should().NotBeNull();
        thumbnail!.Value.ContentType.Should().Be("image/png");
        thumbnail.Value.Content.Should().BeEquivalentTo(smallImage);
    }

    [Fact]
    public async Task GetThumbnailContentAsync_ShouldFallBackToTheFullAsset_WhenBytesArentActuallyDecodable()
    {
        // PngBytes only satisfies the magic-byte sniff (DetectImageContentType), not a real
        // decodable image - SKBitmap.Decode will fail, so this exercises the catch-and-fall-
        // back branch of TryGenerateThumbnail, not just the "already small" branch above.
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

        var uploaded = await service.UploadAsync("undecodable.png", PngBytes, string.Empty);

        var thumbnail = await service.GetThumbnailContentAsync(uploaded.Id);
        thumbnail.Should().NotBeNull();
        thumbnail!.Value.Content.Should().BeEquivalentTo(PngBytes);
    }

    [Fact]
    public async Task GetThumbnailContentAsync_ShouldReturnNull_ForUnknownId()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db, new GwsBusinessSuite.Application.Settings.SiteSettingsService(db));

        (await service.GetThumbnailContentAsync(Guid.NewGuid())).Should().BeNull();
    }

    private static byte[] CreateRealPng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
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
