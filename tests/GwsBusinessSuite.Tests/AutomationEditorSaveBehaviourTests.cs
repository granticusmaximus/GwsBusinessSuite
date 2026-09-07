using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// A5 removed two round trips from every canvas gesture. These pin the parts of that which are
// easy to reintroduce by accident, since neither shows up as a failure - only as the editor
// feeling heavy again, or a node visibly jumping back after a drag.
public sealed class AutomationEditorSaveBehaviourTests
{
    private static string EditorPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/AutomationEditor.razor"));

    [Fact]
    public void GraphEdits_ShouldNotRefetchSharingAndPermissions()
    {
        // Access data changes only when someone edits sharing, but it was being refetched on
        // every node move, connection and parameter edit - a second round trip per gesture.
        var source = File.ReadAllText(EditorPath);

        source.Should().Contain("private Task Reload() => ReloadAsync(includeAccess: false);",
            "the default reload path must not pull access data");
        source.Should().Contain("ReloadAsync(includeAccess: true)",
            "initial load and the sharing operations still need it");

        // Exactly three callers should ask for access: initial load, creating a share, removing
        // a permission. More than that means a graph edit started paying for it again.
        var withAccess = System.Text.RegularExpressions.Regex
            .Matches(source, @"ReloadAsync\(includeAccess: true\)").Count;
        withAccess.Should().Be(3);
    }

    [Fact]
    public void MovingANode_ShouldNotRefetchTheGraph()
    {
        // The canvas has already moved the node, so refetching to re-render it in the same place
        // is pure cost - and the re-render is visible.
        var source = File.ReadAllText(EditorPath);
        var move = source[source.IndexOf("public async Task UpdateNodePosition", StringComparison.Ordinal)..];
        // End at the method's own closing brace rather than a later landmark: the JSInvokables
        // between here and SelectNode do reload, and would make this pass or fail for the
        // wrong reason.
        move = move[..(move.IndexOf("\n    }", StringComparison.Ordinal) + 6)];

        move.Should().Contain("MoveNodeAsync");
        move.Should().NotContain("await Reload()",
            "a moved node is already in the right place on screen");
        move.Should().Contain("_movedPositions[id]",
            "the position overlay is what stops a re-render snapping the node back");
    }

    [Fact]
    public void EveryPositionRead_ShouldGoThroughTheOverlay()
    {
        // AutomationWorkflowView is a class with init-only members, so a moved node's position
        // cannot be written back into it. Anything reading PositionX/Y directly would render
        // from the stale server value - which is how a dragged node snaps back, and how wires
        // detach from the node they belong to.
        var source = File.ReadAllText(EditorPath);
        var offenders = source.Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .Where(entry => entry.line.Contains(".PositionX", StringComparison.Ordinal)
                         || entry.line.Contains(".PositionY", StringComparison.Ordinal))
            .Where(entry => !entry.line.Contains("NodePosition", StringComparison.Ordinal))
            // The overlay helper itself is where the raw values are legitimately read.
            .Where(entry => !entry.line.Contains("_movedPositions", StringComparison.Ordinal))
            // The editor model being sent to the server legitimately carries plain values.
            .Where(entry => !entry.line.Contains("PositionX =", StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "every render-time position read must consult the overlay: " +
            string.Join(" | ", offenders.Select(o => $"line {o.number}")));
    }

    [Fact]
    public void RefetchingTheGraph_ShouldDiscardTheOverlay()
    {
        // Keeping stale overrides after a real fetch would make the editor ignore newer server
        // positions - including another editor's changes.
        var source = File.ReadAllText(EditorPath);
        var reload = source[source.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal)..];
        reload = reload[..reload.IndexOf("catch (Exception ex)", StringComparison.Ordinal)];

        reload.Should().Contain("_movedPositions.Clear()");
    }
}
