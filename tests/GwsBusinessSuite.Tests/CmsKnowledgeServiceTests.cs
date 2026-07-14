using FluentAssertions;
using GwsBusinessSuite.Application.CmsKnowledge;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CmsKnowledgeServiceTests
{
    [Fact]
    public async Task ListSourcesAsync_ShouldReturnSeededCleanRoomSources()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);

        var sources = await service.ListSourcesAsync();

        sources.Should().HaveCountGreaterThan(1);
        sources.Should().Contain(x => x.Key == "wp-clean-room");
        sources.Should().Contain(x => x.Key == "elementor-clean-room");
    }

    [Fact]
    public async Task ListEntriesAsync_ShouldFilterBySource_WhenProvided()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);
        var wpSource = (await service.ListSourcesAsync()).Single(s => s.Key == "wp-clean-room");

        var entries = await service.ListEntriesAsync(wpSource.Id);

        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(e => e.SourceId == wpSource.Id);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnRankedMatches()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);

        var results = await service.SearchAsync("responsive style breakpoint", take: 3);

        results.Should().NotBeEmpty();
        results[0].Capability.Should().ContainEquivalentOf("Responsive");
        results.Should().OnlyContain(x => x.Score > 0);
        results[0].SourceName.Should().Contain("Elementor");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenQueryIsBlank()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);

        var results = await service.SearchAsync("   ");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSourceAsync_ShouldCreateAndUpdateSources()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);

        var created = await service.SaveSourceAsync(new CmsKnowledgeSourceEditorModel
        {
            Key = "shopify-clean-room",
            Name = "Shopify Reference",
            LicenseNotes = "Notes",
            UsageGuidance = "Guidance"
        });

        created.Key.Should().Be("shopify-clean-room");

        var updated = await service.SaveSourceAsync(new CmsKnowledgeSourceEditorModel
        {
            SourceId = created.Id,
            Key = "shopify-clean-room",
            Name = "Shopify Reference (Updated)",
            LicenseNotes = "Notes",
            UsageGuidance = "Guidance"
        });

        updated.Id.Should().Be(created.Id);
        updated.Name.Should().Be("Shopify Reference (Updated)");
    }

    [Fact]
    public async Task DeleteSourceAsync_ShouldCascadeDeleteItsEntries()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);
        var source = await service.SaveSourceAsync(new CmsKnowledgeSourceEditorModel { Key = "temp-source", Name = "Temp" });
        var entry = await service.SaveEntryAsync(new CmsKnowledgeEntryEditorModel
        {
            SourceId = source.Id,
            Capability = "Temp capability"
        });

        await service.DeleteSourceAsync(source.Id);

        (await service.ListSourcesAsync()).Should().NotContain(s => s.Id == source.Id);
        (await service.ListEntriesAsync()).Should().NotContain(e => e.Id == entry.Id);
    }

    [Fact]
    public async Task SaveEntryAsync_ShouldThrow_WhenSourceDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);

        var act = () => service.SaveEntryAsync(new CmsKnowledgeEntryEditorModel
        {
            SourceId = Guid.NewGuid(),
            Capability = "Orphan capability"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteEntryAsync_ShouldRemoveOnlyThatEntry()
    {
        await using var db = await CreateDbAsync();
        var service = new CmsKnowledgeService(db);
        var source = (await service.ListSourcesAsync()).First();
        var entry = await service.SaveEntryAsync(new CmsKnowledgeEntryEditorModel
        {
            SourceId = source.Id,
            Capability = "Deletable capability"
        });

        await service.DeleteEntryAsync(entry.Id);

        (await service.ListEntriesAsync()).Should().NotContain(e => e.Id == entry.Id);
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
