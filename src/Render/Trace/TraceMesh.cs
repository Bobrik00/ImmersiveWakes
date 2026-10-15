using System;
using System.Collections.Generic;
using System.Diagnostics;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// The quads of the live nodes for vspt's Recorder (TraceQuads), one per node whose slot lies in the region, textured
// from the page of the entity atlas. The overlay's mesh would not do: its uv look into the wake texture, its vertices
// move with the camera's block, and its flags hold waterFlagsIn, whose bits 0-7 the tracer reads as glow
// (wf2-A-recorder-foam.md, section 3).
//
// Every write goes to the tracer whole, so the mesh is written only when the set of nodes changed (a node born or dead,
// a slot taken anew, a raised corner), when the anchor moved, when the caller forces it, and on a walk at least
// KeepAliveSeconds after the last write: after a change of the block atlas' page count the Recorder keeps a stale page
// number until the next copy (refute-mechanism, risks). The caller walks once a step with nodes, so on pause the mesh
// stands as it is.
//
// The corners a liquid above raises to the top of the block, as the overlay's (StillWaterScan.RaisedCorners), cached
// by slot: read when the slot held another block or no node at the last walk, and once every RefreshWalks walks. Only
// a liquid placed or taken above the water changes them, and the overlay's cache, which refreshes light too, does not
// run under Skip.
sealed class TraceMesh : TraceQuads
{
    const double KeepAliveSeconds = 5;
    const int RefreshWalks = 100;

    // What the mesh on the GPU holds, quad by quad, to tell a changed set from the same one.
    readonly int[] keyX, keyY, keyZ, keySlot;
    readonly byte[] keyRaised;

    // The corner cache by slot of the core's atlas.
    readonly int[] cachedX = new int[WakeAtlasCpu.Capacity];
    readonly int[] cachedY = new int[WakeAtlasCpu.Capacity];
    readonly int[] cachedZ = new int[WakeAtlasCpu.Capacity];
    readonly long[] laidAt = new long[WakeAtlasCpu.Capacity];
    readonly byte[] raised = new byte[WakeAtlasCpu.Capacity];
    long walks;

    public TraceMesh(ICoreClientAPI capi, int capacity)
        : base(capi, capacity)
    {
        keyX = new int[Capacity];
        keyY = new int[Capacity];
        keyZ = new int[Capacity];
        keySlot = new int[Capacity];
        keyRaised = new byte[Capacity];
        Array.Fill(laidAt, long.MinValue);
    }

    // Walks that found the same set and left the mesh as it was, and nodes whose corners were read.
    public long Unchanged { get; private set; }
    public long CornerReads { get; private set; }

    // The nodes in slots below the region's end, in the order given, relative to the anchor; force writes the mesh
    // whatever the set.
    public void Write(List<WakeNode> nodes, TraceRegion region, IBlockAccessor blocks, Vec3d camera, bool force)
    {
        // The anchor first, or a forced write would keep an old one or none: a pose far from the foam.
        bool moved = MoveAnchor(camera);
        bool changed = force || moved;
        float side = (float)TraceRegion.Texels / region.PageWidth, sideV = (float)TraceRegion.Texels / region.PageHeight;
        // 1/64 of a texel of the page inside the slot, as QuadMesh keeps inside the wake texture's slots: a ray whose
        // uv falls on the quad's edge would read the next slot, which holds another node.
        float insetU = 1f / (64f * region.PageWidth), insetV = 1f / (64f * region.PageHeight);
        float[] xyz = Data.xyz, uv = Data.Uv;
        int quads = 0;
        foreach (WakeNode node in nodes)
        {
            int slot = node.Slot;
            if ((uint)slot >= (uint)region.Slots) continue;
            if (quads == Capacity) break;
            int corners = Corners(slot, node, blocks);
            int q = quads++;
            if (!changed && (keyX[q] != node.X || keyY[q] != node.Y || keyZ[q] != node.Z || keySlot[q] != slot || keyRaised[q] != corners)) changed = true;
            keyX[q] = node.X;
            keyY[q] = node.Y;
            keyZ[q] = node.Z;
            keySlot[q] = slot;
            keyRaised[q] = (byte)corners;

            (float u0, float v0) = region.SlotCorner(slot);
            int x = node.X - AnchorX, y = node.Y - AnchorY, z = node.Z - AnchorZ;
            for (int k = 0; k < 4; k++)
            {
                int v = q * 4 + k, dx = QuadMesh.CornerDx[k], dz = QuadMesh.CornerDz[k];
                xyz[v * 3] = x + dx;
                xyz[v * 3 + 1] = y + ((corners & 1 << k) != 0 ? 1f : QuadMesh.SurfaceHeight);
                xyz[v * 3 + 2] = z + dz;
                uv[v * 2] = u0 + (dx == 0 ? insetU : side - insetU);
                uv[v * 2 + 1] = v0 + (dz == 0 ? insetV : sideV - insetV);
            }
        }
        walks++;
        if (quads != QuadCount) changed = true;
        if (!changed && Stopwatch.GetElapsedTime(LastCommit).TotalSeconds < KeepAliveSeconds)
        {
            Unchanged++;
            return;
        }
        Commit(quads);
    }

    int Corners(int slot, WakeNode node, IBlockAccessor blocks)
    {
        if (laidAt[slot] != walks - 1 || (walks + slot) % RefreshWalks == 0
            || cachedX[slot] != node.X || cachedY[slot] != node.Y || cachedZ[slot] != node.Z)
        {
            raised[slot] = (byte)StillWaterScan.RaisedCorners(blocks, node.X, node.Y, node.Z);
            cachedX[slot] = node.X;
            cachedY[slot] = node.Y;
            cachedZ[slot] = node.Z;
            CornerReads++;
        }
        laidAt[slot] = walks;
        return raised[slot];
    }
}
