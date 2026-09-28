using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class RelatedArticlesServiceTests
{
    private static readonly DateTimeOffset Published = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetRelatedArticlesAsync_ShouldRankByCategoryAndSharedTagsThenPublicationDate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var category = new ArticleCategory { Name = "Engineering", Slug = "engineering" };
        fixture.Db.ArticleCategories.Add(category);
        var anchor = Post("anchor", "dotnet, blazor", category.Id);
        var strongest = Post("category-and-tag", " BLAZOR ", category.Id);
        var categoryOnly = Post("category-only", "unrelated", category.Id);
        var twoTags = Post("two-tags", "DotNet, Blazor", publishedAt: Published.AddDays(2));
        var oneTag = Post("one-tag", "dotnet", publishedAt: Published.AddDays(3));
        var unrelated = Post("unrelated", "other");
        fixture.Db.Articles.AddRange(anchor, strongest, categoryOnly, twoTags, oneTag, unrelated);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.GetRelatedArticlesAsync(anchor, 6);

        result.Select(a => a.Slug).Should().Equal("category-and-tag", "two-tags", "category-only", "one-tag");
    }

    [Fact]
    public async Task GetRelatedArticlesAsync_ShouldExcludeSelfDraftsTrashedAndFuturePublications()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Post("anchor", "shared");
        var visible = Post("visible", "shared");
        var draftWithOldPublishDate = Post("unpublished-again", "shared");
        draftWithOldPublishDate.Status = ArticleStatuses.Draft;
        var neverPublished = Post("never-published", "shared");
        neverPublished.PublishedAt = null;
        var trashed = Post("trashed", "shared");
        trashed.TrashedAt = DateTimeOffset.UtcNow;
        var scheduled = Post("scheduled", "shared", publishedAt: DateTimeOffset.UtcNow.AddDays(5));
        fixture.Db.Articles.AddRange(anchor, visible, draftWithOldPublishDate, neverPublished, trashed, scheduled);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.GetRelatedArticlesAsync(anchor, 6);

        result.Select(a => a.Slug).Should().Equal("visible");
    }

    [Fact]
    public async Task GetRelatedArticlesAsync_ShouldCountEachSharedTagOnceIgnoringCaseAndWhitespace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Post("anchor", "dotnet, DOTNET, blazor");
        var duplicateTags = Post("duplicate-tags", "dotnet, DOTNET, Dotnet", publishedAt: Published.AddDays(2));
        var distinctTags = Post("two-distinct-tags", " DotNet , BLAZOR ");
        fixture.Db.Articles.AddRange(anchor, duplicateTags, distinctTags);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.GetRelatedArticlesAsync(anchor);

        result.Select(a => a.Slug).Should().Equal("two-distinct-tags", "duplicate-tags");
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    [InlineData(100, 6)]
    public async Task GetRelatedArticlesAsync_ShouldClampTakeToOneThroughSix(int take, int expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Post("anchor", "shared");
        fixture.Db.Articles.Add(anchor);
        fixture.Db.Articles.AddRange(Enumerable.Range(1, 8).Select(i => Post($"post-{i}", "shared", publishedAt: Published.AddDays(i))));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.GetRelatedArticlesAsync(anchor, take);

        result.Should().HaveCount(expected);
        result[0].Slug.Should().Be("post-8");
    }

    [Fact]
    public async Task GetRelatedArticlesAsync_ShouldDefaultToThreeResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Post("anchor", "shared");
        fixture.Db.Articles.AddRange(Enumerable.Range(1, 5).Select(i => Post($"post-{i}", "shared")));
        await fixture.Db.SaveChangesAsync();

        (await fixture.Service.GetRelatedArticlesAsync(anchor)).Should().HaveCount(3);
    }

    [Fact]
    public async Task GetRelatedArticlesAsync_ShouldNotTreatMissingCategoriesOrEmptyTagsAsMatches()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Post("anchor", " , ");
        fixture.Db.Articles.AddRange(Post("no-taxonomy", ""), Post("different-tag", "other"));
        await fixture.Db.SaveChangesAsync();

        (await fixture.Service.GetRelatedArticlesAsync(anchor)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/media/hero.jpg", "data:image/png;base64,AA==", "/media/hero.jpg")]
    [InlineData(null, "data:image/png;base64,AA==", "/og-image/related")]
    [InlineData("", "data:image/png;base64,AA==", "/og-image/related")]
    [InlineData(null, "", null)]
    public async Task GetRelatedArticlesAsync_ShouldProjectThePublicHeroImage(string? imageUrl, string imageData, string? expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Post("anchor", "shared");
        var related = Post("related", "shared");
        related.HeroImageUrl = imageUrl;
        related.HeroImageDataUri = imageData;
        fixture.Db.Articles.Add(related);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.GetRelatedArticlesAsync(anchor);

        result.Should().ContainSingle().Which.HeroImageUrl.Should().Be(expected);
    }

    private static Article Post(string slug, string tags, Guid? categoryId = null, DateTimeOffset? publishedAt = null) => new()
    {
        Slug = slug, Title = slug, Tags = tags, CategoryId = categoryId,
        Status = ArticleStatuses.Published, PublishedAt = publishedAt ?? Published
    };

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db) : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; } = db;
        public RelatedArticlesService Service { get; } = new(db);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
