using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using System.Diagnostics;

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

    [Fact]
    public void GetBreadcrumbNodeIds_ShouldReturnRootToSelectedNode()
    {
        var root = Node();
        var child = Node(root.Id);
        var grandchild = Node(child.Id);
        var nodes = new[] { grandchild, root, child };

        SentinelTreeNavigation.GetBreadcrumbNodeIds(grandchild.Id, nodes)
            .Should().Equal(root.Id, child.Id, grandchild.Id);
    }

    [Fact]
    public void GetParentNodeId_ShouldReturnOnlyAParentThatExistsInTheWorkspace()
    {
        var root = Node();
        var child = Node(root.Id);
        var orphan = Node(Guid.NewGuid());
        var nodes = new[] { child, orphan, root };

        SentinelTreeNavigation.GetParentNodeId(child.Id, nodes).Should().Be(root.Id);
        SentinelTreeNavigation.GetParentNodeId(root.Id, nodes).Should().BeNull();
        SentinelTreeNavigation.GetParentNodeId(orphan.Id, nodes).Should().BeNull();
        SentinelTreeNavigation.GetParentNodeId(Guid.NewGuid(), nodes).Should().BeNull();
    }

    [Fact]
    public void GetBreadcrumbNodeIds_ShouldBeEmptyForUnknownNodeAndStopAtCycles()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var cyclicNodes = new[]
        {
            new SentinelTreeNavigationNode(firstId, secondId, 0),
            new SentinelTreeNavigationNode(secondId, firstId, 0)
        };

        SentinelTreeNavigation.GetBreadcrumbNodeIds(Guid.NewGuid(), cyclicNodes)
            .Should().BeEmpty();
        SentinelTreeNavigation.GetBreadcrumbNodeIds(firstId, cyclicNodes)
            .Should().Equal(secondId, firstId);
    }

    [Fact]
    public void NavigationProjection_ShouldStayWithinLargeWorkspaceBudget()
    {
        const int rootCount = 200;
        const int childrenPerRoot = 100;
        var nodes = new List<SentinelTreeNavigationNode>(rootCount * (childrenPerRoot + 1));
        var expanded = new HashSet<Guid>();
        for (var rootIndex = 0; rootIndex < rootCount; rootIndex++)
        {
            var root = Node(sortOrder: rootIndex);
            nodes.Add(root);
            expanded.Add(root.Id);
            for (var childIndex = 0; childIndex < childrenPerRoot; childIndex++)
            {
                nodes.Add(Node(root.Id, childIndex));
            }
        }

        var timer = Stopwatch.StartNew();
        var visible = SentinelTreeNavigation.GetVisibleNodeIds(nodes, expanded);
        timer.Stop();

        visible.Should().HaveCount(nodes.Count);
        timer.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "20,200-page navigation must remain interactive even on slower CI runners");
    }

    private static SentinelTreeNavigationNode Node(
        Guid? parentId = null,
        int sortOrder = 0) =>
        new(Guid.NewGuid(), parentId, sortOrder);
}
