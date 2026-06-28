using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class FormSubmissionServiceTests
{
    [Fact]
    public async Task SubmitAsync_ShouldStoreAndReturnTheSubmission()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        var submission = await service.SubmitAsync(page.Id, "Ada Lovelace", "ada@example.com", "Interested in a quote.");

        submission.Name.Should().Be("Ada Lovelace");
        submission.Email.Should().Be("ada@example.com");
        submission.Message.Should().Be("Interested in a quote.");
        submission.IsRead.Should().BeFalse();

        var listed = await service.ListAsync(page.Id);
        listed.Should().ContainSingle(s => s.Id == submission.Id);
    }

    [Theory]
    [InlineData("", "ada@example.com", "Hello")]
    [InlineData("Ada", "not-an-email", "Hello")]
    [InlineData("Ada", "ada@example.com", "")]
    public async Task SubmitAsync_ShouldRejectInvalidInput(string name, string email, string message)
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        var action = async () => await service.SubmitAsync(page.Id, name, email, message);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenPageDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = new FormSubmissionService(db);

        var action = async () => await service.SubmitAsync(Guid.NewGuid(), "Ada", "ada@example.com", "Hello");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnMostRecentSubmissionsFirst()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        await service.SubmitAsync(page.Id, "First", "first@example.com", "First message");
        await service.SubmitAsync(page.Id, "Second", "second@example.com", "Second message");

        var listed = await service.ListAsync(page.Id);

        listed.Should().HaveCount(2);
        listed[0].Name.Should().Be("Second");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheSubmission()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);
        var submission = await service.SubmitAsync(page.Id, "Ada", "ada@example.com", "Hello");

        await service.DeleteAsync(submission.Id);

        (await service.ListAsync(page.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadAsync_ShouldSetIsReadToTrue()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);
        var submission = await service.SubmitAsync(page.Id, "Ada", "ada@example.com", "Hello");

        await service.MarkReadAsync(submission.Id);

        (await service.ListAsync(page.Id)).Single().IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingThePage_ShouldAlsoRemoveItsSubmissions()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);
        await service.SubmitAsync(page.Id, "Ada", "ada@example.com", "Hello");

        await cmsBuilder.DeletePageAsync(page.Id);

        (await service.ListAsync(page.Id)).Should().BeEmpty();
    }

    private static async Task<GwsBusinessSuite.Domain.Entities.CmsPage> CreatePageAsync(CmsBuilderService cmsBuilder)
    {
        var site = await cmsBuilder.SaveSiteAsync(new CmsSiteEditorModel { Name = "Test Site" });
        return await cmsBuilder.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Contact",
            BlocksJson = "[]"
        });
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
