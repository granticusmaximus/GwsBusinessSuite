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

    [Fact]
    public void BuildCalendarMonth_ShouldCreateSixWeekSundayFirstGrid()
    {
        var dateProperty = NewProperty(WikiDatabasePropertyTypes.Date);

        var calendar = WikiDatabaseViewLogic.BuildCalendarMonth([], dateProperty, new DateOnly(2026, 5, 19));

        calendar.Month.Should().Be(new DateOnly(2026, 5, 1));
        calendar.Days.Should().HaveCount(42);
        calendar.Days[0].Date.Should().Be(new DateOnly(2026, 4, 26));
        calendar.Days[0].IsCurrentMonth.Should().BeFalse();
        calendar.Days[5].Date.Should().Be(new DateOnly(2026, 5, 1));
        calendar.Days[5].IsCurrentMonth.Should().BeTrue();
        calendar.Days[^1].Date.Should().Be(new DateOnly(2026, 6, 6));
    }

    [Fact]
    public void BuildCalendarMonth_ShouldGroupRowsAndPreserveInputOrder()
    {
        var dateProperty = NewProperty(WikiDatabasePropertyTypes.Date);
        var later = RowWithDate(dateProperty.Id, new DateOnly(2026, 5, 15));
        later.SortOrder = 2;
        var earlier = RowWithDate(dateProperty.Id, new DateOnly(2026, 5, 15));
        earlier.SortOrder = 1;
        var undated = RowWithDate(dateProperty.Id, null);

        var calendar = WikiDatabaseViewLogic.BuildCalendarMonth(
            [later, undated, earlier],
            dateProperty,
            new DateOnly(2026, 5, 1));

        calendar.Days.Single(day => day.Date == new DateOnly(2026, 5, 15)).Rows
            .Should().BeEquivalentTo([later, earlier], options => options.WithStrictOrdering());
        calendar.UndatedRows.Should().ContainSingle().Which.Should().BeSameAs(undated);
    }

    [Fact]
    public void BuildCalendarMonth_NonDateProperty_ShouldThrow()
    {
        var textProperty = NewProperty(WikiDatabasePropertyTypes.Text);

        var action = () => WikiDatabaseViewLogic.BuildCalendarMonth([], textProperty, new DateOnly(2026, 5, 1));

        action.Should().Throw<ArgumentException>().WithMessage("Calendar views require a Date property.*");
    }

    [Fact]
    public void BuildTimeline_ShouldGroupRowsChronologicallyAndRetainUndated()
    {
        var dateProperty = NewProperty(WikiDatabasePropertyTypes.Date);
        var later = RowWithDate(dateProperty.Id, new DateOnly(2026, 7, 22));
        var earlier = RowWithDate(dateProperty.Id, new DateOnly(2026, 7, 21));
        var undated = RowWithDate(dateProperty.Id, null);

        var timeline = WikiDatabaseViewLogic.BuildTimeline([later, undated, earlier], dateProperty);

        timeline.Select(group => group.Date).Should().Equal(new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 22), null);
        timeline[^1].Rows.Should().ContainSingle().Which.Should().BeSameAs(undated);
    }

    [Fact]
    public void BuildChart_ShouldCountSelectOptionsAndNoValue()
    {
        var status = NewProperty(WikiDatabasePropertyTypes.Select);
        status.ConfigJson = WikiDatabasePropertyConfig.Serialize([
            new WikiDatabasePropertyOption("todo", "To Do", "#ccc"),
            new WikiDatabasePropertyOption("done", "Done", "#0f0")]);

        var chart = WikiDatabaseViewLogic.BuildChart([
            RowWithText(status.Id, "todo"), RowWithText(status.Id, "todo"),
            RowWithText(status.Id, "done"), RowWithText(status.Id, "")], status);

        chart.Select(bucket => (bucket.Label, bucket.Count)).Should().Equal(("To Do", 2), ("Done", 1), ("Empty", 1));
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

    private static WikiDatabaseRow RowWithDate(Guid propertyId, DateOnly? value)
    {
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        var localDate = value is null
            ? (DateTimeOffset?)null
            : new DateTimeOffset(value.Value.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(12)), DateTimeKind.Local));
        WikiPropertyValues.SetDate(values, propertyId, localDate);
        return new WikiDatabaseRow { Id = Guid.NewGuid(), PropertyValuesJson = WikiPropertyValues.Serialize(values) };
    }
}
