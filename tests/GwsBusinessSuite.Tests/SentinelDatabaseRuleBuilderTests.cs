using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelDatabaseRuleBuilderTests
{
    private static readonly WikiDatabaseProperty Title = new() { Id = Guid.NewGuid(), Name = "Name", Type = WikiDatabasePropertyTypes.Title };
    private static readonly WikiDatabaseProperty Owner = new() { Id = Guid.NewGuid(), Name = "Owner", Type = WikiDatabasePropertyTypes.Person };
    private static readonly WikiDatabaseProperty Notes = new() { Id = Guid.NewGuid(), Name = "Notes", Type = WikiDatabasePropertyTypes.Text };
    private static readonly WikiDatabaseProperty Due = new() { Id = Guid.NewGuid(), Name = "Due", Type = WikiDatabasePropertyTypes.Date };
    private static readonly WikiDatabase Database = new() { Id = Guid.NewGuid(), Title = "Tasks", Properties = [Title, Owner, Notes, Due] };

    [Fact]
    public void Build_RowAdded_ShouldGateOnIsNew_ThenEmail()
    {
        var rule = new SentinelDatabaseRule(Database.Id, SentinelDatabaseRuleKinds.WhenRowAdded, null, null,
            SentinelDatabaseRuleKinds.ThenEmail, EmailTo: "grant@example.com");

        var graph = SentinelDatabaseRuleBuilder.Build(rule, Database);

        graph.Nodes.Select(node => node.TypeKey).Should().Equal("database.rowChangedTrigger", "core.if", "core.notify");
        graph.Connections.Select(connection => connection.SourceOutput).Should().Equal("main", "true");
        graph.Description.Should().StartWith(SentinelDatabaseRuleBuilder.MarkerFor(Database.Id));
        SentinelDatabaseRuleBuilder.SummaryFromDescription(graph.Description, Database.Id)
            .Should().Be("When a row is added, email grant@example.com");
        SentinelDatabaseRuleBuilder.SummaryFromDescription(graph.Description, Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Build_SetDateToNowOnlyIfEmpty_ShouldCheckEmptyThenStampTheTime()
    {
        var rule = new SentinelDatabaseRule(Database.Id, SentinelDatabaseRuleKinds.WhenPropertyEquals, Notes.Id, "ship",
            SentinelDatabaseRuleKinds.ThenSetProperty, Due.Id, SetToNow: true, OnlyIfEmpty: true);

        var graph = SentinelDatabaseRuleBuilder.Build(rule, Database);

        graph.Nodes.Select(node => node.TypeKey).Should().Equal(
            "database.rowChangedTrigger", "core.if", "core.dateTime", "database.setRowProperty");
        graph.Connections.Select(connection => connection.SourceOutput).Should().Equal("main", "false", "main");
        var condition = JsonNode.Parse(graph.Nodes[0].ParametersJson)!["conditions"]![0]!;
        condition["propertyId"]!.GetValue<string>().Should().Be(Notes.Id.ToString());
        condition["value"]!.GetValue<string>().Should().Be("ship");
        JsonNode.Parse(graph.Nodes[3].ParametersJson)!["value"]!.GetValue<string>().Should().Be("{{ $json.timestamp.iso }}");
    }

    [Theory]
    [InlineData(SentinelDatabaseRuleKinds.ThenEmail, "not-an-email", "Enter the email address to notify.")]
    [InlineData(SentinelDatabaseRuleKinds.ThenRunWorkflow, null, "Choose the workflow to run.")]
    public void Validate_ShouldExplainWhatIsMissing(string then, string? email, string expected)
    {
        var rule = new SentinelDatabaseRule(Database.Id, SentinelDatabaseRuleKinds.WhenRowAdded, null, null, then, EmailTo: email);

        SentinelDatabaseRuleBuilder.Validate(rule, Database).Should().Be(expected);
    }

    [Fact]
    public void Validate_ShouldRefuseTypesTheEngineCannotWatchOrSet()
    {
        var watchPerson = new SentinelDatabaseRule(Database.Id, SentinelDatabaseRuleKinds.WhenPropertyEquals, Owner.Id, "grant",
            SentinelDatabaseRuleKinds.ThenEmail, EmailTo: "a@b.c");
        var nowOnText = new SentinelDatabaseRule(Database.Id, SentinelDatabaseRuleKinds.WhenRowAdded, null, null,
            SentinelDatabaseRuleKinds.ThenSetProperty, Notes.Id, SetToNow: true);

        SentinelDatabaseRuleBuilder.Validate(watchPerson, Database).Should().Contain("can't watch");
        SentinelDatabaseRuleBuilder.Validate(nowOnText, Database).Should().Contain("Date");
    }

    // Boards K6: "when a card enters a column".
    private static readonly WikiDatabaseProperty Status = new()
    {
        Id = Guid.NewGuid(), Name = "Status", Type = WikiDatabasePropertyTypes.Status,
        ConfigJson = WikiDatabasePropertyConfig.Serialize([new WikiDatabasePropertyOption("todo", "To do", "gray"), new WikiDatabasePropertyOption("done", "Done", "green")])
    };
    private static readonly WikiDatabaseProperty Blocked = new() { Id = Guid.NewGuid(), Name = "Blocked", Type = WikiDatabasePropertyTypes.Checkbox };
    private static readonly WikiDatabase Board = new() { Id = Guid.NewGuid(), Title = "Work", Properties = [Title, Status, Owner, Notes, Blocked] };

    [Theory]
    [InlineData("false", "When a card enters Done, untick Blocked")]
    [InlineData("true", "When a card enters Done, tick Blocked")]
    public void Describe_ShouldSayTickOrUntick_ForACheckbox(string value, string expected) =>
        SentinelDatabaseRuleBuilder.Describe(new SentinelDatabaseRule(Board.Id, SentinelDatabaseRuleKinds.WhenPropertyBecomes, Status.Id, "done",
            SentinelDatabaseRuleKinds.ThenSetProperty, Blocked.Id, value, CardsOnly: true), Board).Should().Be(expected);

    [Fact]
    public void Build_CardEntersColumn_ShouldTriggerOnChangedTo_SkipSubItems_ThenCompleteThem()
    {
        var rule = new SentinelDatabaseRule(Board.Id, SentinelDatabaseRuleKinds.WhenPropertyBecomes, Status.Id, "done",
            SentinelDatabaseRuleKinds.ThenCompleteSubItems, CardsOnly: true);

        var graph = SentinelDatabaseRuleBuilder.Build(rule, Board);

        graph.Nodes.Select(node => node.TypeKey).Should().Equal("database.rowChangedTrigger", "core.if", "database.completeSubItems");
        graph.Connections.Select(connection => connection.SourceOutput).Should().Equal("main", "false");
        var condition = JsonNode.Parse(graph.Nodes[0].ParametersJson)!["conditions"]![0]!;
        condition["operator"]!.GetValue<string>().Should().Be("changedTo");
        condition["value"]!.GetValue<string>().Should().Be("done");
        JsonNode.Parse(graph.Nodes[1].ParametersJson)!["left"]!.GetValue<string>().Should().Be("{{ $json.parentRowId }}");
        SentinelDatabaseRuleBuilder.Describe(rule, Board).Should().Be("When a card enters Done, complete its sub-items");
        SentinelDatabaseRuleBuilder.Describe(rule with { CardsOnly = false }, Board).Should().Be("When Status becomes Done, complete its sub-items");
        SentinelDatabaseRuleBuilder.Validate(rule, Board).Should().BeNull();
    }

    [Fact]
    public void Validate_CompleteSubItems_ShouldNeedAStatusProperty_AndPeopleCanBeSet()
    {
        var rule = new SentinelDatabaseRule(Database.Id, SentinelDatabaseRuleKinds.WhenRowAdded, null, null, SentinelDatabaseRuleKinds.ThenCompleteSubItems);
        SentinelDatabaseRuleBuilder.Validate(rule, Database).Should().Contain("Status");

        var assign = new SentinelDatabaseRule(Board.Id, SentinelDatabaseRuleKinds.WhenPropertyBecomes, Status.Id, "done",
            SentinelDatabaseRuleKinds.ThenSetProperty, Owner.Id, "grant", CardsOnly: true);
        SentinelDatabaseRuleBuilder.Validate(assign, Board).Should().BeNull();
        SentinelDatabaseRuleBuilder.Describe(assign, Board).Should().Be("When a card enters Done, set Owner to grant");
    }

    [Theory]
    [InlineData("{\"isNew\":false,\"values\":{\"S\":\"done\"},\"previousValues\":{\"S\":\"todo\"}}", true)]
    [InlineData("{\"isNew\":false,\"values\":{\"S\":\"done\"},\"previousValues\":{\"S\":\"done\"}}", false)]
    [InlineData("{\"isNew\":true,\"values\":{\"S\":\"done\"}}", true)]
    [InlineData("{\"isNew\":false,\"values\":{\"S\":\"done\"}}", false)]
    [InlineData("{\"isNew\":false,\"values\":{\"S\":\"todo\"},\"previousValues\":{\"S\":\"done\"}}", false)]
    public void ChangedTo_ShouldMatchOnlyWhenTheValueHasJustBecomeIt(string payloadTemplate, bool expected)
    {
        // Entering (from another value, or a new row made in the column) matches; staying, or a
        // payload with no previous values to prove a change, doesn't.
        var propertyId = Guid.NewGuid().ToString();
        var parameters = new JsonObject
        {
            ["conditions"] = new JsonArray(new JsonObject { ["propertyId"] = propertyId, ["operator"] = "changedTo", ["value"] = "done" })
        }.ToJsonString();

        DatabaseTriggerConditions.Matches(parameters, payloadTemplate.Replace("\"S\"", $"\"{propertyId}\"")).Should().Be(expected);
        var validate = () => DatabaseTriggerConditions.ValidateConditions(parameters, "Trigger");
        validate.Should().NotThrow();
    }
}
