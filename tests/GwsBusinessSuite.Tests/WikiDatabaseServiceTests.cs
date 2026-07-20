using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class WikiDatabaseServiceTests
{
    [Fact]
    public async Task CreateDatabaseAsync_ShouldSeedATitlePropertyAndADefaultTableView()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);

        var database = await service.CreateDatabaseAsync("Projects", null, "grantwatson");

        database.Properties.Should().ContainSingle(p => p.Type == WikiDatabasePropertyTypes.Title);
        database.Views.Should().ContainSingle(v => v.Type == WikiDatabaseViewTypes.Table);
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldRejectASecondTitleProperty()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");

        var act = () => service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Another Title", Type = WikiDatabasePropertyTypes.Title }, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldRejectChangingAnExistingPropertysType()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var property = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "u");

        var act = () => service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Id = property.Id, Name = "Status", Type = WikiDatabasePropertyTypes.Number }, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeletePropertyAsync_ShouldRejectDeletingTheTitleProperty()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);

        var act = () => service.DeletePropertyAsync(database.Id, titleProperty.Id, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveRowAsync_ShouldRoundTripTypedPropertyValues()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var numberProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Points", Type = WikiDatabasePropertyTypes.Number }, "u");
        var checkboxProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Done", Type = WikiDatabasePropertyTypes.Checkbox }, "u");

        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, "Ship the feature");
        WikiPropertyValues.SetNumber(values, numberProperty.Id, 5m);
        WikiPropertyValues.SetCheckbox(values, checkboxProperty.Id, true);

        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { Values = values.ToDictionary(kv => kv.Key, kv => kv.Value) }, "u");

        var reloaded = await service.GetDatabaseAsync(database.Id);
        var reloadedRow = reloaded!.Rows.Single(r => r.Id == row.Id);
        var reloadedValues = WikiPropertyValues.ParseObject(reloadedRow.PropertyValuesJson);
        WikiPropertyValues.GetText(reloadedValues, titleProperty.Id).Should().Be("Ship the feature");
        WikiPropertyValues.GetNumber(reloadedValues, numberProperty.Id).Should().Be(5m);
        WikiPropertyValues.GetCheckbox(reloadedValues, checkboxProperty.Id).Should().BeTrue();
    }

    [Fact]
    public async Task MoveRowAsync_ShouldUpdateTheGroupingValueAndRenumberSiblingsInTheTargetGroup()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Board", null, "u");
        var statusProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Select,
            Options = [new WikiDatabasePropertyOption("todo", "To Do", "#ccc"), new WikiDatabasePropertyOption("done", "Done", "#0f0")]
        }, "u");

        var movingRow = await service.SaveRowAsync(database.Id, RowWithStatus(statusProperty.Id, "todo"), "u");
        var existingInDone1 = await service.SaveRowAsync(database.Id, RowWithStatus(statusProperty.Id, "done"), "u");
        var existingInDone2 = await service.SaveRowAsync(database.Id, RowWithStatus(statusProperty.Id, "done"), "u");

        await service.MoveRowAsync(database.Id, movingRow.Id, statusProperty.Id, "done", 0, "u");

        var reloaded = await service.GetDatabaseAsync(database.Id);
        var moved = reloaded!.Rows.Single(r => r.Id == movingRow.Id);
        WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(moved.PropertyValuesJson), statusProperty.Id).Should().Be("done");
        moved.SortOrder.Should().Be(0, "inserted at index 0 of the Done column");

        reloaded.Rows.Single(r => r.Id == existingInDone1.Id).SortOrder.Should().Be(1);
        reloaded.Rows.Single(r => r.Id == existingInDone2.Id).SortOrder.Should().Be(2);
    }

    [Fact]
    public async Task DeleteViewAsync_ShouldRejectDeletingTheLastView()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var onlyView = database.Views.Single();

        var act = () => service.DeleteViewAsync(database.Id, onlyView.Id, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteDatabaseAsync_ShouldCascadeDeletePropertiesRowsAndViews()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Temp", null, "u");
        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        await service.DeleteDatabaseAsync(database.Id, "u");

        (await db.WikiDatabaseProperties.Where(p => p.WikiDatabaseId == database.Id).ToListAsync()).Should().BeEmpty();
        (await db.WikiDatabaseRows.Where(r => r.WikiDatabaseId == database.Id).ToListAsync()).Should().BeEmpty();
        (await db.WikiDatabaseViews.Where(v => v.WikiDatabaseId == database.Id).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReorderDatabaseAsync_ShouldMoveADatabaseUnderAWikiPageAndRenumberSiblings()
    {
        await using var db = await CreateDbAsync();
        var wikiService = new WikiService(db);
        var databaseService = new WikiDatabaseService(db);
        var page = await wikiService.SavePageAsync(new WikiPageEditorModel { Title = "Projects Hub" }, "u");
        var first = await databaseService.CreateDatabaseAsync("Tasks", null, "u");
        var second = await databaseService.CreateDatabaseAsync("Bugs", null, "u");

        await databaseService.ReorderDatabaseAsync(second.Id, page.Id, 0, "u");

        var moved = await databaseService.GetDatabaseAsync(second.Id);
        moved!.ParentWikiPageId.Should().Be(page.Id);
        moved.SortOrder.Should().Be(0);
    }

    private static WikiDatabaseRowEditor RowWithStatus(Guid statusPropertyId, string statusOptionId)
    {
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, statusPropertyId, statusOptionId);
        return new WikiDatabaseRowEditor { Values = values.ToDictionary(kv => kv.Key, kv => kv.Value) };
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
