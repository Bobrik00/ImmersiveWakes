using System;
using System.Collections.Generic;

namespace ImmersiveWakes.Simulation;

readonly record struct BlockKey(int X, int Y, int Z);

// The insertion queue of Wakes' WakeHandler: blocks that enter the map at the start of the next tick, each with its
// flood level and the impulses gathered for it. Wakes queued a whole new node per touched block; here an entry holds
// one impulse array, taken lazily from a pool, so a tick allocates nothing once the counts stop growing (П5).
// One entry per block (x, y, z): a later arrival merges into the first one, which keeps its place and its flood
// level, and every cell keeps the larger impulse (src_port WakeHandler.insert, WakeNode.mergeImpulses). Entries keep
// the order of arrival: flood fill of the last tick in traversal order, then the sources' steps in the order of the
// calls (FINDINGS "Баги и причуды оригинала").
sealed class ImpulseBuffer
{
    public struct Entry
    {
        public int X, Y, Z, FloodLevel;

        // The water tint for the node the entry makes; a block that held a live node when queued keeps 0, its node is
        // only revived.
        public int Tint;

        // Layout of SimulationNode.Impulses; null while nothing has stamped the block.
        public float[]? Impulses;

        // Queued as the neighbour of a touched block (CoreModes.EagerNeighbours), not touched itself. The drain makes
        // these nodes after all the others, so near the node limit they never take a slot a touch of the same tick
        // needs; once made they hold it like any node. For a live node made ahead the entry only renews its age.
        public bool Ahead;
    }

    readonly int stride, layerSize, keep;
    readonly Dictionary<BlockKey, int> byKey = [];
    readonly Stack<float[]> free = new();
    Entry[] entries = new Entry[64];

    // keep bounds the arrays held between ticks. The node limit does not bound the queue: one tick of many long steps
    // stamps thousands of blocks, and a pool that never shrinks would hold tens of megabytes after it. With keep at
    // the node limit, and no lower than WakeHandler.MinKeptImpulses, a steady tick still allocates nothing.
    public ImpulseBuffer(int resolution, int keep)
    {
        stride = resolution + 2;
        layerSize = stride * stride;
        this.keep = keep;
    }

    public int Count { get; private set; }

    // Entries with Ahead set; without them the drain takes one pass, as before T07.05.
    public int AheadCount { get; private set; }

    // Impulse arrays waiting for the next stamp, at most keep.
    public int Idle => free.Count;

    // Arrivals for a block already queued, counted over the life of the queue.
    public int Merges { get; private set; }

    public ref readonly Entry this[int index] => ref entries[index];

    // The index of the block's entry, made at the end with floodLevel and tint if the block has none.
    public int Enqueue(int x, int y, int z, int floodLevel, int tint)
    {
        var key = new BlockKey(x, y, z);
        if (byKey.TryGetValue(key, out int index))
        {
            Merges++;
            return index;
        }
        if (Count == entries.Length) Array.Resize(ref entries, entries.Length * 2);
        index = Count++;
        entries[index] = new Entry { X = x, Y = y, Z = z, FloodLevel = floodLevel, Tint = tint };
        byKey.Add(key, index);
        return index;
    }

    // A neighbour queued ahead of flood fill: flood level 0, so its node does not flood. A block already queued keeps
    // its entry as it is.
    public void EnqueueAhead(int x, int y, int z, int tint)
    {
        int count = Count;
        int index = Enqueue(x, y, z, 0, tint);
        if (index != count) return;
        entries[index].Ahead = true;
        AheadCount++;
    }

    public bool IsQueued(int x, int y, int z) => byKey.ContainsKey(new BlockKey(x, y, z));

    // A touch of a block queued ahead: the entry becomes the one the touch would have made first, with the touch's
    // flood level, and the drain makes its node with the others.
    public void Touch(int index, int floodLevel)
    {
        ref Entry entry = ref entries[index];
        if (!entry.Ahead) return;
        entry.Ahead = false;
        entry.FloodLevel = floodLevel;
        AheadCount--;
    }

    // setInitialValue over the 3x3 cells around inner cell (cellZ, cellX), kept by the larger value. Inside one call of
    // the handler every cell gets the same value, so this is Wakes' overwrite there and its merge by maximum across
    // calls. The stamp spills into the ghost ring at the node's edge, which the step never reads (FINDINGS "Импульсы").
    public void Stamp(int index, int cellZ, int cellX, float value)
    {
        ref Entry entry = ref entries[index];
        float[] a = entry.Impulses ??= Rent();
        for (int z = cellZ; z <= cellZ + 2; z++)
        {
            for (int i = z * stride + cellX, end = i + 3; i < end; i++)
            {
                if (value > a[i]) a[i] = value;
            }
        }
    }

    public void Clear()
    {
        for (int i = 0; i < Count; i++)
        {
            if (entries[i].Impulses is { } a && free.Count < keep) free.Push(a);
            entries[i] = default;
        }
        Count = 0;
        AheadCount = 0;
        byKey.Clear();
    }

    float[] Rent()
    {
        if (!free.TryPop(out float[]? a)) return new float[layerSize];
        Array.Clear(a);
        return a;
    }
}
