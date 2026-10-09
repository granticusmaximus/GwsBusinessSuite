using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class DailyNoteServiceTests
{
    private static readonly DateOnly Day = new(2026, 10, 9);

    [Fact]
    public async Task OpenOrCreateAsync_ShouldCreateTheDayOnce_UnderTheDailyNotesFolder()
    {
        var (db, service) = await CreateAsync();

        var first = await service.OpenOrCreateAsync(Day, "grant");
        var again = await service.OpenOrCreateAsync(Day, "grant");

        again.Id.Should().Be(first.Id);
        first.Title.Should().Be("2026-10-09 Friday");
        first.SystemKey.Should().Be("daily:2026-10-09");
        var folder = await db.WikiPages.SingleAsync(page => page.SystemKey == DailyNotes.FolderSystemKey);
        first.ParentWikiPageId.Should().Be(folder.Id);
        (await db.WikiPages.CountAsync(page => page.ParentWikiPageId == folder.Id)).Should().Be(1);
    }

    [Fact]
    public async Task OpenOrCreateAsync_ShouldStillFindTheDayAfterItIsRenamed()
    {
        var (db, service) = await CreateAsync();
        var page = await service.OpenOrCreateAsync(Day, "grant");
        page.Title = "Launch day";
        await db.SaveChangesAsync();

        (await service.OpenOrCreateAsync(Day, "grant")).Id.Should().Be(page.Id);
    }

    [Fact]
    public async Task OpenOrCreateAsync_ShouldStartFromTheDailyNoteTemplate_WithFreshBlockIds()
    {
        var (db, service) = await CreateAsync();
        var templateBlock = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.ToDo, 0, [new WikiRichTextSpan("Plan the day")], new Dictionary<string, string>());
        db.SentinelPageTemplates.Add(new SentinelPageTemplate
        {
            Name = "Daily", NormalizedName = "daily", PageTitle = "Daily", Icon = "☀️",
            BlocksJson = WikiBlockJson.Serialize([templateBlock]), UseForDailyNotes = true
        });
        await db.SaveChangesAsync();

        var page = await service.OpenOrCreateAsync(Day, "grant");

        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
        blocks.Should().ContainSingle(block => block.Type == WikiBlockTypes.ToDo);
        blocks[0].Id.Should().NotBe(templateBlock.Id);
        page.Icon.Should().Be("☀️");
    }

    [Fact]
    public async Task AppendAsync_ShouldAddATimestampedSectionToTheEnd()
    {
        var (_, service) = await CreateAsync();
        await service.AppendAsync(Day, "Standup", "- shipped K2", "grant");

        var page = await service.AppendAsync(Day, "Call with Sam", "Agreed the scope.", "grant");

        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
        blocks.Where(block => block.Type == WikiBlockTypes.Heading3)
            .Select(block => string.Concat(block.RichText.Select(span => span.Text)))
            .Should().HaveCount(2).And.Satisfy(
                heading => heading.EndsWith(" Standup"),
                heading => heading.EndsWith(" Call with Sam"));
        string.Concat(blocks[^1].RichText.Select(span => span.Text)).Should().Be("Agreed the scope.");
    }

    [Fact]
    public async Task SetDailyNoteTemplateAsync_ShouldKeepOnlyOneTemplateChosen()
    {
        var (db, _) = await CreateAsync();
        var templates = new SentinelTemplateService(db, new WikiService(db), new WikiDatabaseService(db));
        var a = new SentinelPageTemplate { Name = "A", NormalizedName = "a", PageTitle = "A" };
        var b = new SentinelPageTemplate { Name = "B", NormalizedName = "b", PageTitle = "B" };
        db.SentinelPageTemplates.AddRange(a, b);
        await db.SaveChangesAsync();

        await templates.SetDailyNoteTemplateAsync(a.Id);
        await templates.SetDailyNoteTemplateAsync(b.Id);
        (await templates.ListAsync()).Where(view => view.UseForDailyNotes).Select(view => view.Name).Should().Equal("B");

        await templates.SetDailyNoteTemplateAsync(null);
        (await templates.ListAsync()).Should().NotContain(view => view.UseForDailyNotes);
    }

    [Theory]
    [InlineData("daily:2026-10-09", true)]
    [InlineData("daily:not-a-date", false)]
    [InlineData("quick-notes", false)]
    [InlineData(null, false)]
    public void DateFromSystemKey_ShouldOnlyRecogniseDayPages(string? key, bool isDay)
    {
        (DailyNotes.DateFromSystemKey(key) is not null).Should().Be(isDay);
    }

    private static async Task<(ApplicationDbContext Db, DailyNoteService Service)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (db, new DailyNoteService(db, new WikiService(db), TimeProvider.System));
    }
}
