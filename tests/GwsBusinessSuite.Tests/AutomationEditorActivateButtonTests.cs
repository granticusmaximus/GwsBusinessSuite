using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Regression guard for a real UX finding: Publish and Active used to be two separate controls
// that had to be sequenced correctly, and the Active toggle sat disabled with no explanation
// whenever CurrentVersion was 0. Activate now publishes on the way to going live when a
// version doesn't already exist, so the toggle is never disabled for that reason, and a failed
// validation selects the offending node instead of only naming it in an error banner.
public sealed class AutomationEditorActivateButtonTests
{
    private static string Source => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/AutomationEditor.razor")));

    [Fact]
    public void ActivateButton_ShouldNotBeDisabledJustBecauseNoVersionHasBeenPublishedYet()
    {
        var source = Source;
        var button = source[source.IndexOf("@onclick=\"Activate\"", StringComparison.Ordinal)..];
        button = button[..button.IndexOf("</button>", StringComparison.Ordinal)];

        button.Should().NotContain("CurrentVersion == 0",
            "the old Active/Inactive toggle used to sit disabled for this reason with no visible explanation");
    }

    [Fact]
    public void Activate_ShouldPublishBeforeGoingLive_WhenNotAlreadyActive()
    {
        var source = Source;
        var method = source[source.IndexOf("private async Task Activate()", StringComparison.Ordinal)..];
        method = method[..(method.IndexOf("\n    });", StringComparison.Ordinal) + 5)];

        method.Should().Contain("PublishAsync(WorkflowId,", "activating from a fresh workflow must not require a separate manual Publish first");
        method.Should().Contain("SetActiveAsync(WorkflowId, true)");
        method.Should().Contain("SelectOffendingNode(validation.Errors)",
            "a failed validation should select the offending node, not just name it in an error banner");
    }

    [Fact]
    public void SelectOffendingNode_ShouldMatchByTheQuotedNameInTheErrorMessage()
    {
        var source = Source;
        source.Should().Contain("private void SelectOffendingNode(IReadOnlyList<string> errors)");
        var method = source[source.IndexOf("private void SelectOffendingNode", StringComparison.Ordinal)..];
        method = method[..(method.IndexOf("\n    }", StringComparison.Ordinal) + 6)];

        method.Should().Contain("SelectNode(node.Id)");
    }
}
