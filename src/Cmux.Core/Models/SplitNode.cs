namespace Cmux.Core.Models;

public enum SplitDirection
{
    Horizontal,
    Vertical,
}

public class SplitNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public bool IsLeaf { get; set; } = true;
    public SplitDirection Direction { get; set; } = SplitDirection.Vertical;
    public SplitNode? First { get; set; }
    public SplitNode? Second { get; set; }
    public double SplitRatio { get; set; } = 0.5;
    public string? PaneId { get; set; }

    public static SplitNode CreateLeaf(string? paneId = null)
    {
        return new SplitNode
        {
            IsLeaf = true,
            PaneId = paneId ?? Guid.NewGuid().ToString(),
        };
    }

    /// <summary>
    /// Splits this leaf node into a container with two children.
    /// Returns the new second child (the newly created pane).
    /// </summary>
    public SplitNode Split(SplitDirection direction)
    {
        if (!IsLeaf)
            throw new InvalidOperationException("Cannot split a non-leaf node.");

        var firstChild = new SplitNode
        {
            IsLeaf = true,
            PaneId = PaneId,
        };

        var secondChild = CreateLeaf();

        IsLeaf = false;
        Direction = direction;
        First = firstChild;
        Second = secondChild;
        PaneId = null;
        SplitRatio = 0.5;

        return secondChild;
    }

    /// <summary>
    /// Recursively finds the node containing the given pane ID.
    /// </summary>
    public SplitNode? FindNode(string paneId)
    {
        if (IsLeaf)
            return PaneId == paneId ? this : null;

        return First?.FindNode(paneId) ?? Second?.FindNode(paneId);
    }

    /// <summary>
    /// Finds the parent of a node with the given ID.
    /// </summary>
    public SplitNode? FindParent(string nodeId)
    {
        if (IsLeaf) return null;

        if (First?.Id == nodeId || Second?.Id == nodeId)
            return this;

        return First?.FindParent(nodeId) ?? Second?.FindParent(nodeId);
    }

    /// <summary>
    /// Returns all leaf nodes (terminal panes) in traversal order.
    /// </summary>
    public IEnumerable<SplitNode> GetLeaves()
    {
        if (IsLeaf)
        {
            yield return this;
            yield break;
        }

        if (First != null)
        {
            foreach (var leaf in First.GetLeaves())
                yield return leaf;
        }

        if (Second != null)
        {
            foreach (var leaf in Second.GetLeaves())
                yield return leaf;
        }
    }

    /// <summary>
    /// Removes a leaf node with the given pane ID.
    /// The surviving sibling replaces the parent container.
    /// Returns true if the removal succeeded.
    /// </summary>
    public bool Remove(string paneId)
    {
        if (IsLeaf) return false;

        // Check if one of our direct children is the target
        SplitNode? target = null;
        SplitNode? survivor = null;

        if (First is { IsLeaf: true, PaneId: not null } && First.PaneId == paneId)
        {
            target = First;
            survivor = Second;
        }
        else if (Second is { IsLeaf: true, PaneId: not null } && Second.PaneId == paneId)
        {
            target = Second;
            survivor = First;
        }

        if (target != null && survivor != null)
        {
            // Replace this node's content with the survivor's content
            IsLeaf = survivor.IsLeaf;
            Direction = survivor.Direction;
            First = survivor.First;
            Second = survivor.Second;
            SplitRatio = survivor.SplitRatio;
            PaneId = survivor.PaneId;
            return true;
        }

        // Recurse into children
        if (First?.Remove(paneId) == true) return true;
        if (Second?.Remove(paneId) == true) return true;

        return false;
    }

    /// <summary>
    /// Gets the next leaf node after the given pane ID (for focus navigation).
    /// </summary>
    public SplitNode? GetNextLeaf(string paneId)
    {
        var leaves = GetLeaves().ToList();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].PaneId == paneId && i + 1 < leaves.Count)
                return leaves[i + 1];
        }
        return leaves.Count > 0 ? leaves[0] : null;
    }

    /// <summary>
    /// Gets the previous leaf node before the given pane ID.
    /// </summary>
    public SplitNode? GetPreviousLeaf(string paneId)
    {
        var leaves = GetLeaves().ToList();
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].PaneId == paneId && i - 1 >= 0)
                return leaves[i - 1];
        }
        return leaves.Count > 0 ? leaves[^1] : null;
    }

    /// <summary>Creates a layout with the given number of equal vertical columns.</summary>
    public static SplitNode CreateColumns(int count)
    {
        if (count <= 1) return CreateLeaf();
        var node = CreateLeaf();
        for (int i = 1; i < count; i++)
            node = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Vertical,
                SplitRatio = (double)i / (i + 1),
                First = node,
                Second = CreateLeaf(),
            };
        return node;
    }

    /// <summary>Creates a layout with the given number of equal horizontal rows.</summary>
    public static SplitNode CreateRows(int count)
    {
        if (count <= 1) return CreateLeaf();
        var node = CreateLeaf();
        for (int i = 1; i < count; i++)
            node = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Horizontal,
                SplitRatio = (double)i / (i + 1),
                First = node,
                Second = CreateLeaf(),
            };
        return node;
    }

    /// <summary>Creates a 2x2 grid layout.</summary>
    public static SplitNode CreateGrid()
    {
        return new SplitNode
        {
            IsLeaf = false,
            Direction = SplitDirection.Horizontal,
            SplitRatio = 0.5,
            First = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Vertical,
                SplitRatio = 0.5,
                First = CreateLeaf(),
                Second = CreateLeaf(),
            },
            Second = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Vertical,
                SplitRatio = 0.5,
                First = CreateLeaf(),
                Second = CreateLeaf(),
            },
        };
    }

    /// <summary>Creates a main+stack layout (large pane left, stacked panes right).</summary>
    public static SplitNode CreateMainStack(int stackCount = 2)
    {
        var stack = CreateLeaf();
        for (int i = 1; i < stackCount; i++)
            stack = new SplitNode
            {
                IsLeaf = false,
                Direction = SplitDirection.Horizontal,
                SplitRatio = (double)i / (i + 1),
                First = stack,
                Second = CreateLeaf(),
            };
        return new SplitNode
        {
            IsLeaf = false,
            Direction = SplitDirection.Vertical,
            SplitRatio = 0.6,
            First = CreateLeaf(),
            Second = stack,
        };
    }

    /// <summary>Recursively sets all split ratios to 0.5 (equalizes pane sizes).</summary>
    public void Equalize()
    {
        if (IsLeaf) return;
        SplitRatio = 0.5;
        First?.Equalize();
        Second?.Equalize();
    }

    /// <summary>Adjusts the split ratio of the nearest ancestor containing the given pane.</summary>
    public bool ResizePane(string paneId, double delta)
    {
        if (IsLeaf) return false;
        bool inFirst = First?.FindNode(paneId) != null;
        bool inSecond = Second?.FindNode(paneId) != null;
        if (!inFirst && !inSecond) return false;

        // Direct child is the target
        if ((First?.IsLeaf == true && First.PaneId == paneId) ||
            (Second?.IsLeaf == true && Second.PaneId == paneId))
        {
            SplitRatio = Math.Clamp(SplitRatio + (inFirst ? delta : -delta), 0.1, 0.9);
            return true;
        }
        // Recurse into the subtree containing the pane
        if (inFirst && First!.ResizePane(paneId, delta)) return true;
        if (inSecond && Second!.ResizePane(paneId, delta)) return true;
        // Pane is nested deeper — resize at this level
        SplitRatio = Math.Clamp(SplitRatio + (inFirst ? delta : -delta), 0.1, 0.9);
        return true;
    }

    /// <summary>Swaps two panes by exchanging their PaneId values.</summary>
    public bool SwapPanes(string paneId1, string paneId2)
    {
        var node1 = FindNode(paneId1);
        var node2 = FindNode(paneId2);
        if (node1 == null || node2 == null || !node1.IsLeaf || !node2.IsLeaf) return false;
        (node1.PaneId, node2.PaneId) = (node2.PaneId, node1.PaneId);
        return true;
    }
}
