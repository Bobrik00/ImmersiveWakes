using System;
using System.Collections.Generic;

namespace ImmersiveWakes.Simulation;

// WakeChunkPos: chunk (floorDiv(x, 8), y, floorDiv(z, 8)) of a block. Sorted by (cz, cx, y), the traversal order.
readonly record struct ChunkKey(int Cx, int Y, int Cz) : IComparable<ChunkKey>
{
    public static ChunkKey Of(int x, int y, int z) =>
        new(FloorMath.FloorDiv(x, WakeChunk.Width), y, FloorMath.FloorDiv(z, WakeChunk.Width));

    public int CompareTo(ChunkKey other)
    {
        if (Cz != other.Cz) return Cz.CompareTo(other.Cz);
        if (Cx != other.Cx) return Cx.CompareTo(other.Cx);
        return Y.CompareTo(other.Y);
    }
}

// The chunks of Wakes' WakeHandler and their traversal. Wakes walks a HashMap, whose order changes with the place in
// the world, and with it the halo lag and the lives of nodes: a boat moved by (800, -1200) blocks differs from itself
// in 13% of texels. The port walks chunks by (cz, cx, y) ascending, so a node always ticks after its neighbours N and
// W and before S and E, and a run moved by whole chunks repeats itself bit for bit (fork 17, FINDINGS "Шаг волнового
// уравнения"). Chunks are used again like nodes; once the counts stop growing, a tick allocates nothing (П5).
sealed class WakeChunkMap
{
    readonly Dictionary<ChunkKey, WakeChunk> byKey = [];
    readonly List<WakeChunk> ordered = [];
    readonly Stack<WakeChunk> free = new();
    readonly NodePool pool;
    bool walking;

    public WakeChunkMap(NodePool pool)
    {
        this.pool = pool;
    }

    public int ChunkCount => ordered.Count;
    public int NodeCount { get; private set; }

    // Chunks ever allocated; the rest of them is either in the map or waiting.
    public int ChunksCreated { get; private set; }

    public WakeChunk? GetChunk(int cx, int y, int cz) =>
        byKey.TryGetValue(new ChunkKey(cx, y, cz), out WakeChunk? chunk) ? chunk : null;

    public WakeNode? GetNode(int x, int y, int z) =>
        GetChunk(FloorMath.FloorDiv(x, WakeChunk.Width), y, FloorMath.FloorDiv(z, WakeChunk.Width))
            ?.Get(FloorMath.FloorMod(x, WakeChunk.Width), FloorMath.FloorMod(z, WakeChunk.Width));

    // Between ticks only, into a free block (WakeChunk.Insert). A node inserted during the walk would shift the chunk
    // list under it or join a chunk that has already ticked, so flood fill goes through the queue, as in Wakes. A
    // missing chunk is made here.
    public void Insert(WakeNode node)
    {
        if (walking) throw new InvalidOperationException($"node ({node.X}, {node.Y}, {node.Z}) inserted during the tick");
        var key = ChunkKey.Of(node.X, node.Y, node.Z);
        if (!byKey.TryGetValue(key, out WakeChunk? chunk))
        {
            if (!free.TryPop(out chunk))
            {
                chunk = new WakeChunk(this);
                ChunksCreated++;
            }
            chunk.MoveTo(key);
            byKey.Add(key, chunk);
            ordered.Insert(InsertionIndex(key), chunk);
        }
        chunk.Insert(node);
        NodeCount++;
    }

    // Every chunk in order, then the empty ones go, as in Wakes. Two passes keep the map whole when a tick throws
    // halfway: the list is not compacted under the walk. The synchronous halo takes its own pass first, after the
    // queue went in and before any node steps or dies, as src_port_snap does in WakeHandler.wakeLogic.
    public void Tick(SimulationParams p, IWaterProbe water, IFloodQueue queue, CoreModes modes = CoreModes.None)
    {
        walking = true;
        try
        {
            if ((modes & CoreModes.SyncHalo) != 0)
            {
                foreach (WakeChunk chunk in ordered) chunk.SnapHalo();
            }
            foreach (WakeChunk chunk in ordered) chunk.Tick(p, water, queue, pool, modes);
        }
        finally
        {
            walking = false;
        }

        int kept = 0, live = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            WakeChunk chunk = ordered[i];
            if (chunk.Occupied != 0)
            {
                ordered[kept++] = chunk;
                live += chunk.Occupied;
                continue;
            }
            byKey.Remove(chunk.Key);
            free.Push(chunk);
        }
        ordered.RemoveRange(kept, ordered.Count - kept);
        NodeCount = live;
    }

    // Live nodes in traversal order: chunks by (cz, cx, y), inside a chunk by z, then x.
    public void CollectNodes(List<WakeNode> output)
    {
        output.Clear();
        foreach (WakeChunk chunk in ordered) chunk.CollectNodes(output);
    }

    // The keys are unique, so this is the first chunk that sorts after the key.
    int InsertionIndex(ChunkKey key)
    {
        int lo = 0, hi = ordered.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (ordered[mid].Key.CompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
