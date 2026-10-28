using FluentAssertions;
using GwsBusinessSuite.Application.Boards;

namespace GwsBusinessSuite.Tests;

// Boards K8: one-line cards, and Monte Carlo forecasts.
public sealed class KanbanQuickAndForecastTests
{
    private static readonly DateOnly Wednesday = new(2026, 10, 28);

    private static KanbanBoard Board(bool withFields = true) => new(
        Guid.NewGuid(), "Work", null,
        new KanbanFields(Guid.NewGuid(),
            PriorityPropertyId: withFields ? Guid.NewGuid() : null,
            LabelsPropertyId: withFields ? Guid.NewGuid() : null,
            DuePropertyId: withFields ? Guid.NewGuid() : null,
            AssigneePropertyId: withFields ? Guid.NewGuid() : null,
            EstimatePropertyId: withFields ? Guid.NewGuid() : null,
            KeyPropertyId: null),
        [],
        PriorityOptions: [new("u", "Urgent", "#b91c1c"), new("h", "High", "#d97706"), new("m", "Medium", "#2563eb"), new("l", "Low", "#78716c")],
        LabelOptions: [new("bug", "Bug", "#b91c1c"), new("feat", "Feature", "#2563eb")]);

    [Fact]
    public void Parse_ShouldTurnTokensIntoFields_AndKeepTheRestAsTheTitle()
    {
        var card = KanbanQuickParse.Parse("Fix login !high #bug @grant fri ~3", Board(), Wednesday, "me");

        card.Title.Should().Be("Fix login");
        card.PriorityOptionId.Should().Be("h");
        card.LabelOptionIds.Should().Equal("bug");
        card.People.Should().Equal("grant");
        card.Due.Should().Be(new DateOnly(2026, 10, 30));
        card.Estimate.Should().Be(3m);
        card.Tokens.Select(token => (token.Kind, token.Label)).Should().Equal(
            (KanbanQuickTokens.Priority, "High"), (KanbanQuickTokens.Label, "Bug"), (KanbanQuickTokens.Person, "grant"),
            (KanbanQuickTokens.Estimate, "3 points"), (KanbanQuickTokens.Due, "Fri 30-10-26"));
    }

    [Theory]
    [InlineData("Ship it !1", "u")]
    [InlineData("Ship it !hi", "h")]
    [InlineData("Ship it !LOW", "l")]
    [InlineData("Ship it !9", null)]
    [InlineData("Ship it !nope", null)]
    public void Parse_ShouldMatchPrioritiesByNumberOrTheStartOfTheirName(string text, string? expected)
    {
        var card = KanbanQuickParse.Parse(text, Board(), Wednesday, "me");
        card.PriorityOptionId.Should().Be(expected);
        card.Title.Should().Be(expected is null ? text : "Ship it");
    }

    [Fact]
    public void Parse_ShouldGuessNothing()
    {
        // An unknown label stays in the title; a day word only counts at the end; a one-word card
        // keeps its word; a board without the field leaves the token alone.
        KanbanQuickParse.Parse("Tidy #docs folder", Board(), Wednesday, "me").Title.Should().Be("Tidy #docs folder");
        var monday = KanbanQuickParse.Parse("Plan the monday meeting", Board(), Wednesday, "me");
        monday.Due.Should().BeNull();
        monday.Title.Should().Be("Plan the monday meeting");
        KanbanQuickParse.Parse("Friday", Board(), Wednesday, "me").Title.Should().Be("Friday");
        var bare = KanbanQuickParse.Parse("Fix login !high #bug @grant fri", Board(withFields: false), Wednesday, "me");
        bare.Title.Should().Be("Fix login !high #bug @grant fri");
        bare.Tokens.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Call back today", "2026-10-28")]
    [InlineData("Call back tomorrow", "2026-10-29")]
    [InlineData("Call back wed", "2026-10-28")]
    [InlineData("Call back tuesday", "2026-11-03")]
    [InlineData("Call back 05-11", "2026-11-05")]
    [InlineData("Call back 02-01", "2027-01-02")]
    [InlineData("Call back 01-10-26", "2026-10-01")]
    [InlineData("Call back 31-02", null)]
    public void Parse_ShouldReadDaysTheWayTheAppWritesThem(string text, string? expected)
    {
        var card = KanbanQuickParse.Parse(text, Board(), Wednesday, "me");
        card.Due.Should().Be(expected is null ? null : DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parse_ShouldTakeMeAsTheCurrentPerson_AndEachPersonOnce()
    {
        var card = KanbanQuickParse.Parse("Review @me @sam @Sam #feat #feature", Board(), Wednesday, "grant");
        card.People.Should().Equal("grant", "sam");
        card.LabelOptionIds.Should().Equal("feat");
        card.Title.Should().Be("Review");
    }

    [Fact]
    public void Forecast_ShouldBeExact_WhenTheBoardFinishesTheSameEveryDay()
    {
        var forecast = KanbanForecasting.Forecast([1, 1, 1, 1, 1, 1, 1], 5, Wednesday);

        forecast.CanForecast.Should().BeTrue();
        forecast.P50.Should().Be(Wednesday.AddDays(4));
        forecast.P85.Should().Be(Wednesday.AddDays(4));
        forecast.P95.Should().Be(Wednesday.AddDays(4));
        forecast.Curve.Select(point => point.Chance).Should().Equal(0, 0, 0, 0, 1);
        forecast.Curve[0].Day.Should().Be(Wednesday);
    }

    [Fact]
    public void Forecast_ShouldSpreadOut_WhenThePaceVaries_AndBeRepeatable()
    {
        int[] history = [0, 0, 3, 1, 0, 2, 0, 0, 4, 1, 0, 0, 2, 0];
        var forecast = KanbanForecasting.Forecast(history, 12, Wednesday);

        forecast.P50.Should().BeOnOrBefore(forecast.P85!.Value);
        forecast.P85.Should().BeOnOrBefore(forecast.P95!.Value);
        forecast.P95.Should().BeAfter(forecast.P50!.Value);
        forecast.Curve.Select(point => point.Chance).Should().BeInAscendingOrder();
        forecast.Curve[^1].Chance.Should().BeGreaterThanOrEqualTo(0.95);
        KanbanForecasting.Forecast(history, 12, Wednesday).Should().BeEquivalentTo(forecast);
    }

    [Fact]
    public void Forecast_ShouldSayNothing_WithoutHistory_AndBeDone_WithNothingLeft()
    {
        KanbanForecasting.Forecast([], 4, Wednesday).CanForecast.Should().BeFalse();
        KanbanForecasting.Forecast([0, 0, 0], 4, Wednesday).CanForecast.Should().BeFalse();
        var done = KanbanForecasting.Forecast([], 0, Wednesday);
        done.Remaining.Should().Be(0);
        done.P85.Should().Be(Wednesday);
    }
}
