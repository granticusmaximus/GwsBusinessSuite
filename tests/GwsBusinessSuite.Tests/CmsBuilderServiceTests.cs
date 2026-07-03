using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CmsBuilderServiceTests
{
    [Fact]
    public async Task SavePageAsync_ShouldDefaultNewPagesToDraft()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var page = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Page", Slug = "page" });

        page.Status.Should().Be(CmsPageStatuses.Draft);
        page.PublishedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetPageByFullPathAsync_ShouldHideDraftPages_ByDefault_AndShowThemWithIncludeUnpublished()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Draft Page", Slug = "draft-page" });

        (await service.GetPageByFullPathAsync(site.Id, "draft-page")).Should().BeNull();
        (await service.GetPageByFullPathAsync(site.Id, "draft-page", includeUnpublished: true)).Should().NotBeNull();
    }

    [Fact]
    public async Task SavePageAsync_ShouldStampPublishedAt_OnFirstPublish_AndPreserveIt()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var draft = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Page", Slug = "page" });
        draft.PublishedAt.Should().BeNull();

        var published = await service.SavePageAsync(new CmsPageEditorModel
        {
            PageId = draft.Id,
            SiteId = site.Id,
            Title = "Page",
            Slug = "page",
            Status = CmsPageStatuses.Published
        });
        published.PublishedAt.Should().NotBeNull();
        var firstPublishedAt = published.PublishedAt;

        // Unpublish then republish — PublishedAt shouldn't reset to a new timestamp.
        await service.SavePageAsync(new CmsPageEditorModel { PageId = draft.Id, SiteId = site.Id, Title = "Page", Slug = "page", Status = CmsPageStatuses.Draft });
        var republished = await service.SavePageAsync(new CmsPageEditorModel
        {
            PageId = draft.Id,
            SiteId = site.Id,
            Title = "Page",
            Slug = "page",
            Status = CmsPageStatuses.Published
        });

        republished.PublishedAt.Should().Be(firstPublishedAt);
    }

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
            BlocksJson = "{\"sections\":[{\"id\":\"existing-section\",\"columns\":[]}]}"
        });

        var appended = await service.ApplyWorkflowBlueprintAsync(page.Id, "landing-conversion", replaceExistingBlocks: false);
        appended.BlocksJson.Should().Contain("existing-section");
        appended.BlocksJson.Should().Contain("Trusted by teams");

        var replaced = await service.ApplyWorkflowBlueprintAsync(page.Id, "service-business", replaceExistingBlocks: true);
        replaced.BlocksJson.Should().NotContain("existing-section");
        replaced.BlocksJson.Should().Contain("Discovery");
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
    public async Task GetPageByFullPathAsync_ShouldResolveNestedPages()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var services = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Services", Slug = "services", Status = CmsPageStatuses.Published });
        var webDev = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            ParentPageId = services.Id,
            Title = "Web Dev",
            Slug = "web-dev",
            Status = CmsPageStatuses.Published
        });

        var resolved = await service.GetPageByFullPathAsync(site.Id, "services/web-dev");

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(webDev.Id);
        (await service.GetPageByFullPathAsync(site.Id, "services")).Should().NotBeNull();
        (await service.GetPageByFullPathAsync(site.Id, "services/nope")).Should().BeNull();
    }

    [Fact]
    public async Task SavePageAsync_ShouldAllowSameSlug_UnderDifferentParents()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var servicesParent = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Services", Slug = "services" });
        var productsParent = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Products", Slug = "products" });

        var servicesPricing = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = servicesParent.Id, Title = "Pricing", Slug = "pricing" });
        var productsPricing = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = productsParent.Id, Title = "Pricing", Slug = "pricing" });

        servicesPricing.Slug.Should().Be("pricing");
        productsPricing.Slug.Should().Be("pricing");
        servicesPricing.Id.Should().NotBe(productsPricing.Id);
    }

    [Fact]
    public async Task SavePageAsync_ShouldRejectSelfAsParent()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var page = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Page", Slug = "page" });

        var action = async () => await service.SavePageAsync(new CmsPageEditorModel
        {
            PageId = page.Id,
            SiteId = site.Id,
            ParentPageId = page.Id,
            Title = "Page",
            Slug = "page"
        });

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SavePageAsync_ShouldRejectMovingAPageUnderItsOwnDescendant()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var parent = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Parent", Slug = "parent" });
        var child = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = parent.Id, Title = "Child", Slug = "child" });

        var action = async () => await service.SavePageAsync(new CmsPageEditorModel
        {
            PageId = parent.Id,
            SiteId = site.Id,
            ParentPageId = child.Id,
            Title = "Parent",
            Slug = "parent"
        });

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeletePageAsync_ShouldBeBlocked_WhenPageHasChildren()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var parent = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Parent", Slug = "parent" });
        await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = parent.Id, Title = "Child", Slug = "child" });

        var action = async () => await service.DeletePageAsync(parent.Id);

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*Child*");
        (await service.GetPageAsync(parent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task BuildFullPath_ShouldWalkParentChain()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var services = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Services", Slug = "services" });
        var webDev = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = services.Id, Title = "Web Dev", Slug = "web-dev" });

        var allPages = await service.ListPagesAsync(site.Id);

        service.BuildFullPath(webDev, allPages).Should().Be("services/web-dev");
        service.BuildFullPath(services, allPages).Should().Be("services");
    }

    [Fact]
    public async Task SaveSiteAsync_ShouldPersistNavMenuJson()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Site",
            NavMenuJson = """[{"id":"1","label":"About","href":"/about","openInNewTab":false}]"""
        });

        var reloaded = await service.GetSiteAsync(site.Id);

        reloaded!.NavMenuJson.Should().Contain("About");
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
