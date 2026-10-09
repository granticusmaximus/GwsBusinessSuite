using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class WikiFileValuesTests
{
    [Fact]
    public void FormatThenParse_ShouldRoundTrip_IncludingColonsInTheName()
    {
        var id = Guid.NewGuid();

        var parsed = WikiFileValues.Parse(WikiFileValues.Format(id, "notes: v2.pdf"));

        parsed.Should().Be(new WikiFileReference(id, "notes: v2.pdf"));
        parsed!.DownloadUrl.Should().Be($"/admin/sentinel/files/{id}");
    }

    [Theory]
    [InlineData("https://example.com/report.pdf")]
    [InlineData("Contract draft")]
    [InlineData("sentinel-file:not-a-guid:x.pdf")]
    public void Parse_ShouldLeaveOlderFreeTextValuesAlone(string value)
    {
        WikiFileValues.Parse(value).Should().BeNull();
    }

    [Theory]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("page.html", "application/octet-stream")]
    [InlineData("no-extension", "application/octet-stream")]
    public void ContentTypeFor_ShouldComeFromTheExtension_AndDefaultToDownload(string fileName, string expected)
    {
        WikiFileValues.ContentTypeFor(fileName).Should().Be(expected);
    }

    [Fact]
    public void CleanFileName_ShouldDropAnyPathTheBrowserSent()
    {
        WikiFileValues.CleanFileName(@"C:\Users\me\Desktop\invoice.pdf").Should().Be("invoice.pdf");
        WikiFileValues.CleanFileName("   ").Should().Be("file");
    }

    [Fact]
    public void DisplayText_ShouldShowFileNames_NotTheStoredReference()
    {
        var property = new WikiDatabaseProperty { Id = Guid.NewGuid(), Name = "Files", Type = WikiDatabasePropertyTypes.Files };
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(values, property.Id, [WikiFileValues.Format(Guid.NewGuid(), "brief.pdf"), "https://example.com"]);

        WikiPropertyValues.GetDisplayText(property, values, DateTimeOffset.UtcNow, null, null, null)
            .Should().Be("brief.pdf, https://example.com");
    }

    [Fact]
    public async Task StorePropertyFileAsync_ShouldSaveTheFileWithAServerChosenType()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Contracts", null, "grant");
        var files = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Attachments", Type = WikiDatabasePropertyTypes.Files }, "grant");

        var value = await service.StorePropertyFileAsync(database.Id, files.Id, "../../contract.pdf", [1, 2, 3]);

        var reference = WikiFileValues.Parse(value);
        reference!.FileName.Should().Be("contract.pdf");
        var stored = await db.SentinelImportedFiles.SingleAsync(file => file.Id == reference.Id);
        stored.ContentType.Should().Be("application/pdf");
        stored.SizeBytes.Should().Be(3);
    }

    [Fact]
    public async Task StorePropertyFileAsync_ShouldRefuseAPropertyThatIsNotAFilesProperty()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Contracts", null, "grant");
        var title = database.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);

        var act = () => service.StorePropertyFileAsync(database.Id, title.Id, "x.pdf", [1]);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        (await db.SentinelImportedFiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StorePropertyFileAsync_ShouldRefuseAnEmptyFile()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);

        var act = () => service.StorePropertyFileAsync(Guid.NewGuid(), Guid.NewGuid(), "x.pdf", []);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
