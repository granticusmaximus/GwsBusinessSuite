using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class WikiDatabaseViewLogicTests
{
    [Fact]
    public void ApplyFilters_TextContains_ShouldMatchCaseInsensitively()
    {
        var titleProperty = NewProperty(WikiDatabasePropertyTypes.Text);
        var rows = new[] { RowWithText(titleProperty.Id, "Deploy Runbook"), RowWithText(titleProperty.Id, "Onboarding Guide") };

        var filtered = WikiDatabaseViewLogic.ApplyFilters(rows, [titleProperty], [new WikiDatabaseFilter(titleProperty.Id.ToString(), "contains", "deploy")]);

        filtered.Should().ContainSingle();
        WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(filtered[0].PropertyValuesJson), titleProperty.Id).Should().Be("Deploy Runbook");
    }

    [Fact]
    public void ApplyFilters_NumberGreaterThan_ShouldExcludeNonMatchingRows()
    {
        var pointsProperty = NewProperty(WikiDatabasePropertyTypes.Number);
        var rows = new[] { RowWithNumber(pointsProperty.Id, 3m), RowWithNumber(pointsProperty.Id, 8m) };

        var filtered = WikiDatabaseViewLogic.ApplyFilters(rows, [pointsProperty], [new WikiDatabaseFilter(pointsProperty.Id.ToString(), "greaterThan", "5")]);

        filtered.Should().ContainSingle();
        WikiPropertyValues.GetNumber(WikiPropertyValues.ParseObject(filtered[0].PropertyValuesJson), pointsProperty.Id).Should().Be(8m);
    }

    [Fact]
    public void ApplyFilters_CheckboxIsChecked_ShouldMatchOnlyCheckedRows()
    {
        var doneProperty = NewProperty(WikiDatabasePropertyTypes.Checkbox);
        var rows = new[] { RowWithCheckbox(doneProperty.Id, true), RowWithCheckbox(doneProperty.Id, false) };

        var filtered = WikiDatabaseViewLogic.ApplyFilters(rows, [doneProperty], [new WikiDatabaseFilter(doneProperty.Id.ToString(), "isChecked", "")]);

        filtered.Should().ContainSingle();
        WikiPropertyValues.GetCheckbox(WikiPropertyValues.ParseObject(filtered[0].PropertyValuesJson), doneProperty.Id).Should().BeTrue();
    }

    [Fact]
    public void ApplySort_Number_Descending_ShouldOrderHighestFirst()
    {
        var pointsProperty = NewProperty(WikiDatabasePropertyTypes.Number);
        var rows = new[] { RowWithNumber(pointsProperty.Id, 3m), RowWithNumber(pointsProperty.Id, 8m), RowWithNumber(pointsProperty.Id, 1m) };

        var sorted = WikiDatabaseViewLogic.ApplySort(rows, [pointsProperty], [new WikiDatabaseSort(pointsProperty.Id.ToString(), "descending")]);

        sorted.Select(r => WikiPropertyValues.GetNumber(WikiPropertyValues.ParseObject(r.PropertyValuesJson), pointsProperty.Id))
            .Should().BeEquivalentTo([8m, 3m, 1m], options => options.WithStrictOrdering());
    }

    [Fact]
    public void ApplySort_NoSorts_ShouldFallBackToSortOrder()
    {
        var textProperty = NewProperty(WikiDatabasePropertyTypes.Text);
        var second = RowWithText(textProperty.Id, "second");
        second.SortOrder = 1;
        var first = RowWithText(textProperty.Id, "first");
        first.SortOrder = 0;
        var rows = new[] { second, first };

        var sorted = WikiDatabaseViewLogic.ApplySort(rows, [textProperty], []);

        sorted.Select(r => r.SortOrder).Should().BeEquivalentTo([0, 1], options => options.WithStrictOrdering());
    }

    [Fact]
    public void GroupForBoard_ShouldBucketRowsByOptionAndKeepConfiguredOptionOrder()
    {
        var statusProperty = NewProperty(WikiDatabasePropertyTypes.Select);
        statusProperty.ConfigJson = WikiDatabasePropertyConfig.Serialize(
        [
            new WikiDatabasePropertyOption("todo", "To Do", "#ccc"),
            new WikiDatabasePropertyOption("done", "Done", "#0f0")
        ]);
        var rows = new[]
        {
            RowWithText(statusProperty.Id, "done"),
            RowWithText(statusProperty.Id, "todo"),
            RowWithText(statusProperty.Id, "todo"),
            RowWithText(statusProperty.Id, "")
        };

        var groups = WikiDatabaseViewLogic.GroupForBoard(rows, statusProperty);

        groups.Should().HaveCount(3, "two configured options plus the 'No status' bucket");
        groups[0].Label.Should().Be("To Do");
        groups[0].Rows.Should().HaveCount(2);
        groups[1].Label.Should().Be("Done");
        groups[1].Rows.Should().HaveCount(1);
        groups[2].Label.Should().Be("No status");
        groups[2].Rows.Should().HaveCount(1);
    }

    private static WikiDatabaseProperty NewProperty(string type) => new()
    {
        Id = Guid.NewGuid(),
        WikiDatabaseId = Guid.NewGuid(),
        Name = "Property",
        Type = type
    };

    private static WikiDatabaseRow RowWithText(Guid propertyId, string value)
    {
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetText(values, propertyId, value);
        return new WikiDatabaseRow { Id = Guid.NewGuid(), PropertyValuesJson = WikiPropertyValues.Serialize(values) };
    }

    private static WikiDatabaseRow RowWithNumber(Guid propertyId, decimal value)
    {
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetNumber(values, propertyId, value);
        return new WikiDatabaseRow { Id = Guid.NewGuid(), PropertyValuesJson = WikiPropertyValues.Serialize(values) };
    }

    private static WikiDatabaseRow RowWithCheckbox(Guid propertyId, bool value)
    {
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetCheckbox(values, propertyId, value);
        return new WikiDatabaseRow { Id = Guid.NewGuid(), PropertyValuesJson = WikiPropertyValues.Serialize(values) };
    }
}
