using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelLiveEditingTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static WikiBlock P(Guid id, string text) =>
        new(id, WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());

    private static string Doc(params WikiBlock[] blocks) => WikiBlockJson.Serialize(blocks);

    private static IEnumerable<string> Texts(string json) => WikiBlockJson.ParseBlocks(json).Select(block => block.PlainText);

    [Fact]
    public void NoLocalChanges_ShouldTakeTheRemoteVersion_ReplacingOnlyWhatChanged()
    {
        var baseJson = Doc(P(A, "one"), P(B, "two"));
        var remote = Doc(P(A, "one"), P(B, "two!"), P(C, "three"));

        var plan = SentinelLiveEditing.PlanRemoteApply(baseJson, baseJson, remote);

        plan.Apply.Should().BeTrue();
        plan.NoLocalChanges.Should().BeTrue();
        Texts(plan.MergedJson).Should().Equal("one", "two!", "three");
        plan.ChangedBlockIds.Should().BeEquivalentTo([B, C]);
    }

    [Fact]
    public void EditsToDifferentBlocks_ShouldBothSurvive_AndOnlyTheirBlockIsReplacedHere()
    {
        var baseJson = Doc(P(A, "one"), P(B, "two"));
        var local = Doc(P(A, "one - edited here"), P(B, "two"));
        var remote = Doc(P(A, "one"), P(B, "two - edited there"));

        var plan = SentinelLiveEditing.PlanRemoteApply(baseJson, local, remote);

        plan.Apply.Should().BeTrue();
        plan.NoLocalChanges.Should().BeFalse();
        Texts(plan.MergedJson).Should().Equal("one - edited here", "two - edited there");
        plan.ChangedBlockIds.Should().Equal(B);
    }

    [Fact]
    public void TheSameBlockChangedOnBothSides_ShouldNotBeApplied()
    {
        var baseJson = Doc(P(A, "one"));

        var plan = SentinelLiveEditing.PlanRemoteApply(baseJson, Doc(P(A, "mine")), Doc(P(A, "theirs")));

        plan.Apply.Should().BeFalse();
    }

    [Fact]
    public void ARemoteDeletion_ShouldRemoveTheBlock_WhileKeepingLocalEditsElsewhere()
    {
        var baseJson = Doc(P(A, "one"), P(B, "two"), P(C, "three"));
        var local = Doc(P(A, "one"), P(B, "two"), P(C, "three, edited"));
        var remote = Doc(P(A, "one"), P(C, "three"));

        var plan = SentinelLiveEditing.PlanRemoteApply(baseJson, local, remote);

        plan.Apply.Should().BeTrue();
        Texts(plan.MergedJson).Should().Equal("one", "three, edited");
        plan.ChangedBlockIds.Should().BeEmpty();
    }
}
