using System;
using System.Collections.Generic;

namespace ImmersiveWakes.Simulation;

// Nodes with their field arrays, used again. Wakes allocates a node with about 6.4 KB of arrays at R16 for every
// touched block of every tick, and the workstation GC of the client pays for that on the main thread
// (П5, FINDINGS "Цвет (WakeColor)"). One resolution per pool: a new resolution builds a new core. The pool does not
// shrink: after a burst to the 2000-node limit it keeps about 10.6 MB at R16 and 37 MB at R32 until the core goes.
//
// With an atlas every rented node holds a slot until it comes back, so the slots in use are the live nodes. Nodes are
// born and die only here, in the middle of the walk too, so nothing else claims or frees a slot.
sealed class NodePool
{
    readonly Stack<WakeNode> free = new();
    readonly WakeAtlasCpu? atlas;

    public NodePool(int resolution, WakeAtlasCpu? atlas = null)
    {
        Resolution = resolution;
        this.atlas = atlas;
    }

    public int Resolution { get; }

    // Nodes ever allocated; the rest of them is either live or waiting here.
    public int Created { get; private set; }

    public int Idle => free.Count;

    // The node is cleared here and not on Return, so whatever state it came back in, the next owner starts clean.
    // The slot is claimed first: a full atlas throws before a node leaves the pool.
    public WakeNode Rent(int x, int y, int z, int floodLevel, int waterTint, bool ahead = false)
    {
        int slot = atlas?.Claim() ?? -1;
        if (!free.TryPop(out WakeNode? node))
        {
            node = new WakeNode(Resolution);
            Created++;
        }
        node.Pooled = false;
        node.Slot = slot;
        node.Init(x, y, z, floodLevel, waterTint, ahead);
        return node;
    }

    // A node returned twice would go to two blocks with one field, and a node still linked would feed its new block's
    // edges into the old neighbours' halo; both corrupt the wake silently, so they throw.
    public void Return(WakeNode node)
    {
        if (node.Pooled) throw new InvalidOperationException($"node ({node.X}, {node.Y}, {node.Z}) returned to the pool twice");
        if (node.North?.South == node || node.East?.West == node || node.South?.North == node || node.West?.East == node)
        {
            throw new InvalidOperationException($"node ({node.X}, {node.Y}, {node.Z}) returned while its neighbours still link it");
        }
        if (node.Slot >= 0) atlas!.Free(node.Slot);
        node.Slot = -1;
        node.Pooled = true;
        free.Push(node);
    }
}
