using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelTreeNavigationTests
{
    [Fact]
    public void GetVisibleNodeIds_ShouldDefaultTopLevelBranchesToCollapsed()
    {
        var parent = Node(sortOrder: 1);
        var child = Node(parent.Id, sortOrder: 1);
        var grandchild = Node(child.Id, sortOrder: 1);
        var secondRoot = Node(sortOrder: 2);
        var nodes = new[] { grandchild, secondRoot, child, parent };

        var visible = SentinelTreeNavigation.GetVisibleNodeIds(nodes, new HashSet<Guid>());

        visible.Should().Equal(parent.Id, secondRoot.Id);
    }

    [Fact]
    public void GetVisibleNodeIds_ShouldExpandOnlyRequestedBranches()
    {
        var firstRoot = Node(sortOrder: 1);
        var firstChild = Node(firstRoot.Id, sortOrder: 1);
        var secondRoot = Node(sortOrder: 2);
        var secondChild = Node(secondRoot.Id, sortOrder: 1);
        var nodes = new[] { firstRoot, firstChild, secondRoot, secondChild };

        var visible = SentinelTreeNavigation.GetVisibleNodeIds(
            nodes,
            new HashSet<Guid> { secondRoot.Id });

        visible.Should().Equal(firstRoot.Id, secondRoot.Id, secondChild.Id);
    }

    [Fact]
    public void NavigationHelpers_ShouldIdentifyBranchesAndRevealSelectedPagePath()
    {
        var root = Node();
        var child = Node(root.Id);
        var grandchild = Node(child.Id);
        var leaf = Node();
        var nodes = new[] { root, child, grandchild, leaf };

        SentinelTreeNavigation.GetBranchNodeIds(nodes)
            .Should().BeEquivalentTo(new[] { root.Id, child.Id });
        SentinelTreeNavigation.GetAncestorNodeIds(grandchild.Id, nodes)
            .Should().Equal(child.Id, root.Id);
    }

    private static SentinelTreeNavigationNode Node(
        Guid? parentId = null,
        int sortOrder = 0) =>
        new(Guid.NewGuid(), parentId, sortOrder);
}
