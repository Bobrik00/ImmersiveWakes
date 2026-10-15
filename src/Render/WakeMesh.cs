using System;
using System.Collections.Generic;
using ImmersiveWakes.Game;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render;

// The wake's quads: one per live node of the core, written in a frame whose steps ticked the core and when a new core
// replaces it (WakeUpload). Wakes draws a quad 1x1 per node of the visible chunks and tests each chunk of 8 x 8 blocks
// with a box 8 x 1 x 8 (FINDINGS "Геометрия и два прохода"). The core hands its nodes out chunk by chunk, so the quads
// of a chunk lie in one run of the mesh, and the runs are tested the same way. A run in view draws the whole mesh: the
// one API that draws index ranges, RenderMesh(meshRef, starts, sizes, count), takes the engine's SSBO branch on every
// client but a Mac and draws with the chunk pools' shared index buffer instead of the mesh's own
// (vs-rendermesh-ranges-int-pairs), while the quads out of view cost the GPU next to nothing. The capacity follows the
// core's node limit (fork 20).
//
// Corner heights and light come from the blocks of layer y+1 (QuadMesh.ReadCorners), the corners' oceanity from the
// water of layer y and the ocean map (OceanFlags.Corners), which reads the eight cells around only on the sea row. Nine
// blocks per node, two block accessor calls each, would cost about a third of a millisecond a step at 300 nodes, so the
// corners are cached by atlas slot: read again when the slot was empty at the last write (a new node, even on the block
// of a dead one) or holds a node of another block, and otherwise once every RefreshWrites writes, the slots in turn.
// Light from a torch placed by the water reaches the foam within a second of steps; day and night come through the
// ambient uniforms, not this light.
sealed class WakeMesh : IDisposable
{
    const int RefreshWrites = 20;

    // The sphere around a chunk's box: 8 x 8 at the surface, half a block of wave up and down, and a block for the wave
    // the warp adds to the corners (FINDINGS "Волна"). On the open sea the wave moves a corner by about 1.8 blocks at
    // wind 1 (vs-water-wave-vertical-yindependent) and up to about 2.4 in a storm, where the surface wind reaches 1.5;
    // the sphere holds a corner 3.4 blocks under the surface at 7/8 or 3.7 above it.
    static readonly double ChunkRadius = Math.Sqrt(4 * 4 + 4 * 4 + 0.5 * 0.5) + 1;

    readonly ICoreClientAPI capi;
    readonly List<WakeNode> nodes = [];
    QuadMesh quads;

    // Runs of quads by chunk, in mesh order, contiguous.
    int[] runFirst = [], runCount = [], runCx = [], runY = [], runCz = [];
    int runs;

    // The block whose corners each atlas slot holds and the write that last laid a quad from the slot.
    readonly int[] cachedX = new int[WakeAtlasCpu.Capacity];
    readonly int[] cachedY = new int[WakeAtlasCpu.Capacity];
    readonly int[] cachedZ = new int[WakeAtlasCpu.Capacity];
    readonly long[] laidAt = new long[WakeAtlasCpu.Capacity];
    readonly int[] cornerLight = new int[WakeAtlasCpu.Capacity * 4];
    readonly byte[] raisedCorners = new byte[WakeAtlasCpu.Capacity];
    readonly int[] oceanCorners = new int[WakeAtlasCpu.Capacity];
    long writes;

    public WakeMesh(ICoreClientAPI capi, int capacity)
    {
        this.capi = capi;
        Array.Fill(laidAt, long.MinValue);
        quads = Build(capacity);
    }

    public int Capacity => quads.Capacity;
    public int QuadCount => quads.QuadCount;
    public MeshRef? Ref => quads.Ref;
    // Where the vertices were laid from: the camera's block at the last write. The renderer passes Origin minus the
    // camera of each frame, so the quads stay put while the camera moves between writes.
    public Vec3d Origin => quads.Origin;
    public int Chunks => runs;

    // Nodes whose corners were read from the world in this world, nine blocks each; with the stats it shows the cache
    // at work.
    public long CornerReads { get; private set; }

    // A new mesh for a new node limit; the next Write fills it. The corner cache is by slot and stays.
    public void Resize(int capacity)
    {
        if (Math.Max(1, capacity) == quads.Capacity) return;
        quads.Dispose();
        quads = Build(capacity);
    }

    QuadMesh Build(int capacity)
    {
        var mesh = new QuadMesh(capi, capacity);
        int n = mesh.Capacity;
        runFirst = new int[n];
        runCount = new int[n];
        runCx = new int[n];
        runY = new int[n];
        runCz = new int[n];
        runs = 0;
        return mesh;
    }

    public void Clear()
    {
        runs = 0;
        quads.Commit(0);
    }

    // The live nodes of the core in traversal order, relative to the block the camera is in. Nodes past the capacity are
    // left out, which only a core with a higher limit than the mesh would give.
    public void Write(WakeHandler core, IBlockAccessor blocks, Vec3d camera)
    {
        core.CollectNodes(nodes);
        WakeAtlasCpu atlas = core.Atlas;
        int count = Math.Min(nodes.Count, quads.Capacity);
        if (count == 0 && quads.QuadCount == 0)
        {
            runs = 0;
            writes++;
            return;
        }
        int ox = (int)Math.Floor(camera.X), oy = (int)Math.Floor(camera.Y), oz = (int)Math.Floor(camera.Z);
        quads.Origin.Set(ox, oy, oz);
        float size = atlas.Size, side = atlas.SlotTexels / size;
        int seaLevel = capi.World.SeaLevel;
        runs = 0;
        try
        {
            for (int q = 0; q < count; q++)
            {
                WakeNode node = nodes[q];
                int cx = node.X >> 3, cz = node.Z >> 3, last = runs - 1;
                if (last >= 0 && runCx[last] == cx && runCz[last] == cz && runY[last] == node.Y) runCount[last]++;
                else
                {
                    runFirst[runs] = q;
                    runCount[runs] = 1;
                    runCx[runs] = cx;
                    runY[runs] = node.Y;
                    runCz[runs] = cz;
                    runs++;
                }

                int slot = node.Slot;
                Span<int> light = cornerLight.AsSpan(slot * 4, 4);
                if (laidAt[slot] != writes - 1 || (writes + slot) % RefreshWrites == 0
                    || cachedX[slot] != node.X || cachedY[slot] != node.Y || cachedZ[slot] != node.Z)
                {
                    raisedCorners[slot] = (byte)QuadMesh.ReadCorners(blocks, node.X, node.Y, node.Z, light);
                    oceanCorners[slot] = OceanFlags.Corners(blocks, seaLevel, node.X, node.Y, node.Z);
                    cachedX[slot] = node.X;
                    cachedY[slot] = node.Y;
                    cachedZ[slot] = node.Z;
                    CornerReads++;
                }
                laidAt[slot] = writes;
                (int u, int v) = atlas.SlotCorner(slot);
                quads.SetQuad(q, node.X - ox, node.Y - oy, node.Z - oz, u / size, v / size, side, raisedCorners[slot], oceanCorners[slot], light);
            }
        }
        finally
        {
            nodes.Clear();
        }
        quads.Commit(count);
        writes++;
    }

    // The runs whose chunk sphere is in the frustum and, measured horizontally as the engine culls chunks, within reach:
    // their quads and their number; no quad in view means no draw.
    public (int Quads, int Chunks) InView(FrustumCulling culler, Vec3d camera, double reach)
    {
        int quads = 0, chunks = 0;
        double limit = reach + ChunkRadius;
        for (int r = 0; r < runs; r++)
        {
            double x = runCx[r] * 8 + 4, y = runY[r] + 1, z = runCz[r] * 8 + 4;
            double dx = x - camera.X, dz = z - camera.Z;
            if (dx * dx + dz * dz > limit * limit || !culler.SphereInFrustum(x, y, z, ChunkRadius)) continue;
            quads += runCount[r];
            chunks++;
        }
        return (quads, chunks);
    }

    public void Dispose()
    {
        quads.Dispose();
        runs = 0;
    }
}
