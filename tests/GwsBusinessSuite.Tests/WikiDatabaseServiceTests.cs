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
    public async Task GetDatabaseAsync_ShouldEvaluateFormulaPropertiesWithoutPersistingComputedValues()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Estimates", null, "u");
        var hours = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Hours", Type = WikiDatabasePropertyTypes.Number }, "u");
        var rate = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Rate", Type = WikiDatabasePropertyTypes.Number }, "u");
        var total = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Total",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "round([Hours] * [Rate], 2)"
        }, "u");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetNumber(values, hours.Id, 2.5m);
        WikiPropertyValues.SetNumber(values, rate.Id, 125.25m);
        var row = await service.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(database.Id);

        WikiPropertyValues.GetComputedValue(
            WikiPropertyValues.ParseObject(computed!.Rows.Single().PropertyValuesJson), total.Id).Should().Be(313.13m);
        var stored = await db.WikiDatabaseRows.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        WikiPropertyValues.ParseObject(stored.PropertyValuesJson).ContainsKey(total.Id.ToString()).Should().BeFalse();
    }

    [Fact]
    public async Task GetDatabaseAsync_ShouldEvaluateAdvancedNumericLogicalAndTextFormulaFunctions()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var hours = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Hours", Type = WikiDatabasePropertyTypes.Number }, "u");
        var blocked = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Blocked", Type = WikiDatabasePropertyTypes.Checkbox }, "u");
        var client = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Client", Type = WikiDatabasePropertyTypes.Text }, "u");
        var score = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Score",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "if([Hours] > 8 and not [Blocked], max([Hours] ^ 2 % 50, abs(-12)) * 2, 0)"
        }, "u");
        var label = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Label",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "upper(trim([Client])) + \" · \" + length([Client])"
        }, "u");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetNumber(values, hours.Id, 10m);
        WikiPropertyValues.SetCheckbox(values, blocked.Id, false);
        WikiPropertyValues.SetText(values, client.Id, " Acme ");
        await service.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(database.Id);
        var computedValues = WikiPropertyValues.ParseObject(computed!.Rows.Single().PropertyValuesJson);

        WikiPropertyValues.GetComputedValue(computedValues, score.Id).Should().Be(24m);
        WikiPropertyValues.GetComputedValue(computedValues, label.Id).Should().Be("ACME · 6");
    }

    [Fact]
    public async Task GetDatabaseAsync_ShouldEvaluateAdvancedDateFormulaFunctions()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var due = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Due", Type = WikiDatabasePropertyTypes.Date }, "u");
        var shifted = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Shifted",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "formatDate(dateAdd([Due], 2, \"days\"), \"YYYY-MM-DD\")"
        }, "u");
        var duration = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Duration",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "dateBetween(dateAdd([Due], 3, \"weeks\"), [Due], \"days\")"
        }, "u");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetDate(values, due.Id, new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        await service.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(database.Id);
        var computedValues = WikiPropertyValues.ParseObject(computed!.Rows.Single().PropertyValuesJson);

        WikiPropertyValues.GetComputedValue(computedValues, shifted.Id).Should().Be("2026-07-23");
        WikiPropertyValues.GetComputedValue(computedValues, duration.Id).Should().Be(21m);
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldRejectUnknownAdvancedFormulaFunction()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");

        var action = () => service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Broken",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "mystery(1)"
        }, "u");

        await action.Should().ThrowAsync<Exception>().WithMessage("#ERROR!*Unknown function*");
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldRejectInvalidFormulaSyntax()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Estimates", null, "u");
        await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Hours", Type = WikiDatabasePropertyTypes.Number }, "u");

        var action = () => service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Broken",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "[Hours] * ("
        }, "u");

        await action.Should().ThrowAsync<Exception>().WithMessage("#ERROR!*");
    }

    [Fact]
    public async Task RenamePropertyAsync_ShouldKeepFormulaReferencesValid()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Estimates", null, "u");
        var hours = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Hours", Type = WikiDatabasePropertyTypes.Number }, "u");
        var total = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Double",
            Type = WikiDatabasePropertyTypes.Formula,
            FormulaExpression = "[Hours] * 2"
        }, "u");

        await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Id = hours.Id, Name = "Effort", Type = hours.Type }, "u");

        var reloaded = await service.GetDatabaseAsync(database.Id);
        WikiDatabasePropertyConfig.Parse(reloaded!.Properties.Single(property => property.Id == total.Id))
            .FormulaExpression.Should().Be("[Effort] * 2");
    }

    [Fact]
    public async Task GetDatabaseAsync_ShouldResolveRelationsAndCalculateRollups()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var invoices = await service.CreateDatabaseAsync("Invoices", null, "u");
        var invoiceTitle = invoices.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var amount = await service.SavePropertyAsync(invoices.Id,
            new WikiDatabasePropertyEditor { Name = "Amount", Type = WikiDatabasePropertyTypes.Number }, "u");
        var firstValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(firstValues, invoiceTitle.Id, "INV-001");
        WikiPropertyValues.SetNumber(firstValues, amount.Id, 120m);
        var first = await service.SaveRowAsync(invoices.Id,
            new WikiDatabaseRowEditor { Values = firstValues.ToDictionary(item => item.Key, item => item.Value) }, "u");
        var secondValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(secondValues, invoiceTitle.Id, "INV-002");
        WikiPropertyValues.SetNumber(secondValues, amount.Id, 80m);
        var second = await service.SaveRowAsync(invoices.Id,
            new WikiDatabaseRowEditor { Values = secondValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var clients = await service.CreateDatabaseAsync("Clients", null, "u");
        var relation = await service.SavePropertyAsync(clients.Id, new WikiDatabasePropertyEditor
        {
            Name = "Invoices",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = invoices.Id
        }, "u");
        var rollup = await service.SavePropertyAsync(clients.Id, new WikiDatabasePropertyEditor
        {
            Name = "Revenue",
            Type = WikiDatabasePropertyTypes.Rollup,
            RelationPropertyId = relation.Id,
            RollupPropertyId = amount.Id,
            RollupAggregation = WikiDatabaseRollupAggregations.Sum
        }, "u");
        var clientValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(clientValues, relation.Id, [first.Id.ToString(), second.Id.ToString()]);
        await service.SaveRowAsync(clients.Id,
            new WikiDatabaseRowEditor { Values = clientValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(clients.Id);

        var computedValues = WikiPropertyValues.ParseObject(computed!.Rows.Single().PropertyValuesJson);
        WikiPropertyValues.GetMultiSelect(computedValues, relation.Id).Should().Equal(first.Id.ToString(), second.Id.ToString());
        WikiPropertyValues.GetDisplayText(
            computed.Properties.Single(property => property.Id == relation.Id),
            computedValues,
            computed.Rows.Single().CreatedAt).Should().Be("INV-001, INV-002");
        WikiPropertyValues.GetComputedValue(computedValues, rollup.Id).Should().Be(200m);
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldCreateAPairedReciprocalRelation()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var projects = await service.CreateDatabaseAsync("Projects", null, "u");
        var teams = await service.CreateDatabaseAsync("Teams", null, "u");

        var teamRelation = await service.SavePropertyAsync(projects.Id, new WikiDatabasePropertyEditor
        {
            Name = "Team",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = teams.Id,
            ReciprocalRelationEnabled = true,
            ReciprocalPropertyName = "Projects"
        }, "u");

        var sourceConfig = WikiDatabasePropertyConfig.Parse(teamRelation);
        sourceConfig.ReciprocalPropertyId.Should().NotBeNull();
        var reciprocal = await db.WikiDatabaseProperties.AsNoTracking()
            .SingleAsync(property => property.Id == sourceConfig.ReciprocalPropertyId);
        reciprocal.WikiDatabaseId.Should().Be(teams.Id);
        reciprocal.Name.Should().Be("Projects");
        var reciprocalConfig = WikiDatabasePropertyConfig.Parse(reciprocal);
        reciprocalConfig.RelatedDatabaseId.Should().Be(projects.Id);
        reciprocalConfig.ReciprocalPropertyId.Should().Be(teamRelation.Id);
    }

    [Fact]
    public async Task SaveRowAsync_ShouldSynchronizeReciprocalRelationsFromEitherSide()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var projects = await service.CreateDatabaseAsync("Projects", null, "u");
        var teams = await service.CreateDatabaseAsync("Teams", null, "u");
        var teamRelation = await service.SavePropertyAsync(projects.Id, new WikiDatabasePropertyEditor
        {
            Name = "Team",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = teams.Id,
            ReciprocalRelationEnabled = true,
            ReciprocalPropertyName = "Projects"
        }, "u");
        var reciprocalId = WikiDatabasePropertyConfig.Parse(teamRelation).ReciprocalPropertyId!.Value;
        var project = await service.SaveRowAsync(projects.Id, new WikiDatabaseRowEditor(), "u");
        var team = await service.SaveRowAsync(teams.Id, new WikiDatabaseRowEditor(), "u");

        var projectValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(projectValues, teamRelation.Id, [team.Id.ToString()]);
        await service.SaveRowAsync(projects.Id, new WikiDatabaseRowEditor
        {
            Id = project.Id,
            Values = projectValues.ToDictionary(item => item.Key, item => item.Value)
        }, "u");

        var reloadedTeam = (await service.GetDatabaseAsync(teams.Id))!.Rows.Single();
        WikiPropertyValues.GetMultiSelect(
            WikiPropertyValues.ParseObject(reloadedTeam.PropertyValuesJson), reciprocalId)
            .Should().Equal(project.Id.ToString());

        var reverseValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(reverseValues, reciprocalId, []);
        await service.SaveRowAsync(teams.Id, new WikiDatabaseRowEditor
        {
            Id = team.Id,
            Values = reverseValues.ToDictionary(item => item.Key, item => item.Value)
        }, "u");

        var reloadedProject = (await service.GetDatabaseAsync(projects.Id))!.Rows.Single();
        WikiPropertyValues.GetMultiSelect(
            WikiPropertyValues.ParseObject(reloadedProject.PropertyValuesJson), teamRelation.Id)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldRemoveThePairedPropertyWhenReciprocalIsDisabled()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var projects = await service.CreateDatabaseAsync("Projects", null, "u");
        var teams = await service.CreateDatabaseAsync("Teams", null, "u");
        var teamRelation = await service.SavePropertyAsync(projects.Id, new WikiDatabasePropertyEditor
        {
            Name = "Team",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = teams.Id,
            ReciprocalRelationEnabled = true
        }, "u");
        var reciprocalId = WikiDatabasePropertyConfig.Parse(teamRelation).ReciprocalPropertyId!.Value;

        var updated = await service.SavePropertyAsync(projects.Id, new WikiDatabasePropertyEditor
        {
            Id = teamRelation.Id,
            Name = teamRelation.Name,
            Type = teamRelation.Type,
            RelatedDatabaseId = teams.Id,
            ReciprocalPropertyId = reciprocalId,
            ReciprocalRelationEnabled = false
        }, "u");

        WikiDatabasePropertyConfig.Parse(updated).ReciprocalPropertyId.Should().BeNull();
        (await db.WikiDatabaseProperties.AnyAsync(property => property.Id == reciprocalId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRowAsync_ShouldRemoveReferencesToTheDeletedRow()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var projects = await service.CreateDatabaseAsync("Projects", null, "u");
        var teams = await service.CreateDatabaseAsync("Teams", null, "u");
        var relation = await service.SavePropertyAsync(projects.Id, new WikiDatabasePropertyEditor
        {
            Name = "Team",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = teams.Id,
            ReciprocalRelationEnabled = true
        }, "u");
        var project = await service.SaveRowAsync(projects.Id, new WikiDatabaseRowEditor(), "u");
        var team = await service.SaveRowAsync(teams.Id, new WikiDatabaseRowEditor(), "u");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(values, relation.Id, [team.Id.ToString()]);
        await service.SaveRowAsync(projects.Id, new WikiDatabaseRowEditor
        {
            Id = project.Id,
            Values = values.ToDictionary(item => item.Key, item => item.Value)
        }, "u");

        await service.DeleteRowAsync(teams.Id, team.Id, "u");

        var reloadedProject = (await service.GetDatabaseAsync(projects.Id))!.Rows.Single();
        WikiPropertyValues.GetMultiSelect(
            WikiPropertyValues.ParseObject(reloadedProject.PropertyValuesJson), relation.Id)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task SaveRowAsync_ShouldPersistPageBlocksAndPreserveThemDuringPropertyOnlyEdits()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var blocksJson = WikiBlockJson.Serialize([
            new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                [new WikiRichTextSpan("Full task notes")], new Dictionary<string, string>())]);

        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { BlocksJson = blocksJson }, "u");
        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { Id = row.Id }, "u");

        var reloaded = await service.GetDatabaseAsync(database.Id);
        reloaded!.Rows.Single(item => item.Id == row.Id).BlocksJson.Should().Be(blocksJson);
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
    public async Task GetInlineDatabaseAsync_ShouldReturnOrderedTypedCells()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Launch plan", null, "u");
        var title = database.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var points = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Points", Type = WikiDatabasePropertyTypes.Number }, "u");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, title.Id, "Ship inline tables");
        WikiPropertyValues.SetNumber(values, points.Id, 8.5m);
        await service.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var snapshot = await service.GetInlineDatabaseAsync(database.Id);

        snapshot.Should().NotBeNull();
        snapshot!.Properties.Select(property => property.Name).Should().Equal("Name", "Points");
        snapshot.Rows.Should().ContainSingle();
        snapshot.Rows[0].Cells.Single(cell => cell.PropertyId == title.Id).Value.Should().Be("Ship inline tables");
        snapshot.Rows[0].Cells.Single(cell => cell.PropertyId == points.Id).Value.Should().Be("8.5");
    }

    [Fact]
    public async Task SaveInlineCellAsync_ShouldPersistTypedValuesAndReturnRefreshedSnapshot()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var status = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Select,
            Options = [new WikiDatabasePropertyOption("todo", "To do", "#aaa"), new WikiDatabasePropertyOption("done", "Done", "#0f0")]
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var snapshot = await service.SaveInlineCellAsync(database.Id, row.Id, status.Id, "done", "editor");

        snapshot.Rows.Single().Cells.Single(cell => cell.PropertyId == status.Id).Value.Should().Be("done");
        var reloaded = await service.GetDatabaseAsync(database.Id);
        WikiPropertyValues.GetText(
            WikiPropertyValues.ParseObject(reloaded!.Rows.Single().PropertyValuesJson), status.Id).Should().Be("done");
    }

    [Fact]
    public async Task AddInlineRowAsync_ShouldCreateCanonicalDatabaseRow()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");

        var snapshot = await service.AddInlineRowAsync(database.Id, "editor");

        snapshot.Rows.Should().ContainSingle();
        (await service.GetDatabaseAsync(database.Id))!.Rows.Should().ContainSingle();
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
    public async Task DuplicateDatabaseAsync_ShouldCreateAnIndependentAdjacentCopyAndRemapPropertyReferences()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var source = await service.CreateDatabaseAsync("Projects", null, "owner");
        var following = await service.CreateDatabaseAsync("Following", null, "owner");
        var followingOriginalSortOrder = following.SortOrder;
        var sourceTitle = source.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var status = await service.SavePropertyAsync(source.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Select,
            Options = [new WikiDatabasePropertyOption("active", "Active", "#0f0")]
        }, "owner");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, sourceTitle.Id, "Launch");
        WikiPropertyValues.SetText(values, status.Id, "active");
        var sourceBlockId = Guid.NewGuid();
        await service.SaveRowAsync(source.Id, new WikiDatabaseRowEditor
        {
            Values = values.ToDictionary(item => item.Key, item => item.Value),
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(sourceBlockId, WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Independent notes")], new Dictionary<string, string>())])
        }, "owner");
        await service.SaveViewAsync(source.Id, null, "Board", WikiDatabaseViewTypes.Board,
            new WikiDatabaseViewConfig(
                [new WikiDatabaseFilter(status.Id.ToString(), "equals", "active")],
                [new WikiDatabaseSort(sourceTitle.Id.ToString(), "ascending")],
                status.Id.ToString(),
                WikiDatabaseOpenPageModes.FullPage), "owner");

        var duplicate = await service.DuplicateDatabaseAsync(source.Id, "member");
        var reloaded = await service.GetDatabaseAsync(duplicate.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("Projects Copy");
        reloaded.SortOrder.Should().Be(source.SortOrder + 1);
        (await service.GetDatabaseAsync(following.Id))!.SortOrder.Should().Be(followingOriginalSortOrder + 1);
        reloaded.NotionId.Should().BeNull();

        var copiedTitle = reloaded.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var copiedStatus = reloaded.Properties.Single(property => property.Name == "Status");
        copiedTitle.Id.Should().NotBe(sourceTitle.Id);
        copiedStatus.Id.Should().NotBe(status.Id);
        var copiedRow = reloaded.Rows.Single();
        var copiedValues = WikiPropertyValues.ParseObject(copiedRow.PropertyValuesJson);
        WikiPropertyValues.GetText(copiedValues, copiedTitle.Id).Should().Be("Launch");
        WikiPropertyValues.GetText(copiedValues, copiedStatus.Id).Should().Be("active");
        WikiBlockJson.ParseBlocks(copiedRow.BlocksJson).Single().Id.Should().NotBe(sourceBlockId);

        var copiedBoard = reloaded.Views.Single(view => view.Type == WikiDatabaseViewTypes.Board);
        var copiedConfig = WikiDatabaseViewConfigJson.Parse(copiedBoard.ConfigJson);
        copiedConfig.GroupByPropertyId.Should().Be(copiedStatus.Id.ToString());
        copiedConfig.Filters.Single().PropertyId.Should().Be(copiedStatus.Id.ToString());
        copiedConfig.Sorts.Single().PropertyId.Should().Be(copiedTitle.Id.ToString());
        copiedConfig.OpenPageMode.Should().Be(WikiDatabaseOpenPageModes.FullPage);
    }

    [Fact]
    public async Task DuplicateDatabaseAsync_ShouldRemapSelfRelationsAndRollupsToTheCopy()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var source = await service.CreateDatabaseAsync("Tasks", null, "u");
        var title = source.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var relation = await service.SavePropertyAsync(source.Id, new WikiDatabasePropertyEditor
        {
            Name = "Dependencies",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = source.Id
        }, "u");
        var rollup = await service.SavePropertyAsync(source.Id, new WikiDatabasePropertyEditor
        {
            Name = "Dependency count",
            Type = WikiDatabasePropertyTypes.Rollup,
            RelationPropertyId = relation.Id,
            RollupPropertyId = title.Id,
            RollupAggregation = WikiDatabaseRollupAggregations.Count
        }, "u");
        var dependencyValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(dependencyValues, title.Id, "Foundation");
        var dependency = await service.SaveRowAsync(source.Id,
            new WikiDatabaseRowEditor { Values = dependencyValues.ToDictionary(item => item.Key, item => item.Value) }, "u");
        var taskValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(taskValues, title.Id, "Launch");
        WikiPropertyValues.SetMultiSelect(taskValues, relation.Id, [dependency.Id.ToString()]);
        await service.SaveRowAsync(source.Id,
            new WikiDatabaseRowEditor { Values = taskValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var duplicate = await service.DuplicateDatabaseAsync(source.Id, "u");
        var reloaded = await service.GetDatabaseAsync(duplicate.Id);

        var copiedTitle = reloaded!.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var copiedRelation = reloaded.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Relation);
        var copiedRollup = reloaded.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Rollup);
        WikiDatabasePropertyConfig.Parse(copiedRelation).RelatedDatabaseId.Should().Be(duplicate.Id);
        var copiedRollupConfig = WikiDatabasePropertyConfig.Parse(copiedRollup);
        copiedRollupConfig.RelationPropertyId.Should().Be(copiedRelation.Id);
        copiedRollupConfig.RollupPropertyId.Should().Be(copiedTitle.Id);
        var copiedDependency = reloaded.Rows.Single(row =>
            WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), copiedTitle.Id) == "Foundation");
        var copiedTask = reloaded.Rows.Single(row =>
            WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), copiedTitle.Id) == "Launch");
        var copiedValues = WikiPropertyValues.ParseObject(copiedTask.PropertyValuesJson);
        WikiPropertyValues.GetMultiSelect(copiedValues, copiedRelation.Id).Should().Equal(copiedDependency.Id.ToString());
        WikiPropertyValues.GetComputedValue(copiedValues, copiedRollup.Id).Should().Be(1m);
    }

    [Fact]
    public async Task DuplicateDatabaseAsync_ShouldKeepSelfReciprocalRelationsInsideTheCopy()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var source = await service.CreateDatabaseAsync("People", null, "u");
        await service.SavePropertyAsync(source.Id, new WikiDatabasePropertyEditor
        {
            Name = "Manager",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = source.Id,
            ReciprocalRelationEnabled = true,
            ReciprocalPropertyName = "Reports"
        }, "u");

        var duplicate = await service.DuplicateDatabaseAsync(source.Id, "u");
        var reloaded = await service.GetDatabaseAsync(duplicate.Id);

        var manager = reloaded!.Properties.Single(property => property.Name == "Manager");
        var reports = reloaded.Properties.Single(property => property.Name == "Reports");
        var managerConfig = WikiDatabasePropertyConfig.Parse(manager);
        var reportsConfig = WikiDatabasePropertyConfig.Parse(reports);
        managerConfig.RelatedDatabaseId.Should().Be(duplicate.Id);
        reportsConfig.RelatedDatabaseId.Should().Be(duplicate.Id);
        managerConfig.ReciprocalPropertyId.Should().Be(reports.Id);
        reportsConfig.ReciprocalPropertyId.Should().Be(manager.Id);
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
    public async Task DeleteDatabaseAsync_ShouldRemoveReciprocalPropertiesFromOtherDatabases()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var projects = await service.CreateDatabaseAsync("Projects", null, "u");
        var teams = await service.CreateDatabaseAsync("Teams", null, "u");
        var relation = await service.SavePropertyAsync(projects.Id, new WikiDatabasePropertyEditor
        {
            Name = "Team",
            Type = WikiDatabasePropertyTypes.Relation,
            RelatedDatabaseId = teams.Id,
            ReciprocalRelationEnabled = true
        }, "u");
        var reciprocalId = WikiDatabasePropertyConfig.Parse(relation).ReciprocalPropertyId!.Value;

        await service.DeleteDatabaseAsync(projects.Id, "u");

        (await db.WikiDatabaseProperties.AnyAsync(property => property.Id == reciprocalId)).Should().BeFalse();
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
