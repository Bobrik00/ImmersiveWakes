using System;
using System.Collections.Generic;

namespace ImmersiveWakes.Simulation;

// Wakes' WakeChunk: the nodes of 8 x 8 blocks at one y, nodes[z][x] in one flat array (FINDINGS "Сетка"). The map
// hands an emptied chunk out again for another place.
sealed class WakeChunk
{
    public const int Width = 8;

    readonly WakeNode?[] nodes = new WakeNode?[Width * Width];
    readonly WakeChunkMap map;

    public WakeChunk(WakeChunkMap map)
    {
        this.map = map;
    }

    public ChunkKey Key { get; private set; }
    public int Occupied { get; private set; }

    // A node left behind would sit at the new place with links to its old neighbours.
    public void MoveTo(ChunkKey key)
    {
        if (Occupied != 0) throw new InvalidOperationException($"chunk {Key} still holds {Occupied} nodes");
        Key = key;
    }

    // Local block (x, z), at most one block out. Out on one axis reads the neighbouring chunk, out on both axes gives
    // null even when the diagonal chunk holds a node there, as in Wakes.
    public WakeNode? Get(int x, int z)
    {
        if (x >= 0 && x < Width)
        {
            if (z >= 0 && z < Width) return nodes[z * Width + x];
            WakeChunk? next = map.GetChunk(Key.Cx, Key.Y, z < 0 ? Key.Cz - 1 : Key.Cz + 1);
            return next?.nodes[FloorMath.FloorMod(z, Width) * Width + x];
        }
        if (z >= 0 && z < Width)
        {
            WakeChunk? next = map.GetChunk(x < 0 ? Key.Cx - 1 : Key.Cx + 1, Key.Y, Key.Cz);
            return next?.nodes[z * Width + FloorMath.FloorMod(x, Width)];
        }
        return null;
    }

    // Into a free slot only. Wakes revived the node already in the slot here; the port's handler finds that node
    // first and never builds a second one (T04.05).
    public void Insert(WakeNode node)
    {
        int x = FloorMath.FloorMod(node.X, Width), z = FloorMath.FloorMod(node.Z, Width);
        ref WakeNode? slot = ref nodes[z * Width + x];
        if (slot != null) throw new InvalidOperationException($"block ({node.X}, {node.Y}, {node.Z}) already holds a node");
        slot = node;
        Occupied++;

        if (Get(x, z + 1) is { } south)
        {
            south.North = node;
            node.South = south;
        }
        if (Get(x + 1, z) is { } east)
        {
            east.West = node;
            node.East = east;
        }
        if (Get(x, z - 1) is { } north)
        {
            north.South = node;
            node.North = north;
        }
        if (Get(x - 1, z) is { } west)
        {
            west.East = node;
            node.West = west;
        }
    }

    // By z, then x, as Wakes. A dead node leaves its slot and its neighbours and goes back to the pool before the next
    // one ticks, so a reference to a node holds until the next Tick. False when the chunk is left empty.
    public bool Tick(SimulationParams p, IWaterProbe water, IFloodQueue queue, NodePool pool, CoreModes modes)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            WakeNode? node = nodes[i];
            if (node == null || node.Tick(p, water, queue, modes)) continue;
            node.Unlink();
            nodes[i] = null;
            Occupied--;
            pool.Return(node);
        }
        return Occupied != 0;
    }

    public void SnapHalo()
    {
        foreach (WakeNode? node in nodes) node?.SnapHalo();
    }

    public void CollectNodes(List<WakeNode> output)
    {
        foreach (WakeNode? node in nodes)
        {
            if (node != null) output.Add(node);
        }
    }
}
