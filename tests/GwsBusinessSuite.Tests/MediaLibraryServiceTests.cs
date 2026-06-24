using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class MediaLibraryServiceTests
{
    [Fact]
    public async Task UploadAsync_ShouldStoreAndReturnRetrievableAsset()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);
        var content = Encoding.UTF8.GetBytes("fake-png-bytes");

        var uploaded = await service.UploadAsync("logo.png", "image/png", content, "Site logo");

        uploaded.FileName.Should().Be("logo.png");
        uploaded.Url.Should().Be($"/media/{uploaded.Id}");

        var fetched = await service.GetContentAsync(uploaded.Id);
        fetched.Should().NotBeNull();
        fetched!.Value.ContentType.Should().Be("image/png");
        fetched.Value.Content.Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectNonImageContentTypes()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        var action = async () => await service.UploadAsync("doc.pdf", "application/pdf", [1, 2, 3], string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectFilesOverTheSizeLimit()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);
        var tooLarge = new byte[9 * 1024 * 1024];

        var action = async () => await service.UploadAsync("big.png", "image/png", tooLarge, string.Empty);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnMostRecentlyUploadedFirst()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);

        await service.UploadAsync("first.png", "image/png", [1], string.Empty);
        await service.UploadAsync("second.png", "image/png", [2], string.Empty);

        var assets = await service.ListAsync();

        assets.Should().HaveCount(2);
        assets[0].FileName.Should().Be("second.png");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveAssetSoItCanNoLongerBeFetched()
    {
        await using var db = await CreateDbAsync();
        var service = new MediaLibraryService(db);
        var uploaded = await service.UploadAsync("temp.png", "image/png", [9, 9], string.Empty);

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
