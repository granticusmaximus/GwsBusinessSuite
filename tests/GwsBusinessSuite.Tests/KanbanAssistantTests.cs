using FluentAssertions;
using GwsBusinessSuite.Application.Boards;

namespace GwsBusinessSuite.Tests;

// Boards K8b: the facts a standup and "what's stuck" are written from, and reading a model's steps.
public sealed class KanbanAssistantTests
{
    [Theory]
    [InlineData("2026-10-28T09:00:00", "2026-10-27")] // Wednesday: Tuesday
    [InlineData("2026-10-26T09:00:00", "2026-10-23")] // Monday: Friday
    [InlineData("2026-10-25T09:00:00", "2026-10-23")] // Sunday: Friday
    public void StandupSince_ShouldBeTheStartOfTheLastWorkingDay(string now, string expected)
    {
        var local = DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture);
        var since = KanbanAssistant.StandupSince(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)));
        DateOnly.FromDateTime(since.LocalDateTime).Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        since.LocalDateTime.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Facts_ShouldSayWhatMovedFinishedAndIsStuck_InPlainWords()
    {
        var at = DateTimeOffset.Now;
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var brief = new KanbanBrief(at.AddDays(-1),
            [
                new KanbanBriefMove(a, "PRO-1", "Press kit", "In review", "Done", "grant", at),
                new KanbanBriefMove(b, "PRO-2", "Hero section", "Ready", "In progress", "sam", at),
                new KanbanBriefMove(b, "PRO-2", "Hero section", "In progress", "In review", "sam", at.AddMinutes(5))
            ],
            [
                new KanbanBriefCard(c, "PRO-3", "Checkout", "In progress", 11.4, true, "waiting on Stripe", ["sam"], true),
                new KanbanBriefCard(b, null, "Docs", "In review", 0.3, false, null, [], false)
            ],
            ["In progress"], 8.6);

        KanbanAssistant.StandupFacts(brief, ["Done"]).Should().Equal(
            "Done: PRO-1 \"Press kit\" reached Done (grant).",
            "Moved: PRO-2 \"Hero section\" from Ready to In review (sam).",
            "Blocked: PRO-3 \"Checkout\" in In progress - waiting on Stripe.");
        KanbanAssistant.StuckFacts(brief).Should().Equal(
            "PRO-3 \"Checkout\": blocked: waiting on Stripe; 11 days in In progress, past the 8 days 85% of cards finish within; with sam.",
            "\"Docs\": under a day in In review; nobody assigned.",
            "In progress is over its WIP limit.");
        KanbanAssistant.StandupFacts(brief with { Moves = [], Stuck = [] }, ["Done"]).Should().Equal("Nothing moved since the last working day.");
        KanbanAssistant.StuckFacts(brief with { Stuck = [], OverLimit = [] }).Single().Should().StartWith("Nothing looks stuck");
        KanbanAssistant.FactsPrompt("Launch", ["One.", "Two."]).Should().Be("Board: Launch\nFacts:\n- One.\n- Two.");
    }

    [Theory]
    [InlineData("{\"steps\":[\"Draft the copy\",\"Review it\",\"Draft the copy\"]}", new[] { "Draft the copy", "Review it" })]
    [InlineData("[\"One\", \"Two\"]", new[] { "One", "Two" })]
    [InlineData("{\"tasks\":[\"Only array\"]}", new[] { "Only array" })]
    [InlineData("- Plan it\n2. Build it\n* Ship it", new[] { "Plan it", "Build it", "Ship it" })]
    [InlineData("", new string[0])]
    [InlineData("{\"answer\":\"no list\"}", new string[0])]
    public void ParseSteps_ShouldReadWhatSmallModelsActuallySend(string output, string[] expected) =>
        KanbanAssistant.ParseSteps(output).Should().Equal(expected);

    [Fact]
    public void ParseSteps_ShouldKeepItShort()
    {
        var many = "{\"steps\":[" + string.Join(",", Enumerable.Range(1, 15).Select(i => $"\"Step {i}\"")) + ",\"" + new string('x', 200) + "\"]}";
        var steps = KanbanAssistant.ParseSteps(many);
        steps.Should().HaveCount(10);
        KanbanAssistant.ParseSteps("[\"" + new string('y', 200) + "\"]").Single().Should().HaveLength(118).And.EndWith("…");
        KanbanAssistant.BreakdownPrompt("Ship it", new string('p', 3000)).Length.Should().BeLessThan(2100);
    }
}
