using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ApplicationDbContext _db;
        public WikiService WikiService { get; }
        public SentinelTemplateService TemplateService { get; }

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            _db = db;
            WikiService = new WikiService(db);
            TemplateService = new SentinelTemplateService(db, WikiService);
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
