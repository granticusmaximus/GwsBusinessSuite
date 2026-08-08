using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Regression guard for a real finding: Comment.ArticleId, CmsPageRevision.PageId, and
// CmsPage/CmsPageCategory/GlobalBlock.SiteId had no configured foreign key at all - deleting
// the parent silently orphaned the children with no DB-level integrity or cascade path (the
// admin article-delete endpoint, for one, never touched a deleted article's comments). These
// tests exercise the raw EF Core model/database behavior directly (Add + Remove +
// SaveChangesAsync on ApplicationDbContext, not through any service's own manual cleanup
// logic like CmsBuilderService.DeleteSiteAsync already had) to prove the cascade now comes
// from the schema itself, not just from services remembering to clean up after themselves.
public sealed class CmsAndCommentForeignKeyTests
{
    [Fact]
    public async Task DeletingAnArticle_ShouldCascadeDeleteItsComments()
    {
        await using var db = await CreateDbAsync();
        var article = new Article { Title = "Test article", Slug = "test-article", Topic = "Test", MetaDescription = "Test" };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        db.Comments.Add(new Comment { ArticleId = article.Id, AuthorName = "Reader", Body = "Nice post" });
        await db.SaveChangesAsync();

        db.Articles.Remove(article);
        await db.SaveChangesAsync();

        (await db.Comments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletingACmsPage_ShouldCascadeDeleteItsRevisions()
    {
        await using var db = await CreateDbAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        db.CmsSites.Add(site);
        await db.SaveChangesAsync();
        var page = new CmsPage { SiteId = site.Id, Title = "Page", Slug = "page" };
        db.CmsPages.Add(page);
        await db.SaveChangesAsync();
        db.CmsPageRevisions.Add(new CmsPageRevision { PageId = page.Id, RevisionNumber = 1 });
        await db.SaveChangesAsync();

        db.CmsPages.Remove(page);
        await db.SaveChangesAsync();

        (await db.CmsPageRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletingACmsSite_ShouldCascadeDeletePagesCategoriesAndGlobalBlocks()
    {
        await using var db = await CreateDbAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        db.CmsSites.Add(site);
        await db.SaveChangesAsync();
        db.CmsPages.Add(new CmsPage { SiteId = site.Id, Title = "Page", Slug = "page" });
        db.CmsPageCategories.Add(new CmsPageCategory { SiteId = site.Id, Name = "Category", Slug = "category" });
        db.GlobalBlocks.Add(new GlobalBlock { SiteId = site.Id, Name = "Block" });
        await db.SaveChangesAsync();

        db.CmsSites.Remove(site);
        await db.SaveChangesAsync();

        (await db.CmsPages.CountAsync()).Should().Be(0);
        (await db.CmsPageCategories.CountAsync()).Should().Be(0);
        (await db.GlobalBlocks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletingACmsPage_ShouldCascadeDeleteItsFormSubmissions()
    {
        await using var db = await CreateDbAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        db.CmsSites.Add(site);
        await db.SaveChangesAsync();
        var page = new CmsPage { SiteId = site.Id, Title = "Page", Slug = "page" };
        db.CmsPages.Add(page);
        await db.SaveChangesAsync();
        db.FormSubmissions.Add(new FormSubmission { PageId = page.Id });
        await db.SaveChangesAsync();

        db.CmsPages.Remove(page);
        await db.SaveChangesAsync();

        (await db.FormSubmissions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletingACmsSite_ShouldCascadeDeleteAppGenerationRequestsTargetingIt()
    {
        await using var db = await CreateDbAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        db.CmsSites.Add(site);
        await db.SaveChangesAsync();
        db.AppGenerationRequests.Add(new AppGenerationRequest { TargetSiteId = site.Id, Title = "Generate a site" });
        await db.SaveChangesAsync();

        db.CmsSites.Remove(site);
        await db.SaveChangesAsync();

        (await db.AppGenerationRequests.CountAsync()).Should().Be(0);
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
