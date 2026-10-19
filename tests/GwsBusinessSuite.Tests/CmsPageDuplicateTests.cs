using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Pages > Duplicate: a new draft copy of a page.
public sealed class CmsPageDuplicateTests
{
    [Fact]
    public async Task Duplicate_ShouldMakeADraftCopy_WithItsLatestContentAndSettings()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var parent = await cms.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Services", Status = CmsPageStatuses.Published });
        var original = await cms.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id, ParentPageId = parent.Id, Title = "Pricing", Slug = "pricing",
            BlocksJson = """{"sections":[{"id":"live"}]}""", MetaTitle = "Pricing | Site", MetaDescription = "Plans",
            CanonicalUrl = "https://example.com/pricing", CategoryName = "Sales", Tags = "plans", CustomCss = ".x{}",
            Status = CmsPageStatuses.Published
        });
        // An unpublished draft edit on the live page: the copy takes the latest content.
        await cms.SavePageAsync(new CmsPageEditorModel
        {
            PageId = original.Id, SiteId = site.Id, ParentPageId = parent.Id, Title = "Pricing", Slug = "pricing",
            BlocksJson = """{"sections":[{"id":"draft"}]}""", Status = CmsPageStatuses.Published, ContentIsDraftEdit = true,
            CategoryName = "Sales", Tags = "plans"
        });
        var stored = await db.CmsPages.SingleAsync(page => page.Id == original.Id);
        stored.PropertyValuesJson = """{"00000000-0000-0000-0000-000000000001":42}""";
        await db.SaveChangesAsync();

        var copy = await cms.DuplicatePageAsync(original.Id, "grant");

        copy.Id.Should().NotBe(original.Id);
        copy.Title.Should().Be("Copy of Pricing");
        copy.Slug.Should().Be("copy-of-pricing");
        copy.Status.Should().Be(CmsPageStatuses.Draft);
        copy.ParentPageId.Should().Be(parent.Id);
        copy.BlocksJson.Should().Contain("draft");
        copy.CanonicalUrl.Should().BeEmpty("the copy mustn't claim to be the original");
        copy.Tags.Should().Be("plans");
        copy.CategoryId.Should().Be(stored.CategoryId);
        copy.PropertyValuesJson.Should().Contain("42", "typed values are copied as stored");
        (await db.CmsPages.SingleAsync(page => page.Id == original.Id)).Status.Should().Be(CmsPageStatuses.Published, "the original is untouched");

        var second = await cms.DuplicatePageAsync(original.Id);
        second.Slug.Should().Be("copy-of-pricing-2", "slugs stay unique");
        (await cms.DuplicatePageAsync(copy.Id)).Title.Should().Be("Copy of Pricing", "no 'Copy of Copy of'");
    }

    [Fact]
    public async Task Duplicate_ShouldRefuseTrashedOrMissingPages()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        var page = await cms.SavePageAsync(new CmsPageEditorModel { SiteId = site.Id, Title = "Old" });
        await cms.TrashPageAsync(page.Id);

        await cms.Invoking(service => service.DuplicatePageAsync(page.Id)).Should().ThrowAsync<InvalidOperationException>();
        await cms.Invoking(service => service.DuplicatePageAsync(Guid.NewGuid())).Should().ThrowAsync<InvalidOperationException>();
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
