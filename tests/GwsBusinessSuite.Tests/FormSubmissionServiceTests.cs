using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class FormSubmissionServiceTests
{
    private static Dictionary<string, string> AdaFields(string message = "Interested in a quote.") => new()
    {
        ["name"] = "Ada Lovelace",
        ["email"] = "ada@example.com",
        ["message"] = message
    };

    [Fact]
    public async Task SubmitAsync_ShouldStoreAndReturnTheSubmission()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        var submission = await service.SubmitAsync(page.Id, AdaFields());

        submission.FieldsJson.Should().Contain("Ada Lovelace");
        submission.FieldsJson.Should().Contain("ada@example.com");
        submission.FieldsJson.Should().Contain("Interested in a quote.");
        submission.IsRead.Should().BeFalse();

        var listed = await service.ListAsync(page.Id);
        listed.Should().ContainSingle(s => s.Id == submission.Id);
    }

    [Fact]
    public async Task SubmitAsync_ShouldRejectSubmissionsWithNoNonEmptyFields()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        var action = async () => await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "   " });

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldIgnoreBlankFields_ButKeepNonBlankOnes()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        var submission = await service.SubmitAsync(page.Id, new Dictionary<string, string>
        {
            ["name"] = "Ada",
            ["optional-field"] = ""
        });

        submission.FieldsJson.Should().Contain("Ada");
        submission.FieldsJson.Should().NotContain("optional-field");
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenPageDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = new FormSubmissionService(db);

        var action = async () => await service.SubmitAsync(Guid.NewGuid(), AdaFields());

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnMostRecentSubmissionsFirst()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);

        await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "First" });
        await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "Second" });

        var listed = await service.ListAsync(page.Id);

        listed.Should().HaveCount(2);
        listed[0].FieldsJson.Should().Contain("Second");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheSubmission()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = new FormSubmissionService(db);
        var page = await CreatePageAsync(cmsBuilder);
        var submission = await service.SubmitAsync(page.Id, AdaFields());

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
        var submission = await service.SubmitAsync(page.Id, AdaFields());

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
        await service.SubmitAsync(page.Id, AdaFields());

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
            BlocksJson = "{\"sections\":[]}"
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
