using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CmsBuilderServiceTests
{
    [Fact]
    public async Task ListWorkflowBlueprintsAsync_ShouldReturnDefaultBlueprintLibrary()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var blueprints = await service.ListWorkflowBlueprintsAsync();

        blueprints.Should().NotBeEmpty();
        blueprints.Should().Contain(x => x.Key == "landing-conversion");
        blueprints.Should().Contain(x => x.Key == "blog-editorial");
    }

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
    public async Task SaveSiteAsync_AndSavePageAsync_ShouldPersistCustomCss()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Styled Site",
            CustomCss = "body { background: #111; }"
        });

        var page = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Home",
            BlocksJson = "[]",
            CustomCss = ".cms-hero { color: hotpink; }"
        });

        site.CustomCss.Should().Be("body { background: #111; }");
        page.CustomCss.Should().Be(".cms-hero { color: hotpink; }");
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

    [Fact]
    public async Task ApplyWorkflowBlueprintAsync_ShouldAppendOrReplaceBlocks()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Docs"
        });

        var page = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Home",
            BlocksJson = "[{\"type\":\"existing\"}]"
        });

        var appended = await service.ApplyWorkflowBlueprintAsync(page.Id, "landing-conversion", replaceExistingBlocks: false);
        appended.BlocksJson.Should().Contain("existing");
        appended.BlocksJson.Should().Contain("proof-grid");

        var replaced = await service.ApplyWorkflowBlueprintAsync(page.Id, "service-business", replaceExistingBlocks: true);
        replaced.BlocksJson.Should().NotContain("existing");
        replaced.BlocksJson.Should().Contain("service-list");
    }

    [Fact]
    public async Task GetSiteBySlugAndGetPageBySlugAsync_ShouldResolveThePublishedPage()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Public Site" });
        await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Home",
            BlocksJson = "[]",
            MetaTitle = "Welcome",
            MetaDescription = "A test page",
            OgImageUrl = "/media/abc"
        });

        var resolvedSite = await service.GetSiteBySlugAsync("public-site");
        resolvedSite.Should().NotBeNull();

        var resolvedPage = await service.GetPageBySlugAsync(resolvedSite!.Id, "home");
        resolvedPage.Should().NotBeNull();
        resolvedPage!.MetaTitle.Should().Be("Welcome");
        resolvedPage.MetaDescription.Should().Be("A test page");
        resolvedPage.OgImageUrl.Should().Be("/media/abc");
    }

    [Fact]
    public async Task GetSiteBySlugAsync_ShouldReturnNull_ForUnknownSlug()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        (await service.GetSiteBySlugAsync("does-not-exist")).Should().BeNull();
    }

    [Fact]
    public async Task ApplyWorkflowBlueprintAsync_ShouldThrowForUnknownBlueprint()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Docs"
        });

        var page = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Home",
            BlocksJson = "[]"
        });

        var action = async () => await service.ApplyWorkflowBlueprintAsync(page.Id, "missing-blueprint", replaceExistingBlocks: false);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
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
