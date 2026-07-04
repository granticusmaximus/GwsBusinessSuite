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
        var service = new SiteSettingsService(db);

        var settings = await service.GetSettingsAsync();

        Assert.Equal(10, settings.PostsPerPage);
        Assert.Null(settings.DefaultArticleCategoryId);
        Assert.Null(settings.DefaultAuthorByline);
        Assert.Null(settings.OllamaModelOverride);
        Assert.Null(settings.OllamaTimeoutMinutesOverride);
        Assert.Equal(8, settings.MaxMediaUploadSizeMb);

        // Reading defaults must not create a row on what's a hot read path (queried on
        // every /blog request, media upload, etc.).
        Assert.Equal(0, await db.SiteSettings.CountAsync());
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldPersistAndReload_OnFirstSave()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db);

        var categoryId = Guid.NewGuid();
        await service.SaveSettingsAsync(new SiteSettingsView(
            PostsPerPage: 25,
            DefaultArticleCategoryId: categoryId,
            DefaultAuthorByline: "  Grant Watson  ",
            OllamaModelOverride: "  llama3.2  ",
            OllamaTimeoutMinutesOverride: 45,
            MaxMediaUploadSizeMb: 16));

        var reloaded = await service.GetSettingsAsync();

        Assert.Equal(25, reloaded.PostsPerPage);
        Assert.Equal(categoryId, reloaded.DefaultArticleCategoryId);
        Assert.Equal("Grant Watson", reloaded.DefaultAuthorByline);
        Assert.Equal("llama3.2", reloaded.OllamaModelOverride);
        Assert.Equal(45, reloaded.OllamaTimeoutMinutesOverride);
        Assert.Equal(16, reloaded.MaxMediaUploadSizeMb);
        Assert.Equal(1, await db.SiteSettings.CountAsync());
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClearOverrides_WhenBlankOrNullPassed()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db);

        await service.SaveSettingsAsync(new SiteSettingsView(25, Guid.NewGuid(), "Someone", "llama3.2", 45, 16));

        // A second save with blanks/nulls clears the overrides back to "use appsettings
        // default" rather than leaving the previous override in place.
        await service.SaveSettingsAsync(new SiteSettingsView(25, null, "   ", "   ", null, 16));

        var reloaded = await service.GetSettingsAsync();

        Assert.Null(reloaded.DefaultArticleCategoryId);
        Assert.Null(reloaded.DefaultAuthorByline);
        Assert.Null(reloaded.OllamaModelOverride);
        Assert.Null(reloaded.OllamaTimeoutMinutesOverride);
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClampPostsPerPage_ToAllowedValues()
    {
        await using var db = await CreateDbAsync();
        var service = new SiteSettingsService(db);

        await service.SaveSettingsAsync(new SiteSettingsView(99, null, null, null, null, 8));

        var reloaded = await service.GetSettingsAsync();

        Assert.Equal(10, reloaded.PostsPerPage);
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
