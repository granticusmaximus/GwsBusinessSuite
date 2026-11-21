using FluentAssertions;
using GwsBusinessSuite.Application.Mail;

namespace GwsBusinessSuite.Tests;

// Mail rules (phase 6, 21-11-26): what a rule's conditions match, and what can be saved.
public sealed class MailRuleEngineTests
{
    private static readonly MailInboxItem Bill = new(
        new MailMessageSummary("m", null, new MailAddress("Georgia Power", "billing@georgiapower.example"), [new MailAddress("Grant", "grant@gwsapp.net")],
            "Your November bill", "Amount due $84.20", DateTimeOffset.UnixEpoch, true, false, false),
        "<m>", new Dictionary<string, string> { ["List-Id"] = "<billing.georgiapower.example>" });

    private static MailRuleView Rule(bool matchAll, params MailRuleCondition[] conditions) =>
        new(Guid.Empty, "Rule", true, matchAll, conditions, [new MailRuleAction(MailRuleActionKinds.Read)], 0);

    [Theory]
    [InlineData(MailRuleFields.From, MailRuleOps.Contains, "@georgiapower.example", true)]
    [InlineData(MailRuleFields.From, MailRuleOps.Contains, "georgia power", true)]
    [InlineData(MailRuleFields.From, MailRuleOps.Is, "georgiapower.example", false)]
    [InlineData(MailRuleFields.From, MailRuleOps.Is, "BILLING@georgiapower.example", true)]
    [InlineData(MailRuleFields.To, MailRuleOps.Contains, "grant@", true)]
    [InlineData(MailRuleFields.Subject, MailRuleOps.NotContains, "invoice", true)]
    [InlineData(MailRuleFields.Words, MailRuleOps.Contains, "amount due", true)]
    [InlineData(MailRuleFields.List, MailRuleOps.Contains, "billing.georgiapower", true)]
    [InlineData(MailRuleFields.Subject, MailRuleOps.Contains, "   ", false)]
    public void Matches_ShouldCompareWithoutCase(string field, string op, string value, bool expected) =>
        MailRuleEngine.Matches(Rule(true, new MailRuleCondition(field, op, value)), Bill).Should().Be(expected);

    [Fact]
    public void MatchAllOrAny_ShouldCombineConditions_AndActionsRunFlagsFirstMoveLast()
    {
        var conditions = new[] { new MailRuleCondition(MailRuleFields.From, MailRuleOps.Contains, "power"), new MailRuleCondition(MailRuleFields.Subject, MailRuleOps.Contains, "invoice") };
        MailRuleEngine.Matches(Rule(true, conditions), Bill).Should().BeFalse();
        MailRuleEngine.Matches(Rule(false, conditions), Bill).Should().BeTrue();
        MailRuleEngine.InRunOrder([new(MailRuleActionKinds.Folder, "folder:Bills"), new(MailRuleActionKinds.Tab, "inbox:updates"), new(MailRuleActionKinds.Read)])
            .Select(action => action.Kind).Should().Equal(MailRuleActionKinds.Read, MailRuleActionKinds.Tab, MailRuleActionKinds.Folder);
    }

    [Fact]
    public void Validate_ShouldRefuseRulesThatWouldNotRun()
    {
        var good = new MailRuleView(Guid.Empty, " Bills ", true, true, [new(MailRuleFields.From, MailRuleOps.Contains, " power ")],
            [new(MailRuleActionKinds.Folder, "folder:Bills"), new(MailRuleActionKinds.Folder, "folder:Bills")], 0);
        var clean = MailRuleEngine.Validate(good);
        clean.Name.Should().Be("Bills");
        clean.Conditions.Single().Value.Should().Be("power");
        clean.Actions.Should().ContainSingle("duplicate actions are kept once");

        foreach (var bad in new[]
        {
            good with { Name = " " },
            good with { Conditions = [new(MailRuleFields.From, MailRuleOps.Contains, " ")] },
            good with { Conditions = [new("body", MailRuleOps.Contains, "x")] },
            good with { Actions = [] },
            good with { Actions = [new(MailRuleActionKinds.Folder, "inbox")] },
            good with { Actions = [new(MailRuleActionKinds.Tab, "inbox:nonsense")] },
            good with { Actions = [new(MailRuleActionKinds.Folder, "folder:Bills"), new(MailRuleActionKinds.Archive)] },
            good with { Actions = [new("delete")] },
        })
            ((Action)(() => MailRuleEngine.Validate(bad))).Should().Throw<MailException>();
    }
}
