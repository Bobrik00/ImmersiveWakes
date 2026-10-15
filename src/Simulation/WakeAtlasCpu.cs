using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ImmersiveWakes.Simulation;

// The CPU side of Wakes' WakeTextureAtlas: 64 x 64 slots of SlotTexels x SlotTexels ARGB texels, one slot per live
// node. A slot goes to the lowest free number (claimSubTexture) and keeps its pixels when freed (invalidate); unlike
// Wakes, the top of the slots in use comes down again as the slots above are freed, so the strip to upload shrinks
// after a burst (FINDINGS "Атлас текстуры", fork 20).
//
// The game atlas has 32 texels a slot at any resolution (fork 14 after S1): a node at R16 or R8 is stretched by
// bilinear heights, coloured texel by texel. With SlotTexels equal to the resolution a node is written 1:1 as Wakes
// does, which is the bench's mode against the oracle.
sealed class WakeAtlasCpu
{
    public const int SlotsPerRow = 64;

    // Wakes keeps the last slot for its error texture; the handler's node limit stays below this.
    public const int Capacity = SlotsPerRow * SlotsPerRow - 1;

    public const int GameTexels = 32;

    readonly ulong[] used = new ulong[SlotsPerRow * SlotsPerRow / 64];
    int dirtyFirst = int.MaxValue, dirtyEnd;

    // Scratch of a node: its cells inside a ring of the neighbours' edges, the ring's rows stretched along x, then
    // the heights of its texels.
    readonly int res, factor, ring;
    readonly float[] cells, rows, texels;
    readonly int[] buckets = new int[WakePalette.ColorCount];

    // Texel i of a slot row lies between ring samples sample[i] and sample[i] + 1, at weights near[i] and far[i].
    readonly int[] sample;
    readonly float[] near, far;

    // CoreModes.SoftEdge: a texel i texels in from an edge without a neighbour keeps edgeRamp[i] of its alpha, one at
    // the corner without a diagonal neighbour cornerRamp[dz * band + dx] by its distance from the corner; band texels
    // from an edge are faded. edgeColumns holds a node's weights along x.
    readonly float[] edgeRamp, cornerRamp, edgeColumns;
    readonly int band;

    // The same row on AVX2, 8 texels at a time: block q loads 8 ring samples from first[q] on and picks each texel's
    // pair out of them, so a lane multiplies and adds what the scalar loop does for its texel. Null without AVX2 or
    // without stretching. Stretched at least twice, 8 texels read at most 6 samples: 6 at R16 to 32, 4 at R8 to 32.
    readonly int[]? first;
    readonly Vector256<int>[]? pickNear, pickFar;
    readonly Vector256<float>[]? nearLanes, farLanes;

    // pixels takes the buffer of a core this one replaces: at 32 texels a slot it is 16 MiB in the large object heap,
    // and a new one on every rebuild would cost a gen2 collection when the old one goes. Its texels stay as they are;
    // a slot is drawn whole by the tick that gives it to a node.
    public WakeAtlasCpu(int resolution, int slotTexels = GameTexels, int[]? pixels = null)
    {
        if (slotTexels < resolution || slotTexels > GameTexels || slotTexels % resolution != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotTexels), slotTexels,
                $"a slot of R{resolution} holds a whole multiple of {resolution} texels, at most {GameTexels}");
        }
        res = resolution;
        SlotTexels = slotTexels;
        factor = slotTexels / resolution;
        ring = resolution + 2;
        if (pixels != null && pixels.Length != Size * Size)
        {
            throw new ArgumentException($"an atlas of {SlotTexels} texels a slot takes {Size * Size} pixels, not {pixels.Length}", nameof(pixels));
        }
        Pixels = pixels ?? new int[Size * Size];
        // A block of the last ring row loads up to 8 samples past the row's end, whose lanes it never picks.
        cells = new float[ring * ring + Lanes];
        rows = new float[ring * slotTexels];
        texels = new float[slotTexels * slotTexels];
        sample = new int[slotTexels];
        near = new float[slotTexels];
        far = new float[slotTexels];

        // The centre of texel i is at (i + 0.5) / factor - 0.5 in units of node cells, cell c centred at c. Cell -1
        // and cell res are the neighbours' edges, ring indices 0 and res + 1. At the game's factors 1, 2 and 4 the
        // weights are exact binary fractions.
        for (int i = 0; i < slotTexels; i++)
        {
            double p = (i + 0.5) / factor - 0.5;
            double cell = Math.Floor(p);
            sample[i] = (int)cell + 1;
            far[i] = (float)(p - cell);
            near[i] = 1 - far[i];
        }

        edgeRamp = new float[slotTexels];
        edgeColumns = new float[slotTexels];
        for (int i = 0; i < slotTexels; i++)
        {
            edgeRamp[i] = (float)Math.Min(1, (i + 0.5) / slotTexels / EdgeFadeBlocks);
            if (edgeRamp[i] < 1) band = i + 1;
        }
        // FadeEdges takes a texel to lie in at most one strip along each axis and in one corner.
        if (2 * band > slotTexels) throw new InvalidOperationException($"a soft edge of {EdgeFadeBlocks} blocks is over half a block");
        cornerRamp = new float[band * band];
        for (int dz = 0; dz < band; dz++)
        {
            for (int dx = 0; dx < band; dx++)
            {
                double d = Math.Sqrt((dx + 0.5) * (dx + 0.5) + (dz + 0.5) * (dz + 0.5)) / slotTexels;
                cornerRamp[dz * band + dx] = (float)Math.Min(1, d / EdgeFadeBlocks);
            }
        }

        if (factor == 1 || !Avx2.IsSupported || slotTexels % Lanes != 0) return;
        int blocks = slotTexels / Lanes;
        first = new int[blocks];
        pickNear = new Vector256<int>[blocks];
        pickFar = new Vector256<int>[blocks];
        nearLanes = new Vector256<float>[blocks];
        farLanes = new Vector256<float>[blocks];
        Span<int> pick = stackalloc int[Lanes];
        for (int q = 0; q < blocks; q++)
        {
            int start = q * Lanes;
            first[q] = sample[start];
            for (int k = 0; k < Lanes; k++) pick[k] = sample[start + k] - first[q];
            pickNear[q] = Vector256.Create((ReadOnlySpan<int>)pick);
            pickFar[q] = pickNear[q] + Vector256<int>.One;
            nearLanes[q] = Vector256.Create((ReadOnlySpan<float>)near.AsSpan(start, Lanes));
            farLanes[q] = Vector256.Create((ReadOnlySpan<float>)far.AsSpan(start, Lanes));
        }
    }

    const int Lanes = 8;

    // Width of the faded strip of CoreModes.SoftEdge, in blocks.
    public const double EdgeFadeBlocks = 0.25;

    public int SlotTexels { get; }
    public int Size => SlotsPerRow * SlotTexels;

    // ARGB, Size x Size by rows; texel (x, z) of a slot is at column x, row z, so u runs along +X and v along +Z.
    public int[] Pixels { get; }

    public int Used { get; private set; }

    // One past the highest slot in use: no quad looks above it.
    public int Top { get; private set; }

    // The lowest free slot. The handler keeps its nodes below Capacity, so a full atlas is a bug.
    public int Claim()
    {
        for (int w = 0; w < used.Length; w++)
        {
            ulong bits = used[w];
            if (bits == ulong.MaxValue) continue;
            int slot = w * 64 + BitOperations.TrailingZeroCount(~bits);
            if (slot >= Capacity) break;
            used[w] = bits | 1UL << (slot & 63);
            Used++;
            if (slot >= Top) Top = slot + 1;
            return slot;
        }
        throw new InvalidOperationException($"all {Capacity} atlas slots are taken");
    }

    // The pixels stay; the next node in the slot is drawn whole at the end of the tick that makes it.
    public void Free(int slot)
    {
        if ((uint)slot >= Capacity || (used[slot >> 6] & 1UL << (slot & 63)) == 0)
        {
            throw new InvalidOperationException($"atlas slot {slot} is not in use");
        }
        used[slot >> 6] &= ~(1UL << (slot & 63));
        Used--;
        if (slot + 1 != Top) return;
        Top = 0;
        for (int w = slot >> 6; w >= 0; w--)
        {
            if (used[w] == 0) continue;
            Top = w * 64 + 64 - BitOperations.LeadingZeroCount(used[w]);
            break;
        }
    }

    public int Texel(int slot, int z, int x) => Pixels[Origin(slot) + z * Size + x];

    // Texel column and row of the slot's corner at -X -Z; the slot spans SlotTexels from there along +X and +Z.
    public (int X, int Z) SlotCorner(int slot) => (slot % SlotsPerRow * SlotTexels, slot / SlotsPerRow * SlotTexels);

    // Texel rows drawn since the last call: one strip from the lowest slot row drawn to the highest, empty when
    // nothing was drawn. The uploader takes it once a step.
    public (int FirstRow, int Rows) TakeDirty()
    {
        if (dirtyEnd == 0) return (0, 0);
        var strip = (dirtyFirst * SlotTexels, (dirtyEnd - dirtyFirst) * SlotTexels);
        dirtyFirst = int.MaxValue;
        dirtyEnd = 0;
        return strip;
    }

    // WakeNode.draw: every texel of the node's slot, coloured by the palette's buckets blended with its water tint at
    // the opacity baked on its tick. softEdge fades the texels toward a missing neighbour (CoreModes.SoftEdge), except
    // in the debug colours, which show the field as it is.
    public void Draw(WakeNode node, WakePalette palette, bool debugColors, bool softEdge = false)
    {
        if (node.Slot < 0) throw new InvalidOperationException($"node ({node.X}, {node.Y}, {node.Z}) has no atlas slot");
        ReadOnlySpan<float> h = TexelHeights(node);
        if (!debugColors) palette.BlendBuckets(node.WaterTint, node.Opacity, buckets);

        int[] pixels = Pixels, colors = buckets;
        int t = SlotTexels, origin = Origin(node.Slot);
        for (int z = 0; z < t; z++)
        {
            Span<int> row = pixels.AsSpan(origin + z * Size, t);
            ReadOnlySpan<float> heights = h.Slice(z * t, t);
            if (debugColors)
            {
                for (int x = 0; x < row.Length; x++) row[x] = WakePalette.DebugColor(heights[x]);
            }
            else palette.Paint(heights, colors, row);
        }
        if (softEdge && !debugColors) FadeEdges(node, origin);

        int slotRow = node.Slot / SlotsPerRow;
        if (slotRow < dirtyFirst) dirtyFirst = slotRow;
        if (slotRow >= dirtyEnd) dirtyEnd = slotRow + 1;
    }

    // The heights the node's texels are coloured by, SlotTexels^2 by rows of z, valid until the next call. One texel a
    // cell takes the cell's mean of three layers as SimulationNode.getPixelColor does. Stretched texels interpolate
    // the cells bilinearly and take the neighbours' cells at the edges through the node's links, which hold live
    // nodes only once the walk is over; a corner goes through two links, and a missing neighbour repeats the node's
    // own edge. The ghost ring is not read: the halo puts only layer 1 there, a step behind for the neighbours south
    // and east.
    public ReadOnlySpan<float> TexelHeights(WakeNode node)
    {
        SimulationNode own = node.Sim;
        float[] h = texels;
        if (factor == 1)
        {
            for (int z = 0; z < res; z++) own.HeightRow(z, h.AsSpan(z * res, res));
            return h;
        }

        float[] c = cells, xs = rows;
        int last = res - 1, s = ring, t = SlotTexels;
        for (int z = 0; z < res; z++) own.HeightRow(z, c.AsSpan((z + 1) * s + 1, res));
        Span<float> northRow = c.AsSpan(1, res), southRow = c.AsSpan((res + 1) * s + 1, res);
        if (node.North != null) node.North.Sim.HeightRow(last, northRow);
        else c.AsSpan(s + 1, res).CopyTo(northRow);
        if (node.South != null) node.South.Sim.HeightRow(0, southRow);
        else c.AsSpan(res * s + 1, res).CopyTo(southRow);
        SimulationNode? east = node.East?.Sim, west = node.West?.Sim;
        for (int i = 0; i < res; i++)
        {
            int row = (i + 1) * s;
            c[row] = west?.Height(i, last) ?? c[row + 1];
            c[row + res + 1] = east?.Height(i, 0) ?? c[row + res];
        }
        c[0] = (node.North?.West ?? node.West?.North)?.Sim.Height(last, last) ?? c[s + 1];
        c[res + 1] = (node.North?.East ?? node.East?.North)?.Sim.Height(last, 0) ?? c[s + res];
        c[(res + 1) * s] = (node.South?.West ?? node.West?.South)?.Sim.Height(0, last) ?? c[res * s + 1];
        c[(res + 1) * s + res + 1] = (node.South?.East ?? node.East?.South)?.Sim.Height(0, 0) ?? c[res * s + res];

        int[] at = sample;
        float[] wn = near, wf = far;
        if (first != null)
        {
            for (int r = 0; r < s; r++)
            {
                int src = r * s, dst = r * t;
                for (int q = 0; q < first.Length; q++)
                {
                    var samples = Vector256.Create((ReadOnlySpan<float>)c.AsSpan(src + first[q], Lanes));
                    (Avx2.PermuteVar8x32(samples, pickNear![q]) * nearLanes![q] + Avx2.PermuteVar8x32(samples, pickFar![q]) * farLanes![q])
                        .CopyTo(xs.AsSpan(dst + q * Lanes, Lanes));
                }
            }
        }
        else
        {
            for (int r = 0; r < s; r++)
            {
                int src = r * s, dst = r * t;
                for (int i = 0; i < t; i++) xs[dst + i] = c[src + at[i]] * wn[i] + c[src + at[i] + 1] * wf[i];
            }
        }
        for (int j = 0; j < t; j++) Blend(xs.AsSpan(at[j] * t, t), xs.AsSpan(at[j] * t + t, t), wn[j], wf[j], h.AsSpan(j * t, t));
        return h;
    }

    // The alpha of the slot's texels times the ramps of the sides without a neighbour, and of each corner whose two
    // sides have neighbours and whose diagonal has none: the wave of the neighbours runs to that corner from both
    // sides. Two missing sides multiply, which rounds the corner between them. A node with all eight neighbours is
    // left as it is. The heights are not faded: a strong crest sinking to 0 on the strip passes the white bucket, and
    // the cut line along the edge only moved a few texels in (bench, T07.05).
    void FadeEdges(WakeNode node, int origin)
    {
        bool n = node.North == null, s = node.South == null, w = node.West == null, e = node.East == null;
        bool nw = !n && !w && (node.North!.West ?? node.West!.North) == null;
        bool ne = !n && !e && (node.North!.East ?? node.East!.North) == null;
        bool sw = !s && !w && (node.South!.West ?? node.West!.South) == null;
        bool se = !s && !e && (node.South!.East ?? node.East!.South) == null;
        if (!(n || s || w || e || nw || ne || sw || se)) return;

        int t = SlotTexels;
        float[] ramp = edgeRamp, columns = edgeColumns, corner = cornerRamp;
        for (int x = 0; x < t; x++) columns[x] = (w ? ramp[x] : 1) * (e ? ramp[t - 1 - x] : 1);
        for (int z = 0; z < t; z++)
        {
            Span<int> row = Pixels.AsSpan(origin + z * Size, t);
            float across = (n ? ramp[z] : 1) * (s ? ramp[t - 1 - z] : 1);
            if (across < 1)
            {
                for (int x = 0; x < t; x++) Fade(ref row[x], across * columns[x]);
            }
            else
            {
                for (int x = 0; x < band; x++)
                {
                    if (w) Fade(ref row[x], ramp[x]);
                    if (e) Fade(ref row[t - 1 - x], ramp[x]);
                }
            }
            int dz = z < band ? z : t - 1 - z;
            if (dz >= band) continue;
            bool left = z < band ? nw : sw, right = z < band ? ne : se;
            if (!left && !right) continue;
            for (int dx = 0; dx < band; dx++)
            {
                if (left) Fade(ref row[dx], corner[dz * band + dx]);
                if (right) Fade(ref row[t - 1 - dx], corner[dz * band + dx]);
            }
        }
    }

    static void Fade(ref int argb, float weight) => argb = (int)(((uint)argb >> 24) * weight) << 24 | argb & 0xFFFFFF;

    // into = a * wa + b * wb, lane by lane as the scalar tail.
    static void Blend(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float wa, float wb, Span<float> into)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<float> va = new(wa), vb = new(wb);
            for (; i <= into.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(a[i..]) * va + new Vector<float>(b[i..]) * vb).CopyTo(into[i..]);
            }
        }
        for (; i < into.Length; i++) into[i] = a[i] * wa + b[i] * wb;
    }

    int Origin(int slot)
    {
        var (x, z) = SlotCorner(slot);
        return z * Size + x;
    }
}
