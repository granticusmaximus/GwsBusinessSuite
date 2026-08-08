using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task GetDatabaseAsync_ShouldCalculateNewRollupAggregations()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var invoices = await service.CreateDatabaseAsync("Invoices", null, "u");
        var invoiceTitle = invoices.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var amount = await service.SavePropertyAsync(invoices.Id,
            new WikiDatabasePropertyEditor { Name = "Amount", Type = WikiDatabasePropertyTypes.Number }, "u");

        async Task<WikiDatabaseRow> AddInvoiceAsync(string title, decimal? invoiceAmount)
        {
            var values = new System.Text.Json.Nodes.JsonObject();
            WikiPropertyValues.SetText(values, invoiceTitle.Id, title);
            if (invoiceAmount is { } amountValue)
            {
                WikiPropertyValues.SetNumber(values, amount.Id, amountValue);
            }
            return await service.SaveRowAsync(invoices.Id,
                new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) }, "u");
        }

        // 10, 20, 30 amounts plus one row with no amount at all - covers both "value present"
        // aggregations (median/range) and "presence" aggregations (countEmpty/percentEmpty).
        var first = await AddInvoiceAsync("INV-001", 10m);
        var second = await AddInvoiceAsync("INV-002", 20m);
        var third = await AddInvoiceAsync("INV-003", 30m);
        var fourth = await AddInvoiceAsync("INV-004", null);

        var clients = await service.CreateDatabaseAsync("Clients", null, "u");
        var relation = await service.SavePropertyAsync(clients.Id, new WikiDatabasePropertyEditor
        {
            Name = "Invoices", Type = WikiDatabasePropertyTypes.Relation, RelatedDatabaseId = invoices.Id
        }, "u");

        async Task<Guid> AddRollupAsync(string aggregation) => (await service.SavePropertyAsync(clients.Id, new WikiDatabasePropertyEditor
        {
            Name = aggregation, Type = WikiDatabasePropertyTypes.Rollup,
            RelationPropertyId = relation.Id, RollupPropertyId = amount.Id, RollupAggregation = aggregation
        }, "u")).Id;

        var countEmptyId = await AddRollupAsync(WikiDatabaseRollupAggregations.CountEmpty);
        var countNotEmptyId = await AddRollupAsync(WikiDatabaseRollupAggregations.CountNotEmpty);
        var percentEmptyId = await AddRollupAsync(WikiDatabaseRollupAggregations.PercentEmpty);
        var percentNotEmptyId = await AddRollupAsync(WikiDatabaseRollupAggregations.PercentNotEmpty);
        var medianId = await AddRollupAsync(WikiDatabaseRollupAggregations.Median);
        var rangeId = await AddRollupAsync(WikiDatabaseRollupAggregations.Range);

        var clientValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(clientValues, relation.Id,
            [first.Id.ToString(), second.Id.ToString(), third.Id.ToString(), fourth.Id.ToString()]);
        await service.SaveRowAsync(clients.Id,
            new WikiDatabaseRowEditor { Values = clientValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(clients.Id);
        var computedValues = WikiPropertyValues.ParseObject(computed!.Rows.Single().PropertyValuesJson);

        WikiPropertyValues.GetComputedValue(computedValues, countEmptyId).Should().Be(1m);
        WikiPropertyValues.GetComputedValue(computedValues, countNotEmptyId).Should().Be(3m);
        WikiPropertyValues.GetComputedValue(computedValues, percentEmptyId).Should().Be(25m);
        WikiPropertyValues.GetComputedValue(computedValues, percentNotEmptyId).Should().Be(75m);
        WikiPropertyValues.GetComputedValue(computedValues, medianId).Should().Be(20m);
        WikiPropertyValues.GetComputedValue(computedValues, rangeId).Should().Be(20m);
    }

    [Fact]
    public async Task GetDatabaseAsync_ShouldEvaluateFormulaArrayFunctions()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var tags = await service.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Tags", Type = WikiDatabasePropertyTypes.MultiSelect,
                Options = [new("a", "alpha", "#111"), new("b", "beta", "#222"), new("c", "gamma", "#333")] }, "u");
        var firstTag = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "First tag", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "first([Tags])"
        }, "u");
        var lastTag = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Last tag", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "last([Tags])"
        }, "u");
        var tagCount = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Tag count", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "length([Tags])"
        }, "u");
        var joinedTags = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Joined", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "join([Tags], \"|\")"
        }, "u");
        var includesBeta = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Has beta", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "includes([Tags], \"b\")"
        }, "u");
        var sortedTags = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Sorted", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "join(sort([Tags]), \",\")"
        }, "u");
        var uniqueTags = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Unique count", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "length(unique([Tags]))"
        }, "u");
        var atIndex = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "At -1", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "at([Tags], -1)"
        }, "u");
        var sliced = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Sliced", Type = WikiDatabasePropertyTypes.Formula, FormulaExpression = "join(slice([Tags], 1), \",\")"
        }, "u");

        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(values, tags.Id, ["c", "a", "b"]);
        var row = await service.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(database.Id);
        var computedValues = WikiPropertyValues.ParseObject(computed!.Rows.Single(item => item.Id == row.Id).PropertyValuesJson);

        WikiPropertyValues.GetComputedValue(computedValues, firstTag.Id).Should().Be("c");
        WikiPropertyValues.GetComputedValue(computedValues, lastTag.Id).Should().Be("b");
        WikiPropertyValues.GetComputedValue(computedValues, tagCount.Id).Should().Be(3m);
        WikiPropertyValues.GetComputedValue(computedValues, joinedTags.Id).Should().Be("c|a|b");
        WikiPropertyValues.GetComputedValue(computedValues, includesBeta.Id).Should().Be(true);
        WikiPropertyValues.GetComputedValue(computedValues, sortedTags.Id).Should().Be("a,b,c");
        WikiPropertyValues.GetComputedValue(computedValues, uniqueTags.Id).Should().Be(3m);
        WikiPropertyValues.GetComputedValue(computedValues, atIndex.Id).Should().Be("b");
        WikiPropertyValues.GetComputedValue(computedValues, sliced.Id).Should().Be("a,b");
    }

    [Fact]
    public async Task GetDatabaseAsync_ShouldResolveChainedRollupsTwoHopsDeep()
    {
        // Regression guard: GetDatabaseAsync used to only pre-fetch databases directly related
        // to the one being loaded. A rollup-of-a-rollup needs the *second* hop's own related
        // database resolved too, or evaluating it fails with "#REF! Related database missing".
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);

        var invoices = await service.CreateDatabaseAsync("Invoices", null, "u");
        var invoiceTitle = invoices.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var amount = await service.SavePropertyAsync(invoices.Id,
            new WikiDatabasePropertyEditor { Name = "Amount", Type = WikiDatabasePropertyTypes.Number }, "u");
        var invoiceValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(invoiceValues, invoiceTitle.Id, "INV-001");
        WikiPropertyValues.SetNumber(invoiceValues, amount.Id, 100m);
        var invoice = await service.SaveRowAsync(invoices.Id,
            new WikiDatabaseRowEditor { Values = invoiceValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        // Clients -> Invoices, rolling up Amount (this is the first, directly-related hop).
        var clients = await service.CreateDatabaseAsync("Clients", null, "u");
        var invoiceRelation = await service.SavePropertyAsync(clients.Id, new WikiDatabasePropertyEditor
        {
            Name = "Invoices", Type = WikiDatabasePropertyTypes.Relation, RelatedDatabaseId = invoices.Id
        }, "u");
        var revenue = await service.SavePropertyAsync(clients.Id, new WikiDatabasePropertyEditor
        {
            Name = "Revenue", Type = WikiDatabasePropertyTypes.Rollup,
            RelationPropertyId = invoiceRelation.Id, RollupPropertyId = amount.Id,
            RollupAggregation = WikiDatabaseRollupAggregations.Sum
        }, "u");
        var clientValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(clientValues, invoiceRelation.Id, [invoice.Id.ToString()]);
        var client = await service.SaveRowAsync(clients.Id,
            new WikiDatabaseRowEditor { Values = clientValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        // Regions -> Clients, rolling up Revenue - which is itself a Rollup, so resolving this
        // needs Invoices (the second hop, only related to Clients, not directly to Regions).
        var regions = await service.CreateDatabaseAsync("Regions", null, "u");
        var clientRelation = await service.SavePropertyAsync(regions.Id, new WikiDatabasePropertyEditor
        {
            Name = "Clients", Type = WikiDatabasePropertyTypes.Relation, RelatedDatabaseId = clients.Id
        }, "u");
        var totalRevenue = await service.SavePropertyAsync(regions.Id, new WikiDatabasePropertyEditor
        {
            Name = "Total revenue", Type = WikiDatabasePropertyTypes.Rollup,
            RelationPropertyId = clientRelation.Id, RollupPropertyId = revenue.Id,
            RollupAggregation = WikiDatabaseRollupAggregations.Sum
        }, "u");
        var regionValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(regionValues, clientRelation.Id, [client.Id.ToString()]);
        await service.SaveRowAsync(regions.Id,
            new WikiDatabaseRowEditor { Values = regionValues.ToDictionary(item => item.Key, item => item.Value) }, "u");

        var computed = await service.GetDatabaseAsync(regions.Id);

        var computedValues = WikiPropertyValues.ParseObject(computed!.Rows.Single().PropertyValuesJson);
        WikiPropertyValues.GetComputedValue(computedValues, totalRevenue.Id).Should().Be(100m);
        WikiPropertyValues.GetDisplayText(
            computed.Properties.Single(property => property.Id == totalRevenue.Id),
            computedValues,
            computed.Rows.Single().CreatedAt).Should().NotContain("#REF!");
    }

    [Fact]
    public async Task MoveRowAsync_ShouldRejectAGroupByPropertyThatIsNotASelectType()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Board", null, "u");
        var titleProperty = database.Properties.Single(property => property.Type == WikiDatabasePropertyTypes.Title);
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var act = async () => await service.MoveRowAsync(database.Id, row.Id, titleProperty.Id, "anything", 0, "u");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Select or Status property*");
    }

    [Fact]
    public async Task MoveRowAsync_ShouldRejectAGroupOptionIdThatDoesNotExistOnTheProperty()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Board", null, "u");
        var statusProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status", Type = WikiDatabasePropertyTypes.Select,
            Options = [new WikiDatabasePropertyOption("todo", "To Do", "#ccc")]
        }, "u");
        var row = await service.SaveRowAsync(database.Id, RowWithStatus(statusProperty.Id, "todo"), "u");

        var act = async () => await service.MoveRowAsync(database.Id, row.Id, statusProperty.Id, "no-such-option", 0, "u");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*group option*");
    }

    [Fact]
    public async Task MoveRowAsync_ShouldRejectAGroupByPropertyIdFromAnotherDatabase()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Board", null, "u");
        var otherDatabase = await service.CreateDatabaseAsync("Other", null, "u");
        var otherSelect = await service.SavePropertyAsync(otherDatabase.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status", Type = WikiDatabasePropertyTypes.Select,
            Options = [new WikiDatabasePropertyOption("todo", "To Do", "#ccc")]
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var act = async () => await service.MoveRowAsync(database.Id, row.Id, otherSelect.Id, "todo", 0, "u");

        await act.Should().ThrowAsync<KeyNotFoundException>();
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

        await service.DeleteRowPermanentlyAsync(teams.Id, team.Id, "u");

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
    public async Task AddInlineBoardRowAsync_ShouldCreateTheTaskInTheRequestedColumn()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Board", null, "u");
        var statusProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Select,
            Options =
            [
                new WikiDatabasePropertyOption("todo", "To Do", "gray"),
                new WikiDatabasePropertyOption("done", "Done", "green")
            ]
        }, "u");

        var snapshot = await service.AddInlineBoardRowAsync(
            database.Id,
            statusProperty.Id,
            "done",
            "Ship Kanban",
            "u");

        snapshot.Rows.Should().ContainSingle();
        snapshot.Rows.Single().Cells.Should().Contain(cell =>
            cell.PropertyId == statusProperty.Id && cell.Value == "done");
        snapshot.Rows.Single().Cells.Should().Contain(cell =>
            cell.Value == "Ship Kanban");
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

    [Theory]
    [InlineData(WikiDatabasePropertyTypes.CreatedTime)]
    [InlineData(WikiDatabasePropertyTypes.LastEditedTime)]
    [InlineData(WikiDatabasePropertyTypes.LastEditedBy)]
    [InlineData(WikiDatabasePropertyTypes.CreatedBy)]
    [InlineData(WikiDatabasePropertyTypes.Formula)]
    [InlineData(WikiDatabasePropertyTypes.Rollup)]
    public async Task SaveInlineCellAsync_ShouldRejectComputedPropertyTypes(string propertyType)
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        WikiDatabaseProperty? relationSource = null;
        WikiDatabaseProperty? rollupTarget = null;
        if (propertyType is WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup)
        {
            var otherDatabase = await service.CreateDatabaseAsync("Other", null, "u");
            relationSource = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
            {
                Name = "Related",
                Type = WikiDatabasePropertyTypes.Relation,
                RelatedDatabaseId = otherDatabase.Id
            }, "u");
            rollupTarget = otherDatabase.Properties.Single(item => item.Type == WikiDatabasePropertyTypes.Title);
        }
        var property = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Computed",
            Type = propertyType,
            FormulaExpression = propertyType == WikiDatabasePropertyTypes.Formula ? "1 + 1" : null,
            RelationPropertyId = propertyType == WikiDatabasePropertyTypes.Rollup ? relationSource!.Id : null,
            RollupPropertyId = propertyType == WikiDatabasePropertyTypes.Rollup ? rollupTarget!.Id : null,
            RollupAggregation = propertyType == WikiDatabasePropertyTypes.Rollup ? WikiDatabaseRollupAggregations.Count : null
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var action = async () => await service.SaveInlineCellAsync(database.Id, row.Id, property.Id, "attempted-value", "editor");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveInlineCellAsync_ShouldRejectAButtonProperty()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var button = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Run",
            Type = WikiDatabasePropertyTypes.Button,
            ButtonLabel = "Run it"
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var action = async () => await service.SaveInlineCellAsync(database.Id, row.Id, button.Id, "click", "editor");

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveInlineCellAsync_ShouldValidateStatusOptionsLikeSelect()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var status = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status",
            Type = WikiDatabasePropertyTypes.Status,
            Options =
            [
                new WikiDatabasePropertyOption("todo", "To do", "#aaa", WikiDatabaseStatusGroups.ToDo),
                new WikiDatabasePropertyOption("done", "Done", "#0f0", WikiDatabaseStatusGroups.Complete)
            ]
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var snapshot = await service.SaveInlineCellAsync(database.Id, row.Id, status.Id, "done", "editor");
        snapshot.Rows.Single().Cells.Single(cell => cell.PropertyId == status.Id).Value.Should().Be("done");

        var invalidSnapshot = await service.SaveInlineCellAsync(database.Id, row.Id, status.Id, "not-a-real-option", "editor");
        invalidSnapshot.Rows.Single().Cells.Single(cell => cell.PropertyId == status.Id).Value.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WikiDatabasePropertyTypes.Email, "person@example.com")]
    [InlineData(WikiDatabasePropertyTypes.Phone, "+1 555-0100")]
    public async Task SaveInlineCellAsync_ShouldPersistEmailAndPhoneAsPlainText(string propertyType, string value)
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Contacts", null, "u");
        var property = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Contact info",
            Type = propertyType
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var snapshot = await service.SaveInlineCellAsync(database.Id, row.Id, property.Id, value, "editor");

        snapshot.Rows.Single().Cells.Single(cell => cell.PropertyId == property.Id).Value.Should().Be(value);
    }

    [Fact]
    public async Task SavePropertyAsync_ShouldRejectAButtonThatPointsToAMissingWorkflow()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");

        var action = async () => await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Run",
            Type = WikiDatabasePropertyTypes.Button,
            ButtonLabel = "Run it",
            AutomationWorkflowId = Guid.NewGuid()
        }, "u");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(WikiDatabasePropertyTypes.Relation)]
    [InlineData(WikiDatabasePropertyTypes.Person)]
    [InlineData(WikiDatabasePropertyTypes.Files)]
    public async Task SaveInlineCellAsync_ShouldRejectArrayShapedPropertyTypes(string propertyType)
    {
        // Regression guard for a real finding: this method's value parameter is always a
        // single scalar string, but Relation/Person/Files are JSON-array-shaped
        // (WikiPropertyValues.SetRelation/SetPerson/SetFiles). Falling through to the switch's
        // default SetText(...) branch used to silently overwrite the array with a plain
        // string - every reader (dependent rollups, reciprocal relation sync, the row detail
        // panel) then read the property back as empty, with no error anywhere. This must throw
        // instead of silently corrupting the row, matching how CreatedTime/Formula/Rollup
        // (the other properties this method already refuses to touch) behave.
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var relatedDatabaseId = propertyType == WikiDatabasePropertyTypes.Relation
            ? (await service.CreateDatabaseAsync("Related", null, "u")).Id
            : (Guid?)null;
        var property = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Linked",
            Type = propertyType,
            RelatedDatabaseId = relatedDatabaseId
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var action = async () => await service.SaveInlineCellAsync(database.Id, row.Id, property.Id, "some-scalar-value", "editor");

        await action.Should().ThrowAsync<InvalidOperationException>();
        var reloaded = await service.GetDatabaseAsync(database.Id);
        reloaded!.Rows.Single().PropertyValuesJson.Should().NotContain("some-scalar-value");
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
                WikiDatabaseOpenPageModes.FullPage,
                [status.Id.ToString(), sourceTitle.Id.ToString()],
                [status.Id.ToString()],
                new Dictionary<string, string> { [sourceTitle.Id.ToString()] = "count" }), "owner");

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
        copiedConfig.PagePropertyOrder.Should().Equal(
            copiedStatus.Id.ToString(),
            copiedTitle.Id.ToString());
        copiedConfig.HiddenPagePropertyIds.Should().Equal(copiedStatus.Id.ToString());
        copiedConfig.Calculations.Should().Contain(copiedTitle.Id.ToString(), "count");
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

        await service.DeleteDatabasePermanentlyAsync(database.Id, "u");

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

        await service.DeleteDatabasePermanentlyAsync(projects.Id, "u");

        (await db.WikiDatabaseProperties.AnyAsync(property => property.Id == reciprocalId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDatabaseAsync_ShouldRemoveSentinelPermissionsAndPublicSharesForTheDatabase()
    {
        // Regression guard: SentinelResourcePermissions/SentinelPublicShares reference a
        // database polymorphically (TargetId + IsDatabase), so they can't have a real FK -
        // previously nothing cleaned them up on database delete and they were dangling forever.
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var access = new GwsBusinessSuite.Infrastructure.Services.SentinelAccessService(db);
        var database = await service.CreateDatabaseAsync("Temp", null, "u");
        await access.SetPermissionAsync(database.Id, true, "viewer", SentinelAccessLevels.View, "u");
        await access.CreatePublicShareAsync(database.Id, true, null, false, null, "u");

        await service.DeleteDatabasePermanentlyAsync(database.Id, "u");

        (await db.SentinelResourcePermissions.Where(x => x.TargetId == database.Id).ToListAsync()).Should().BeEmpty();
        (await db.SentinelPublicShares.Where(x => x.TargetId == database.Id).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TrashDatabaseAsync_ShouldHideTheDatabaseButLeaveItsRowsPhysicallyIntact()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Temp", null, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        await service.TrashDatabaseAsync(database.Id, "u");

        (await service.ListDatabasesAsync()).Should().BeEmpty();
        (await service.GetDatabaseAsync(database.Id)).Should().BeNull();
        (await service.ListTrashedDatabasesAsync()).Should().ContainSingle(d => d.Id == database.Id);
        // The row itself was never trashed - it's hidden transitively because its parent
        // database is, and comes back automatically once the database is restored.
        (await db.WikiDatabaseRows.SingleAsync(r => r.Id == row.Id)).TrashedAt.Should().BeNull();
    }

    [Fact]
    public async Task RestoreDatabaseAsync_ShouldBringBackTheDatabaseAndAllItsRows()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Temp", null, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");
        await service.TrashDatabaseAsync(database.Id, "u");

        await service.RestoreDatabaseAsync(database.Id, "u");

        var restored = await service.GetDatabaseAsync(database.Id);
        restored.Should().NotBeNull();
        restored!.Rows.Should().ContainSingle(r => r.Id == row.Id);
        (await service.ListTrashedDatabasesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreDatabaseAsync_ShouldReparentToRoot_WhenTheOriginalParentPageIsStillTrashed()
    {
        await using var db = await CreateDbAsync();
        var wikiService = new WikiService(db);
        var databaseService = new WikiDatabaseService(db);
        var page = await wikiService.SavePageAsync(new WikiPageEditorModel { Title = "Parent page" }, "u");
        var database = await databaseService.CreateDatabaseAsync("Nested", page.Id, "u");
        await wikiService.TrashPageAsync(page.Id, "u");

        await databaseService.RestoreDatabaseAsync(database.Id, "u");

        var restored = await databaseService.GetDatabaseAsync(database.Id);
        restored.Should().NotBeNull();
        restored!.ParentWikiPageId.Should().BeNull();
    }

    [Fact]
    public async Task SaveRowAsync_ShouldThrow_WhenEditingATrashedRow()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Temp", null, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");
        await service.TrashRowAsync(database.Id, row.Id, "u");

        var act = () => service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { Id = row.Id }, "u");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Trash*");
    }

    [Fact]
    public async Task TrashRowAsync_ShouldHideRow_AndRestoreRowAsync_ShouldBringItBack()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Temp", null, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        await service.TrashRowAsync(database.Id, row.Id, "u");

        (await service.GetDatabaseAsync(database.Id))!.Rows.Should().BeEmpty();
        (await service.ListTrashedRowsAsync(database.Id)).Should().ContainSingle(r => r.Id == row.Id);

        await service.RestoreRowAsync(database.Id, row.Id, "u");

        (await service.GetDatabaseAsync(database.Id))!.Rows.Should().ContainSingle(r => r.Id == row.Id);
        (await service.ListTrashedRowsAsync(database.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task TrashRowAsync_ShouldLeaveOtherRowsRelationReferencesIntact()
    {
        // Unlike DeleteRowPermanentlyAsync, trash is reversible - a row referencing this one
        // via a Relation property must keep that reference while the row is only trashed.
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var targets = await service.CreateDatabaseAsync("Targets", null, "u");
        var target = await service.SaveRowAsync(targets.Id, new WikiDatabaseRowEditor(), "u");
        var sources = await service.CreateDatabaseAsync("Sources", null, "u");
        var relation = await service.SavePropertyAsync(sources.Id, new WikiDatabasePropertyEditor
        {
            Name = "Target", Type = WikiDatabasePropertyTypes.Relation, RelatedDatabaseId = targets.Id
        }, "u");
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetMultiSelect(values, relation.Id, [target.Id.ToString()]);
        var source = await service.SaveRowAsync(sources.Id,
            new WikiDatabaseRowEditor { Values = values.ToDictionary(kv => kv.Key, kv => kv.Value) }, "u");

        await service.TrashRowAsync(targets.Id, target.Id, "u");

        var reloadedSource = (await db.WikiDatabaseRows.AsNoTracking().SingleAsync(r => r.Id == source.Id));
        WikiPropertyValues.GetMultiSelect(WikiPropertyValues.ParseObject(reloadedSource.PropertyValuesJson), relation.Id)
            .Should().Equal(target.Id.ToString());
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

    [Fact]
    public async Task SaveRowAsync_ShouldPreserveIconAndCover_OnAPropertyOnlyEdit()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var created = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            BlocksJson = ParagraphBlocks("Body."),
            Icon = "🚀",
            CoverImageUrl = "https://example.com/cover.png"
        }, "u");

        var values = WikiPropertyValues.ParseObject(created.PropertyValuesJson);
        WikiPropertyValues.SetText(values, titleProperty.Id, "Renamed");
        var propertyOnlyEdit = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = created.Id,
            Values = values.ToDictionary(kv => kv.Key, kv => kv.Value)
        }, "u");

        propertyOnlyEdit.Icon.Should().Be("🚀", "a null Icon/CoverImageUrl on the editor means preserve, not clear");
        propertyOnlyEdit.CoverImageUrl.Should().Be("https://example.com/cover.png");
        propertyOnlyEdit.BlocksJson.Should().Contain("Body.", "a property-only save must not touch the page body either");
    }

    [Fact]
    public async Task SaveRowAsync_ShouldOnlyCreateHistory_WhenBlocksJsonIsPartOfTheSave()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);

        // AddInlineRowAsync-style blank-editor row creation should not create a revision -
        // there is no page body yet.
        var blank = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");
        (await service.GetRowHistoryAsync(blank.Id)).Should().BeEmpty();

        var opened = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = blank.Id,
            BlocksJson = ParagraphBlocks("First save.")
        }, "u");
        (await service.GetRowHistoryAsync(opened.Id)).Should().ContainSingle();

        var values = WikiPropertyValues.ParseObject(opened.PropertyValuesJson);
        WikiPropertyValues.SetText(values, titleProperty.Id, "Renamed");
        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = blank.Id,
            Values = values.ToDictionary(kv => kv.Key, kv => kv.Value)
        }, "u");
        (await service.GetRowHistoryAsync(blank.Id)).Should().ContainSingle("a property-only save must not add a noise revision");
    }

    [Fact]
    public async Task SaveRowAsync_WithCreateRevisionCheckpointFalse_ShouldPersistContentWithoutAddingAVersion()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var created = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { BlocksJson = ParagraphBlocks("Checkpoint.") }, "u");
        (await service.GetRowHistoryAsync(created.Id)).Should().ContainSingle();

        // Mirrors a silent autosave tick: content changes, but no version-history entry.
        var autosaved = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = created.Id,
            BlocksJson = ParagraphBlocks("Mid-edit, not yet saved by the user."),
            CreateRevisionCheckpoint = false
        }, "u");

        autosaved.BlocksJson.Should().Contain("Mid-edit, not yet saved by the user.", "autosave must still persist the content");
        (await service.GetRowHistoryAsync(created.Id)).Should().ContainSingle("an autosave tick must not mint a new version");

        // A subsequent explicit save (checkpoint defaults true) still creates a version on
        // top of whatever autosave already persisted.
        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = created.Id,
            BlocksJson = ParagraphBlocks("Explicitly saved.")
        }, "u");
        (await service.GetRowHistoryAsync(created.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveRowAsync_ShouldTrimOldRowRevisions_BeyondMaxRevisionsPerRow()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var created = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { BlocksJson = ParagraphBlocks("v0") }, "u");

        for (var i = 1; i <= 25; i++)
        {
            await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
            {
                Id = created.Id,
                BlocksJson = ParagraphBlocks($"v{i}")
            }, "u");
        }

        var history = await service.GetRowHistoryAsync(created.Id);
        history.Should().HaveCount(20, "revisions are trimmed to WikiDatabaseService.MaxRevisionsPerRow");
        history[0].RevisionNumber.Should().Be(26, "the newest revisions are kept, not the oldest");
    }

    [Fact]
    public async Task RevertRowToRevisionAsync_ShouldRestoreOldContent_AsANewVersion()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var created = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { BlocksJson = ParagraphBlocks("Version one.") }, "u");
        var firstRevisionId = (await service.GetRowHistoryAsync(created.Id))[0].Id;

        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = created.Id,
            BlocksJson = ParagraphBlocks("Version two.")
        }, "u");

        var reverted = await service.RevertRowToRevisionAsync(database.Id, created.Id, firstRevisionId, "u");

        reverted.BlocksJson.Should().Contain("Version one.");

        var history = await service.GetRowHistoryAsync(created.Id);
        history.Should().HaveCount(3, "the revert itself is a new version, not a history rewrite");
    }

    [Fact]
    public async Task GetRowStructuralDiffAsync_ShouldDescribeChangedBlocks()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Projects", null, "u");
        var created = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { BlocksJson = ParagraphBlocks("Step one.") }, "u");
        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { Id = created.Id, BlocksJson = ParagraphBlocks("Step two.") }, "u");

        var history = await service.GetRowHistoryAsync(created.Id);
        var diff = await service.GetRowStructuralDiffAsync(created.Id, history[1].Id, history[0].Id);

        diff.Should().NotBeNullOrEmpty();
        diff.Should().Contain("Step two.");
    }

    [Fact]
    public async Task SaveRowAsync_ShouldFireASubscribedActiveAutomation_OnlyWhenPropertyValuesActuallyChange()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, NullLogger<AutomationTriggerService>.Instance);
        var service = new WikiDatabaseService(db, triggers);

        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);

        var subscriber = await workflowService.CreateAsync("On task change");
        var trigger = await workflowService.SaveNodeAsync(subscriber.Id, new AutomationNodeEditor
        {
            Name = "Row changed",
            TypeKey = "database.rowChangedTrigger",
            PositionX = 120,
            PositionY = 420,
            ParametersJson = $"{{\"wikiDatabaseId\":\"{database.Id}\"}}"
        });
        var setNode = await workflowService.SaveNodeAsync(subscriber.Id, new AutomationNodeEditor
        {
            Name = "Note",
            TypeKey = "core.set",
            PositionX = 400,
            PositionY = 420,
            ParametersJson = "{\"values\":{\"seen\":\"yes\"}}"
        });
        await workflowService.AddConnectionAsync(subscriber.Id, trigger.Id, "main", setNode.Id);
        await workflowService.PublishAsync(subscriber.Id, "v1");
        await workflowService.SetActiveAsync(subscriber.Id, true);

        var row = await service.SaveRowAsync(database.Id, RowWithStatus(titleProperty.Id, "First task"), "u");
        (await db.AutomationExecutions.AsNoTracking().Where(item => item.WorkflowId == subscriber.Id).ToListAsync())
            .Should().ContainSingle("creating a row is a property change");

        // Re-saving the exact same values (e.g. a body-only autosave that also resends the
        // unchanged current property values) must not re-fire the automation.
        var unchangedEditor = RowWithStatus(titleProperty.Id, "First task");
        unchangedEditor.Id = row.Id;
        await service.SaveRowAsync(database.Id, unchangedEditor, "u");
        (await db.AutomationExecutions.AsNoTracking().CountAsync(item => item.WorkflowId == subscriber.Id))
            .Should().Be(1, "no property value actually changed on this save");

        // An actual property change fires it again.
        var renameEditor = RowWithStatus(titleProperty.Id, "Renamed task");
        renameEditor.Id = row.Id;
        await service.SaveRowAsync(database.Id, renameEditor, "u");
        (await db.AutomationExecutions.AsNoTracking().CountAsync(item => item.WorkflowId == subscriber.Id))
            .Should().Be(2);
    }

    [Fact]
    public async Task DatabaseSetRowPropertyNode_ShouldWriteBackTheRowWithoutRetriggeringItsOwnWorkflow()
    {
        await using var db = await CreateDbAsync();
        IWikiDatabaseService? wikiDatabaseService = null;
        var dbContextFactoryOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(db.Database.GetDbConnection())
            .Options;
        var registry = new AutomationNodeRegistry(
            new FakeHttpClient(),
            dbContextFactory: new FakeAppDbContextFactory(dbContextFactoryOptions),
            serviceProvider: new SingleServiceProvider(() => wikiDatabaseService));
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, NullLogger<AutomationTriggerService>.Instance);
        var service = new WikiDatabaseService(db, triggers);
        wikiDatabaseService = service;

        // database.setRowProperty now checks that the workflow's own owner has Edit access to
        // the target database (see AutomationNodeRegistry.EnsureCanEditDatabaseAsync) - an
        // Admin bypasses that check the same way Admins bypass SentinelResourcePermission
        // everywhere else, which is what this test's automation write-back relies on.
        // AutomationWorkflowService.CreateAsync hardcodes CreatedBy to "user", not the "u"
        // used elsewhere in this test as the acting username for row saves.
        db.AppUsers.Add(new AppUser { Username = "user", Role = AppRoles.Admin, PasswordHash = "hash" });
        await db.SaveChangesAsync();

        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await service.SavePropertyAsync(
            database.Id, new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "u");

        var workflow = await workflowService.CreateAsync("Auto-mark synced");
        var trigger = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Row changed",
            TypeKey = "database.rowChangedTrigger",
            PositionX = 120,
            PositionY = 420,
            ParametersJson = $"{{\"wikiDatabaseId\":\"{database.Id}\"}}"
        });
        var writeBack = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Mark synced",
            TypeKey = "database.setRowProperty",
            PositionX = 400,
            PositionY = 420,
            ParametersJson = $"{{\"wikiDatabaseId\":\"{database.Id}\",\"rowId\":\"{{{{ $json.rowId }}}}\",\"propertyId\":\"{statusProperty.Id}\",\"value\":\"synced-by-automation\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", writeBack.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");
        await workflowService.SetActiveAsync(workflow.Id, true);

        var row = await service.SaveRowAsync(database.Id, RowWithStatus(titleProperty.Id, "First task"), "u");

        var updatedRow = await db.WikiDatabaseRows.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(updatedRow.PropertyValuesJson), statusProperty.Id)
            .Should().Be("synced-by-automation");

        // The write-back node's own save must not re-fire database.rowChangedTrigger on this
        // same database - otherwise this workflow would trigger itself indefinitely.
        (await db.AutomationExecutions.AsNoTracking().Where(item => item.WorkflowId == workflow.Id).ToListAsync())
            .Should().ContainSingle("the write-back save must not spawn a second execution of its own workflow");
    }

    [Fact]
    public async Task DatabaseSetRowPropertyNode_ShouldRejectWritesFromANonAdminOwnerWithoutSentinelEditAccess()
    {
        // Regression guard for a real finding: this node could write to any Sentinel
        // database/row by id with no ownership or scoping check at all - a non-Admin workflow
        // owner could edit a database they have no Sentinel access to, bypassing the same
        // per-resource Edit check the Wiki UI itself enforces for them.
        await using var db = await CreateDbAsync();
        IWikiDatabaseService? wikiDatabaseService = null;
        var dbContextFactoryOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(db.Database.GetDbConnection())
            .Options;
        var accessService = new SentinelAccessService(db);
        var registry = new AutomationNodeRegistry(
            new FakeHttpClient(),
            dbContextFactory: new FakeAppDbContextFactory(dbContextFactoryOptions),
            serviceProvider: new AutomationServiceProvider(() => wikiDatabaseService, accessService));
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, NullLogger<AutomationTriggerService>.Instance);
        var service = new WikiDatabaseService(db, triggers);
        wikiDatabaseService = service;

        // "user" (AutomationWorkflowService.CreateAsync's hardcoded workflow owner) has no
        // AppUsers row at all here, so it's neither an Admin nor holds any Sentinel grant.
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await service.SavePropertyAsync(
            database.Id, new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "u");
        var row = await service.SaveRowAsync(database.Id, RowWithStatus(titleProperty.Id, "First task"), "u");

        var workflow = await workflowService.CreateAsync("Unauthorized write");
        var writeBack = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Mark synced",
            TypeKey = "database.setRowProperty",
            PositionX = 400,
            PositionY = 420,
            ParametersJson = $"{{\"wikiDatabaseId\":\"{database.Id}\",\"rowId\":\"{row.Id}\",\"propertyId\":\"{statusProperty.Id}\",\"value\":\"synced-by-automation\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", writeBack.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executions.ExecuteAsync(workflow.Id, "{}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        execution.ErrorMessage.Should().Contain("does not have Edit access");
        var reloadedRow = await db.WikiDatabaseRows.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(reloadedRow.PropertyValuesJson), statusProperty.Id)
            .Should().BeNull("the unauthorized write must not have applied");
    }

    [Fact]
    public async Task DatabaseSetRowPropertyNode_ShouldAllowWritesFromANonAdminOwnerWithExplicitEditAccess()
    {
        await using var db = await CreateDbAsync();
        IWikiDatabaseService? wikiDatabaseService = null;
        var dbContextFactoryOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(db.Database.GetDbConnection())
            .Options;
        var accessService = new SentinelAccessService(db);
        var registry = new AutomationNodeRegistry(
            new FakeHttpClient(),
            dbContextFactory: new FakeAppDbContextFactory(dbContextFactoryOptions),
            serviceProvider: new AutomationServiceProvider(() => wikiDatabaseService, accessService));
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, NullLogger<AutomationTriggerService>.Instance);
        var service = new WikiDatabaseService(db, triggers);
        wikiDatabaseService = service;

        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await service.SavePropertyAsync(
            database.Id, new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "u");
        var row = await service.SaveRowAsync(database.Id, RowWithStatus(titleProperty.Id, "First task"), "u");

        // Grant "user" (the hardcoded workflow owner) real Edit access to this specific
        // database, matching exactly what a Contributor would need through the Wiki Share
        // panel to edit it directly.
        await accessService.SetPermissionAsync(database.Id, isDatabase: true, "user", SentinelAccessLevels.Edit, "admin");

        var workflow = await workflowService.CreateAsync("Authorized write");
        var writeBack = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Mark synced",
            TypeKey = "database.setRowProperty",
            PositionX = 400,
            PositionY = 420,
            ParametersJson = $"{{\"wikiDatabaseId\":\"{database.Id}\",\"rowId\":\"{row.Id}\",\"propertyId\":\"{statusProperty.Id}\",\"value\":\"synced-by-automation\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", writeBack.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executions.ExecuteAsync(workflow.Id, "{}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        var reloadedRow = await db.WikiDatabaseRows.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(reloadedRow.PropertyValuesJson), statusProperty.Id)
            .Should().Be("synced-by-automation");
    }

    private sealed class SingleServiceProvider(Func<object?> resolver) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IWikiDatabaseService) ? resolver() : null;
    }

    private sealed class AutomationServiceProvider(Func<object?> wikiDatabaseServiceResolver, ISentinelAccessService accessService) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWikiDatabaseService) ? wikiDatabaseServiceResolver()
            : serviceType == typeof(ISentinelAccessService) ? accessService
            : null;
    }

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options)
        : GwsBusinessSuite.Application.Abstractions.IAppDbContextFactory
    {
        public Task<GwsBusinessSuite.Application.Abstractions.IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GwsBusinessSuite.Application.Abstractions.IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class FakeHttpClient : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
    }

    private sealed class FakeSecretProtector : GwsBusinessSuite.Application.Abstractions.ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext))}";
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[11..]));
    }

    private static string ParagraphBlocks(string text) => WikiBlockJson.Serialize(
    [
        new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>())
    ]);

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
