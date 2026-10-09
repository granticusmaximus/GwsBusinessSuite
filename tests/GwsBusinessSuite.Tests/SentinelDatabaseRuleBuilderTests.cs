using System.Text.Json.Nodes;
using FluentAssertions;
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
}
