using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CmsSiteRegionServiceTests
{
    [Fact]
    public async Task ARegion_IsHiddenFromPageListsAndRouting_ButOpensInTheEditorById()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var regions = new CmsSiteRegionService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Regions" });

        var header = await regions.CreateAsync(site.Id, CmsPageRegions.Header, "header-logo-menu", "test");

        (await cms.ListPagesAsync(site.Id)).Should().BeEmpty();
        (await cms.GetPageByFullPathAsync(site.Id, header.Slug, includeUnpublished: true)).Should().BeNull();
        (await cms.GetPageAsync(header.Id)).Should().NotBeNull();
        (await regions.GetBySlugAsync(site.Id, CmsPageRegions.SlugFor(CmsPageRegions.Header)))!.Id.Should().Be(header.Id);
    }

    [Fact]
    public async Task OnlyALiveRegion_ReplacesTheBuiltInChrome_AndSavingKeepsItsReservedSlug()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var regions = new CmsSiteRegionService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Regions" });
        var footer = await regions.CreateAsync(site.Id, CmsPageRegions.Footer, "footer-simple", "test");

        (await regions.GetLiveLayoutsAsync(site.Id)).Footer.Should().BeNull("a new region starts as a draft");

        await regions.SetLiveAsync(site.Id, CmsPageRegions.Footer, live: true, "test");
        var (header, liveFooter) = await regions.GetLiveLayoutsAsync(site.Id);
        header.Should().BeNull();
        liveFooter!.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets)
            .Select(w => w.WidgetType).Should().Contain(["nav-menu", "copyright"]);

        // The editor saves a region like any page (title/slug fields included) - it must stay a region.
        var reloaded = await cms.GetPageAsync(footer.Id);
        await cms.SavePageAsync(new CmsPageEditorModel
        {
            PageId = footer.Id, SiteId = site.Id, Title = "Renamed", Slug = "contact", ParentPageId = null,
            BlocksJson = reloaded!.BlocksJson, Status = CmsPageStatuses.Published
        });
        var saved = await cms.GetPageAsync(footer.Id);
        saved!.Slug.Should().Be(CmsPageRegions.SlugFor(CmsPageRegions.Footer));
        saved.Region.Should().Be(CmsPageRegions.Footer);

        await regions.SetLiveAsync(site.Id, CmsPageRegions.Footer, live: false, "test");
        (await regions.GetLiveLayoutsAsync(site.Id)).Footer.Should().BeNull();
    }

    [Fact]
    public void FillSiteChrome_PutsTheSiteLogoAndMenusIntoTheBuilderWidgets()
    {
        var layout = CmsRegionTemplates.Find("header-logo-menu")!.Build();
        var html = CmsBlockHtmlRenderer.Render(layout);

        var filled = PublicSiteHtmlRenderer.FillSiteChrome(html,
            [new NavMenuItem("1", "Blog", "/blog", false), new NavMenuItem("2", "GitHub", "https://github.com/someone", true)],
            [], "My Site", "/media/logo.png");

        filled.Should().Contain("<img src=\"/media/logo.png\" alt=\"My Site\"");
        filled.Should().Contain("<a href=\"/blog\">Blog</a>");
        filled.Should().Contain("bi-github", "social links render as icons, like the built-in header");
    }

    [Fact]
    public void Layout_UsesACustomHeaderAndFooter_InsteadOfTheBuiltInOnes()
    {
        var html = PublicSiteHtmlRenderer.Layout("T", "D", null, "<p>body</p>", headerHtml: "<div>MY HEADER</div>", footerHtml: "<div>MY FOOTER</div>");

        html.Should().Contain("<header class=\"gws-site-region gws-site-header\"><div>MY HEADER</div></header>");
        html.Should().Contain("<footer class=\"gws-site-region gws-site-footer\"><div>MY FOOTER</div></footer>");
        html.Should().NotContain("class=\"site-nav\"");
        html.Should().NotContain("class=\"site-footer\"");
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
