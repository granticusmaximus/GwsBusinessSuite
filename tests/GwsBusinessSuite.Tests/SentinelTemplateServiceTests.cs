using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelTemplateServiceTests
{
    [Fact]
    public async Task CreatePageAsync_ShouldCloneTemplateMetadataAndRegenerateBlockIds()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sourceBlockId = Guid.NewGuid();
        var source = await fixture.WikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Incident Review",
            Icon = "🚨",
            CoverImageUrl = "https://example.test/cover.jpg",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(sourceBlockId, WikiBlockTypes.Heading1, 0,
                    [new WikiRichTextSpan("What happened?")], new Dictionary<string, string>())])
        }, "Owner");
        var template = await fixture.TemplateService.CreateFromPageAsync(
            source.Id, "Incident retrospective", "Owner");

        await fixture.WikiService.DeletePageAsync(source.Id, "Owner");
        var created = await fixture.TemplateService.CreatePageAsync(template.Id, null, "Member");
        var createdBlock = WikiBlockJson.ParseBlocks(created.BlocksJson).Single();

        created.Title.Should().Be("Incident Review");
        created.Icon.Should().Be("🚨");
        created.CoverImageUrl.Should().Be("https://example.test/cover.jpg");
        created.CreatedBy.Should().Be("member");
        createdBlock.Id.Should().NotBe(sourceBlockId);
        createdBlock.PlainText.Should().Be("What happened?");
        (await fixture.TemplateService.ListAsync()).Should().ContainSingle(item =>
            item.Id == template.Id && item.BlockCount == 1 && item.CreatedBy == "owner");
    }

    [Fact]
    public async Task CreateFromPageAsync_ShouldRejectDuplicateNamesIgnoringCaseAndWhitespace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.WikiService.SavePageAsync(
            new WikiPageEditorModel { Title = "Runbook" }, "Owner");
        await fixture.TemplateService.CreateFromPageAsync(source.Id, " Team Runbook ", "Owner");

        var action = () => fixture.TemplateService.CreateFromPageAsync(
            source.Id, "team runbook", "Owner");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*template with that name already exists*");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveOnlyTheSelectedTemplate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.WikiService.SavePageAsync(
            new WikiPageEditorModel { Title = "Runbook" }, "Owner");
        var first = await fixture.TemplateService.CreateFromPageAsync(source.Id, "First", "Owner");
        var second = await fixture.TemplateService.CreateFromPageAsync(source.Id, "Second", "Owner");

        await fixture.TemplateService.DeleteAsync(first.Id);

        (await fixture.TemplateService.ListAsync()).Should().ContainSingle(item => item.Id == second.Id);
    }

    [Fact]
    public async Task CreateDatabaseAsync_ShouldRestoreIndependentSnapshotAfterSourceDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.DatabaseService.CreateDatabaseAsync("Launch tracker", null, "Owner");
        var title = source.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var status = await fixture.DatabaseService.SavePropertyAsync(source.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Select,
            Options = [new WikiDatabasePropertyOption("active", "Active", "#22c55e")]
        }, "Owner");
        var values = new JsonObject();
        WikiPropertyValues.SetText(values, title.Id, "Public beta");
        WikiPropertyValues.SetText(values, status.Id, "active");
        var sourceBlockId = Guid.NewGuid();
        var sourceRow = await fixture.DatabaseService.SaveRowAsync(source.Id, new WikiDatabaseRowEditor
        {
            Values = values.ToDictionary(item => item.Key, item => item.Value),
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(sourceBlockId, WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Launch checklist")], new Dictionary<string, string>())])
        }, "Owner");
        await fixture.DatabaseService.SaveViewAsync(source.Id, null, "Board", WikiDatabaseViewTypes.Board,
            new WikiDatabaseViewConfig([], [], status.Id.ToString()), "Owner");

        var template = await fixture.TemplateService.CreateFromDatabaseAsync(
            source.Id, "Launch database", "Owner");
        await fixture.DatabaseService.DeleteDatabaseAsync(source.Id, "Owner");
        var created = await fixture.TemplateService.CreateDatabaseAsync(template.Id, null, "Member");
        var reloaded = await fixture.DatabaseService.GetDatabaseAsync(created.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("Launch tracker");
        reloaded.CreatedBy.Should().Be("member");
        reloaded.Properties.Should().HaveCount(2);
        reloaded.Rows.Should().ContainSingle();
        reloaded.Views.Should().HaveCount(2);
        reloaded.Properties.Should().OnlyContain(property => property.Id != title.Id && property.Id != status.Id);
        reloaded.Rows.Single().Id.Should().NotBe(sourceRow.Id);
        WikiBlockJson.ParseBlocks(reloaded.Rows.Single().BlocksJson).Single().Id.Should().NotBe(sourceBlockId);

        var copiedTitle = reloaded.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var copiedStatus = reloaded.Properties.Single(property => property.Name == "Status");
        var copiedValues = WikiPropertyValues.ParseObject(reloaded.Rows.Single().PropertyValuesJson);
        WikiPropertyValues.GetText(copiedValues, copiedTitle.Id).Should().Be("Public beta");
        WikiPropertyValues.GetText(copiedValues, copiedStatus.Id).Should().Be("active");
        WikiDatabaseViewConfigJson.Parse(reloaded.Views.Single(view => view.Type == WikiDatabaseViewTypes.Board).ConfigJson)
            .GroupByPropertyId.Should().Be(copiedStatus.Id.ToString());
        (await fixture.TemplateService.ListDatabaseTemplatesAsync()).Should().ContainSingle(item =>
            item.Id == template.Id && item.PropertyCount == 2 && item.RowCount == 1 && item.ViewCount == 2);
    }

    [Fact]
    public async Task DatabaseTemplates_ShouldRejectDuplicateNamesAndDeleteOnlySelectedTemplate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.DatabaseService.CreateDatabaseAsync("Projects", null, "Owner");
        var first = await fixture.TemplateService.CreateFromDatabaseAsync(source.Id, " Team projects ", "Owner");

        var duplicate = () => fixture.TemplateService.CreateFromDatabaseAsync(
            source.Id, "team projects", "Owner");
        await duplicate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*database template with that name already exists*");

        var second = await fixture.TemplateService.CreateFromDatabaseAsync(source.Id, "Portfolio", "Owner");
        await fixture.TemplateService.DeleteDatabaseTemplateAsync(first.Id);

        (await fixture.TemplateService.ListDatabaseTemplatesAsync())
            .Should().ContainSingle(item => item.Id == second.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ApplicationDbContext _db;
        public WikiService WikiService { get; }
        public WikiDatabaseService DatabaseService { get; }
        public SentinelTemplateService TemplateService { get; }

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            _db = db;
            WikiService = new WikiService(db);
            DatabaseService = new WikiDatabaseService(db);
            TemplateService = new SentinelTemplateService(db, WikiService, DatabaseService);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
