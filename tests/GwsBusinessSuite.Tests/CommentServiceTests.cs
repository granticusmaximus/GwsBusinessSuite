using FluentAssertions;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CommentServiceTests
{
    [Fact]
    public async Task SubmitAsync_ShouldStoreAsPending()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        var comment = await service.SubmitAsync(article.Id, "Ada Lovelace", "ada@example.com", "Great article!");

        comment.Status.Should().Be(CommentStatuses.Pending);
        comment.AuthorName.Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenNameIsBlank()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        var action = async () => await service.SubmitAsync(article.Id, "  ", "ada@example.com", "Great article!");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenEmailIsMissingAtSign()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        var action = async () => await service.SubmitAsync(article.Id, "Ada", "not-an-email", "Great article!");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenBodyIsBlank()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        var action = async () => await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "   ");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenArticleDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = new CommentService(db);

        var action = async () => await service.SubmitAsync(Guid.NewGuid(), "Ada", "ada@example.com", "Great article!");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListApprovedForArticleAsync_ShouldOnlyReturnApprovedComments()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        var pending = await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "Pending comment");
        var toApprove = await service.SubmitAsync(article.Id, "Bob", "bob@example.com", "Approved comment");
        await service.ApproveAsync(toApprove.Id);

        var approved = await service.ListApprovedForArticleAsync(article.Id);

        approved.Should().ContainSingle(c => c.Id == toApprove.Id);
        approved.Should().NotContain(c => c.Id == pending.Id);
    }

    [Fact]
    public async Task ListForModerationAsync_ShouldIncludeArticleTitleAndSlug()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db, title: "Clean Architecture", slug: "clean-architecture");
        var service = new CommentService(db);
        await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "Nice writeup");

        var moderation = await service.ListForModerationAsync(null);

        moderation.Should().ContainSingle();
        moderation[0].ArticleTitle.Should().Be("Clean Architecture");
        moderation[0].ArticleSlug.Should().Be("clean-architecture");
    }

    [Fact]
    public async Task ListForModerationAsync_ShouldFilterByStatus()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        var toSpam = await service.SubmitAsync(article.Id, "Bot", "bot@example.com", "buy cheap watches");
        await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "Genuine comment");
        await service.MarkSpamAsync(toSpam.Id);

        var spamOnly = await service.ListForModerationAsync(CommentStatuses.Spam);

        spamOnly.Should().ContainSingle(c => c.Id == toSpam.Id);
    }

    [Fact]
    public async Task ApproveAsync_ShouldSetStatusToApproved()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);
        var comment = await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "Great article!");

        await service.ApproveAsync(comment.Id);

        (await service.ListForModerationAsync(null)).Single().Status.Should().Be(CommentStatuses.Approved);
    }

    [Fact]
    public async Task MarkSpamAsync_ShouldSetStatusToSpam()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);
        var comment = await service.SubmitAsync(article.Id, "Bot", "bot@example.com", "spam link");

        await service.MarkSpamAsync(comment.Id);

        (await service.ListForModerationAsync(null)).Single().Status.Should().Be(CommentStatuses.Spam);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheComment()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);
        var comment = await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "Great article!");

        await service.DeleteAsync(comment.Id);

        (await service.ListForModerationAsync(null)).Should().BeEmpty();
    }

    [Fact]
    public async Task CountPendingAsync_ShouldOnlyCountPendingComments()
    {
        await using var db = await CreateDbAsync();
        var article = await CreateArticleAsync(db);
        var service = new CommentService(db);

        await service.SubmitAsync(article.Id, "Ada", "ada@example.com", "First");
        var second = await service.SubmitAsync(article.Id, "Bob", "bob@example.com", "Second");
        await service.ApproveAsync(second.Id);

        (await service.CountPendingAsync()).Should().Be(1);
    }

    private static async Task<Article> CreateArticleAsync(
        ApplicationDbContext db,
        string title = "Test Article",
        string slug = "test-article")
    {
        var article = new Article
        {
            Slug = slug,
            Title = title,
            Status = ArticleStatuses.Published,
            CreatedBy = "test"
        };

        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article;
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
