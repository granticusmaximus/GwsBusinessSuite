using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CmsBuilderServiceTests
{
    [Fact]
    public async Task SaveSiteAsync_ShouldCreateUpdateAndDeleteSitesAndPages()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var createdSite = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Main Site",
            Slug = "main-site",
            Theme = "Editorial"
        });

        var createdPage = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = createdSite.Id,
            Title = "Home",
            BlocksJson = "[{\"type\":\"hero\",\"title\":\"Welcome\"}]"
        });

        createdSite.Slug.Should().Be("main-site");
        createdPage.Slug.Should().Be("home");

        var updatedSite = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            SiteId = createdSite.Id,
            Name = "Main Site",
            Slug = "main-site",
            Theme = "Magazine"
        });

        updatedSite.Theme.Should().Be("Magazine");

        var listedPages = await service.ListPagesAsync(createdSite.Id);
        listedPages.Should().HaveCount(1);
        listedPages[0].BlocksJson.Should().Contain("hero");

        await service.DeleteSiteAsync(createdSite.Id);

        (await service.ListSitesAsync()).Should().BeEmpty();
        (await service.ListPagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSiteAsync_ShouldGenerateUniqueSlugsForDuplicateNames()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var first = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Public Site"
        });

        var second = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Public Site"
        });

        first.Slug.Should().Be("public-site");
        second.Slug.Should().Be("public-site-2");
    }

    [Fact]
    public async Task SavePageAsync_ShouldGenerateUniqueSlugsWithinTheSameSite()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Docs"
        });

        var first = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Landing Page",
            BlocksJson = "[]"
        });

        var second = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Landing Page",
            BlocksJson = "[]"
        });

        first.Slug.Should().Be("landing-page");
        second.Slug.Should().Be("landing-page-2");
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