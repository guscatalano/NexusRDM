using System;
using System.Collections.Generic;
using System.Linq;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;
using NexusRDM.Views;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests for the drag-persist helpers <see cref="ConnectionsPane"/>
/// uses to reconcile the post-drop tree against the DB. The handler
/// itself is event-driven UI code; the *logic* — walking the tree
/// to figure out each node's effective parent group, and rejecting
/// cyclic group moves — is pure and lives in the static helpers
/// covered here.
/// </summary>
public sealed class TreeReorderTests
{
    // ── EnumerateWithParent ──────────────────────────────────────────────

    [Fact]
    public void EnumerateWithParent_Empty_YieldsNothing()
    {
        var pairs = ConnectionsPane.EnumerateWithParent(
            Array.Empty<ConnectionTreeNode>(), parentGroupId: null).ToList();
        Assert.Empty(pairs);
    }

    [Fact]
    public void EnumerateWithParent_FlatConnectionsAtRoot_AllParentNull()
    {
        var c1 = ConnNode("c1");
        var c2 = ConnNode("c2");
        var roots = new List<ConnectionTreeNode> { c1, c2 };

        var pairs = ConnectionsPane.EnumerateWithParent(roots, null).ToList();

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Null(p.ParentGroupId));
    }

    [Fact]
    public void EnumerateWithParent_GroupWithChildren_ChildrenCarryGroupId()
    {
        var groupId = Guid.NewGuid();
        var group   = GroupNode(groupId, "g");
        var c1      = ConnNode("c1");
        var c2      = ConnNode("c2");
        group.Children.Add(c1);
        group.Children.Add(c2);

        var pairs = ConnectionsPane.EnumerateWithParent(
            new List<ConnectionTreeNode> { group }, null).ToList();

        // group itself: parent null. Children: parent = group's id.
        Assert.Collection(pairs,
            p => { Assert.Same(group, p.Node); Assert.Null(p.ParentGroupId); },
            p => { Assert.Same(c1, p.Node);    Assert.Equal(groupId, p.ParentGroupId); },
            p => { Assert.Same(c2, p.Node);    Assert.Equal(groupId, p.ParentGroupId); });
    }

    [Fact]
    public void EnumerateWithParent_NestedGroups_DeepestParentIdFlowsDown()
    {
        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();

        var outer = GroupNode(outerId, "outer");
        var inner = GroupNode(innerId, "inner");
        var leaf  = ConnNode("leaf");

        outer.Children.Add(inner);
        inner.Children.Add(leaf);

        var pairs = ConnectionsPane.EnumerateWithParent(
            new List<ConnectionTreeNode> { outer }, null).ToList();

        Assert.Equal(3, pairs.Count);
        Assert.Null(pairs[0].ParentGroupId);              // outer at root
        Assert.Equal(outerId, pairs[1].ParentGroupId);    // inner under outer
        Assert.Equal(innerId, pairs[2].ParentGroupId);    // leaf under inner
    }

    [Fact]
    public void EnumerateWithParent_SeedParentApplies_ToImmediateChildren()
    {
        // Caller can pass an initial parent — useful for partial walks.
        var seed = Guid.NewGuid();
        var c1   = ConnNode("c1");

        var pair = ConnectionsPane.EnumerateWithParent(
            new[] { c1 }, parentGroupId: seed).Single();

        Assert.Equal(seed, pair.ParentGroupId);
    }

    // ── IsDescendantOf ───────────────────────────────────────────────────

    [Fact]
    public void IsDescendantOf_SelfReturnsTrue()
    {
        // Moving a group "into itself" must be rejected. ContainsGroup
        // treats the ancestor as its own descendant for this guard.
        var id = Guid.NewGuid();
        var g  = GroupNode(id, "g");
        var roots = new[] { g };

        Assert.True(ConnectionsPane.IsDescendantOf(id, id, roots));
    }

    [Fact]
    public void IsDescendantOf_DirectChildIsDescendant()
    {
        var parentId = Guid.NewGuid();
        var childId  = Guid.NewGuid();

        var parent = GroupNode(parentId, "p");
        var child  = GroupNode(childId,  "c");
        parent.Children.Add(child);

        Assert.True(ConnectionsPane.IsDescendantOf(childId, parentId, new[] { parent }));
    }

    [Fact]
    public void IsDescendantOf_DeeplyNestedDescendant_ReturnsTrue()
    {
        var aId = Guid.NewGuid(); var bId = Guid.NewGuid(); var cId = Guid.NewGuid();
        var a = GroupNode(aId, "a"); var b = GroupNode(bId, "b"); var c = GroupNode(cId, "c");
        a.Children.Add(b);
        b.Children.Add(c);

        Assert.True(ConnectionsPane.IsDescendantOf(cId, aId, new[] { a }));
    }

    [Fact]
    public void IsDescendantOf_UnrelatedGroup_ReturnsFalse()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var a = GroupNode(aId, "a");
        var b = GroupNode(bId, "b");

        // Two siblings at the root — neither is in the other's subtree.
        Assert.False(ConnectionsPane.IsDescendantOf(bId, aId, new[] { a, b }));
        Assert.False(ConnectionsPane.IsDescendantOf(aId, bId, new[] { a, b }));
    }

    [Fact]
    public void IsDescendantOf_AncestorMissingFromTree_ReturnsFalse()
    {
        // If the ancestor doesn't exist anywhere in the supplied
        // roots, no descendant relationship can be established.
        var present = Guid.NewGuid();
        var phantom = Guid.NewGuid();
        var node = GroupNode(present, "p");

        Assert.False(ConnectionsPane.IsDescendantOf(present, phantom, new[] { node }));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ConnectionTreeNode GroupNode(Guid id, string name) =>
        new ConnectionTreeNode(new Group { Id = id, Name = name });

    private static ConnectionTreeNode ConnNode(string name) =>
        new ConnectionTreeNode(new ConnectionProfile
        {
            Id          = Guid.NewGuid(),
            DisplayName = name,
            Host        = name,
            Port        = 22,
            Protocol    = ConnectionProtocol.Ssh,
        });
}
