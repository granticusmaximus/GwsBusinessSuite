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
    public async Task GetHomepageAsync_ShouldPreferHomeThenIndexThenFirstVisibleTopLevelPage()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var homeSite = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Home Site" });
        await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = homeSite.Id,
            Title = "About",
            Slug = "about",
            Status = CmsPageStatuses.Published
        });
        await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = homeSite.Id,
            Title = "Index",
            Slug = "index",
            Status = CmsPageStatuses.Published
        });
        var homePage = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = homeSite.Id,
            Title = "Home",
            Slug = "home",
            Status = CmsPageStatuses.Published
        });

        var preferredHome = await service.GetHomepageAsync(homeSite.Id);
        preferredHome!.Id.Should().Be(homePage.Id);

        var indexSite = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Index Site" });
        await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = indexSite.Id,
            Title = "About",
            Slug = "about",
            Status = CmsPageStatuses.Published
        });
        var indexPage = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = indexSite.Id,
            Title = "Index",
            Slug = "index",
            Status = CmsPageStatuses.Published
        });

        var preferredIndex = await service.GetHomepageAsync(indexSite.Id);
        preferredIndex!.Id.Should().Be(indexPage.Id);

        var fallbackSite = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Fallback Site" });
        var fallbackPage = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = fallbackSite.Id,
            Title = "Landing",
            Slug = "landing",
            Status = CmsPageStatuses.Published
        });
        await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = fallbackSite.Id,
            Title = "Contact",
            Slug = "contact",
            Status = CmsPageStatuses.Published
        });

        var fallbackHome = await service.GetHomepageAsync(fallbackSite.Id);
        fallbackHome!.Id.Should().Be(fallbackPage.Id);
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
        var child = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = parent.Id, Title = "Child", Slug = "child" });

        // Permanent delete requires the page to already be trashed, and trashing itself
        // requires no *active* children — trash the child first, then the parent, so this
        // test actually exercises DeletePageAsync's children-check (which blocks on any
        // children regardless of trash status), not TrashPageAsync's.
        await service.TrashPageAsync(child.Id);
        await service.TrashPageAsync(parent.Id);
        var action = async () => await service.DeletePageAsync(parent.Id);

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*Child*");
        (await service.GetPageAsync(parent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task TrashPageAsync_ShouldHidePageFromListsAndPublicRouting()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var page = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Page",
            Slug = "page",
            Status = CmsPageStatuses.Published
        });

        await service.TrashPageAsync(page.Id);

        (await service.ListPagesAsync(site.Id)).Should().NotContain(p => p.Id == page.Id);
        (await service.ListPagesAsync(site.Id, includeTrashed: true)).Should().Contain(p => p.Id == page.Id);
        (await service.GetPageByFullPathAsync(site.Id, "page")).Should().BeNull();
        // Trash is unconditional — even an authenticated admin preview doesn't see it.
        (await service.GetPageByFullPathAsync(site.Id, "page", includeUnpublished: true)).Should().BeNull();
    }

    [Fact]
    public async Task RestorePageAsync_ShouldBringBackAPage_WithItsOriginalStatusIntact()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var page = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Page",
            Slug = "page",
            Status = CmsPageStatuses.Published
        });

        await service.TrashPageAsync(page.Id);
        await service.RestorePageAsync(page.Id);

        var restored = await service.GetPageAsync(page.Id);
        restored!.TrashedAt.Should().BeNull();
        restored.Status.Should().Be(CmsPageStatuses.Published);
        (await service.GetPageByFullPathAsync(site.Id, "page")).Should().NotBeNull();
    }

    [Fact]
    public async Task TrashPageAsync_ShouldBeBlockedByActiveChildren_ButAllowedWhenChildrenAreAlreadyTrashed()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var parent = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Parent", Slug = "parent" });
        var child = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, ParentPageId = parent.Id, Title = "Child", Slug = "child" });

        var blockedAction = async () => await service.TrashPageAsync(parent.Id);
        await blockedAction.Should().ThrowAsync<ArgumentException>().WithMessage("*Child*");

        await service.TrashPageAsync(child.Id);
        var allowedAction = async () => await service.TrashPageAsync(parent.Id);
        await allowedAction.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeletePageAsync_ShouldRequireThePageToAlreadyBeTrashed()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var page = await service.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Page", Slug = "page" });

        var action = async () => await service.DeletePageAsync(page.Id);
        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*Trash*");

        await service.TrashPageAsync(page.Id);
        await service.DeletePageAsync(page.Id);
        (await service.GetPageAsync(page.Id)).Should().BeNull();
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
    public async Task SaveSiteAsync_ShouldPersistFooterNavMenuJson_IndependentlyOfPrimaryNav()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Site",
            NavMenuJson = """[{"id":"1","label":"About","href":"/about","openInNewTab":false}]""",
            FooterNavMenuJson = """[{"id":"2","label":"Privacy","href":"/privacy","openInNewTab":false}]"""
        });

        var reloaded = await service.GetSiteAsync(site.Id);

        reloaded!.NavMenuJson.Should().Contain("About");
        reloaded.NavMenuJson.Should().NotContain("Privacy");
        reloaded.FooterNavMenuJson.Should().Contain("Privacy");
        reloaded.FooterNavMenuJson.Should().NotContain("About");
    }

    [Fact]
    public async Task SaveSiteAsync_ShouldDefaultFooterNavMenuJson_ToEmptyArray_WhenNotProvided()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });

        site.FooterNavMenuJson.Should().Be("[]");
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

    [Fact]
    public async Task SaveSiteAsync_ShouldPersistLogoAndFaviconUrls()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel
        {
            Name = "Branded Site",
            LogoUrl = "https://cdn.example.com/logo.svg",
            FaviconUrl = "https://cdn.example.com/favicon.png"
        });

        var reloaded = await service.GetSiteAsync(site.Id);

        reloaded!.LogoUrl.Should().Be("https://cdn.example.com/logo.svg");
        reloaded.FaviconUrl.Should().Be("https://cdn.example.com/favicon.png");
    }

    [Fact]
    public async Task SavePageAsync_ShouldPersistCanonicalTagsAndSiteScopedCategories()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var firstSite = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site One" });
        var secondSite = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site Two" });

        var firstPage = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = firstSite.Id,
            Title = "Landing",
            Slug = "landing",
            BlocksJson = "[]",
            CanonicalUrl = "https://example.com/landing",
            CategoryName = "Marketing",
            Tags = "seo, landing"
        });

        var secondPageSameSite = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = firstSite.Id,
            Title = "Pricing",
            Slug = "pricing",
            BlocksJson = "[]",
            CategoryName = "Marketing"
        });

        var thirdPageOtherSite = await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = secondSite.Id,
            Title = "Pricing",
            Slug = "pricing",
            BlocksJson = "[]",
            CategoryName = "Marketing"
        });

        firstPage.CanonicalUrl.Should().Be("https://example.com/landing");
        firstPage.Tags.Should().Be("seo, landing");
        firstPage.CategoryId.Should().NotBeNull();
        secondPageSameSite.CategoryId.Should().Be(firstPage.CategoryId!.Value);
        thirdPageOtherSite.CategoryId.Should().NotBe(firstPage.CategoryId!.Value);

        var categories = await service.ListPageCategoriesAsync(firstSite.Id);
        categories.Should().ContainSingle(category => category.Name == "Marketing");
    }

    [Fact]
    public async Task GetPageByFullPathAsync_ShouldHideFutureScheduledPages_UntilTheirPublishTime()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsBuilderService(db);

        var site = await service.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(3);

        await service.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Launch Page",
            Slug = "launch-page",
            BlocksJson = "[]",
            Status = CmsPageStatuses.Published,
            PublishedAt = scheduledAt
        });

        (await service.GetPageByFullPathAsync(site.Id, "launch-page")).Should().BeNull();
        var preview = await service.GetPageByFullPathAsync(site.Id, "launch-page", includeUnpublished: true);
        preview.Should().NotBeNull();
        preview!.PublishedAt.Should().Be(scheduledAt);
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
