using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SiteSettingsServiceTests
{
    [Fact]
    public async Task GetSettingsAsync_ShouldReturnEntityDefaults_WhenNoRowExists()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db, new FixedCurrentUserAccessor("grantwatson"));

        var settings = await service.GetSettingsAsync();

        Assert.Equal(12, settings.PostsPerPage);
        Assert.Null(settings.DefaultArticleCategoryId);
        Assert.Null(settings.DefaultAuthorByline);
        Assert.Null(settings.OllamaModelOverride);
        Assert.Null(settings.OllamaTimeoutMinutesOverride);
        Assert.Null(settings.HeroImageModelOverride);
        Assert.Equal(8, settings.MaxMediaUploadSizeMb);

        // Reading defaults must not create a row on what's a hot read path (queried on
        // every /blog request, media upload, etc.).
        Assert.Equal(0, await db.SiteSettings.CountAsync());
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldPersistAndReload_OnFirstSave()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db, new FixedCurrentUserAccessor("grantwatson"));

        var categoryId = Guid.NewGuid();
        await service.SaveSettingsAsync(new SiteSettingsView(
            PostsPerPage: 25,
            DefaultArticleCategoryId: categoryId,
            DefaultAuthorByline: "  Grant Watson  ",
            OllamaModelOverride: "  llama3.2  ",
            OllamaTimeoutMinutesOverride: 45,
            HeroImageModelOverride: "  x/z-image-turbo  ",
            MaxMediaUploadSizeMb: 16));

        var reloaded = await service.GetSettingsAsync();

        Assert.Equal(25, reloaded.PostsPerPage);
        Assert.Equal(categoryId, reloaded.DefaultArticleCategoryId);
        Assert.Equal("Grant Watson", reloaded.DefaultAuthorByline);
        Assert.Equal("llama3.2", reloaded.OllamaModelOverride);
        Assert.Equal(45, reloaded.OllamaTimeoutMinutesOverride);
        Assert.Equal("x/z-image-turbo", reloaded.HeroImageModelOverride);
        Assert.Equal(16, reloaded.MaxMediaUploadSizeMb);
        Assert.Equal(1, await db.SiteSettings.CountAsync());
        Assert.Equal("grantwatson", (await db.SiteSettings.SingleAsync()).UpdatedBy);
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClearOverrides_WhenBlankOrNullPassed()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db, new FixedCurrentUserAccessor("grantwatson"));

        await service.SaveSettingsAsync(new SiteSettingsView(25, Guid.NewGuid(), "Someone", "llama3.2", 45, "x/z-image-turbo", 16));

        // A second save with blanks/nulls clears the overrides back to "use appsettings
        // default" rather than leaving the previous override in place.
        await service.SaveSettingsAsync(new SiteSettingsView(25, null, "   ", "   ", null, "   ", 16));

        var reloaded = await service.GetSettingsAsync();

        Assert.Null(reloaded.DefaultArticleCategoryId);
        Assert.Null(reloaded.DefaultAuthorByline);
        Assert.Null(reloaded.OllamaModelOverride);
        Assert.Null(reloaded.OllamaTimeoutMinutesOverride);
        Assert.Null(reloaded.HeroImageModelOverride);
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClampPostsPerPage_ToAllowedValues()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db, new FixedCurrentUserAccessor("grantwatson"));

        await service.SaveSettingsAsync(new SiteSettingsView(99, null, null, null, null, null, 8));

        var reloaded = await service.GetSettingsAsync();

        Assert.Equal(12, reloaded.PostsPerPage);
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClampMaxMediaUploadSizeMb_To100()
    {
        // Regression test: the Settings.razor UI caps this at 100 via an HTML max
        // attribute only - a direct service/API call previously had no server-side
        // ceiling at all, letting MediaLibraryService accept effectively unbounded
        // uploads.
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db, new FixedCurrentUserAccessor("grantwatson"));

        await service.SaveSettingsAsync(new SiteSettingsView(25, null, null, null, null, null, 5000));

        var reloaded = await service.GetSettingsAsync();
        Assert.Equal(100, reloaded.MaxMediaUploadSizeMb);
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClampOllamaTimeoutMinutesOverride_To180()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db, new FixedCurrentUserAccessor("grantwatson"));

        await service.SaveSettingsAsync(new SiteSettingsView(25, null, null, null, 999999, null, 8));

        var reloaded = await service.GetSettingsAsync();
        Assert.Equal(180, reloaded.OllamaTimeoutMinutesOverride);
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
