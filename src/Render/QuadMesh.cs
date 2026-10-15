using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render;

// Quads over the top face of still water, one per block, in the vertex layout of wakessurface.vsh: xyz relative to
// Origin, uv, rgba with the corner light, flags with waterFlagsIn. The wake's nodes and the S1 tiles both lie on it.
// One UploadMesh at full capacity, then only UpdateMesh: AllocateEmptyMesh would give an SSBO mesh without positions
// (vs-allocateemptymesh-is-ssbo). UpdateMesh checks no bounds, so the quad count is clamped here.
sealed class QuadMesh : IDisposable
{
    // Level 7 water stands at 7/8 of the block, a raised corner at the top (FINDINGS "Тесселяция и высота
    // поверхности"). The lift above the water is the uniform wakesLift of wakessurface.vsh (WakeLook.DefaultLift),
    // which .wakes look lift retunes on the fly.
    public const float SurfaceHeight = 0.875f;

    // How far the texture coordinates stay inside the square of a quad: 1/64 of a texel of an atlas 2048 wide, an exact
    // binary fraction. A fragment whose centre falls on the quad's edge would otherwise read the first texel of the next
    // slot, which holds another node, and at mip level 5 the whole slot is one texel.
    const float UvInset = 1f / (1 << 17);

    // Corner offsets in the order NW, SW, SE, NE of the water's top face.
    public static ReadOnlySpan<int> CornerDx => new[] { 0, 0, 1, 1 };
    public static ReadOnlySpan<int> CornerDz => new[] { 0, 1, 1, 0 };

    readonly ICoreClientAPI capi;
    readonly MeshData data;
    MeshRef? meshRef;

    public QuadMesh(ICoreClientAPI capi, int capacity)
    {
        this.capi = capi;
        Capacity = Math.Max(1, capacity);
        data = new MeshData(Capacity * 4, Capacity * 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
        // With the defaults (true) UploadMesh asks for STATIC_DRAW.
        data.XyzStatic = data.UvStatic = data.RgbaStatic = data.FlagsStatic = data.IndicesStatic = false;
        FillIndices(data.Indices, Capacity);
        // UploadMesh sizes the buffers by the counts, not by the array lengths.
        data.VerticesCount = Capacity * 4;
        data.IndicesCount = Capacity * 6;
        meshRef = capi.Render.UploadMesh(data);
        // The upload left every index to draw.
        Commit(0);
    }

    // Two triangles per quad with the diagonal NW-SE, as the water's top face; 6 indices for each 4 vertices.
    public static void FillIndices(int[] indices, int quads)
    {
        for (int q = 0; q < quads; q++)
        {
            int v = q * 4, i = q * 6;
            indices[i] = v;
            indices[i + 1] = v + 1;
            indices[i + 2] = v + 2;
            indices[i + 3] = v;
            indices[i + 4] = v + 2;
            indices[i + 5] = v + 3;
        }
    }

    public int Capacity { get; }
    public MeshRef? Ref => meshRef;
    public int QuadCount { get; private set; }
    // Absolute position the vertices are relative to, the origin of OverlayPrograms.SetLayer; internal y.
    public Vec3d Origin { get; } = new();
    public int Updates { get; private set; }

    public ReadOnlySpan<float> Xyz => data.xyz.AsSpan(0, QuadCount * 12);
    public ReadOnlySpan<int> Flags => data.Flags.AsSpan(0, QuadCount * 4);

    // Quad q over block (x, y, z), relative to Origin, textured by the square of side `side` from (u0, v0), u along +X
    // and v along +Z. raised has a bit per corner lifted to the top of the block, ocean the corners' oceanity a byte each
    // in corner order (OceanFlags.Corners), light the corners' light ints in corner order (GetLightRGBsAsInt, written
    // byte by byte as chunk meshes write theirs). Takes effect on Commit.
    public void SetQuad(int q, int x, int y, int z, float u0, float v0, float side, int raised, int ocean, ReadOnlySpan<int> light)
    {
        if ((uint)q >= (uint)Capacity) throw new ArgumentOutOfRangeException(nameof(q), q, $"the mesh holds {Capacity} quads");
        float[] xyz = data.xyz, uv = data.Uv;
        byte[] rgba = data.Rgba;
        int[] flags = data.Flags;
        for (int k = 0; k < 4; k++)
        {
            int v = q * 4 + k, dx = CornerDx[k], dz = CornerDz[k];
            bool up = (raised & (1 << k)) != 0;
            xyz[v * 3] = x + dx;
            xyz[v * 3 + 1] = y + (up ? 1f : SurfaceHeight);
            xyz[v * 3 + 2] = z + dz;
            uv[v * 2] = u0 + (dx == 0 ? UvInset : side - UvInset);
            uv[v * 2 + 1] = v0 + (dz == 0 ? UvInset : side - UvInset);
            int l = light[k];
            rgba[v * 4] = (byte)l;
            rgba[v * 4 + 1] = (byte)(l >> 8);
            rgba[v * 4 + 2] = (byte)(l >> 16);
            rgba[v * 4 + 3] = (byte)(l >> 24);
            // Bit 0 of waterFlagsIn: the corner moves with the wave. The tesselator gives it only to corners it does
            // not raise, and the oceanity in bits 2-9 to every corner.
            flags[v] = (up ? 0 : 1) | ((ocean >> (8 * k)) & 0xFF) << 2;
        }
    }

    // The corners of the quad over block (x, y, z): blocks x - 1 .. x + 1 and z - 1 .. z + 1 of layer y + 1, read once
    // each, and corner (dx, dz) takes the four of them at x + dx - 1 .. x + dx, z + dz - 1 .. z + dz. The water's top
    // face takes its light from the blocks above it, per corner when smooth lighting is on (LiquidTesselator,
    // CalcBlockFaceLight with the block above; vs-vertex-light-int-layout); light gets the four blocks' mean per byte,
    // without the tesselator's ambient occlusion. Returns a bit per corner the tesselator raises to the top of the block:
    // one of the four blocks holds a liquid of any kind, as StillWaterScan.RaisedCorners has it (the block over the cell
    // itself is never liquid on a surface the wake takes).
    public static int ReadCorners(IBlockAccessor blocks, int x, int y, int z, Span<int> light)
    {
        Span<int> cells = stackalloc int[9];
        Span<bool> liquid = stackalloc bool[9];
        int up = y + 1;
        for (int j = 0; j < 3; j++)
        {
            for (int i = 0; i < 3; i++)
            {
                int bx = x - 1 + i, bz = z - 1 + j;
                cells[j * 3 + i] = blocks.GetLightRGBsAsInt(bx, up, bz);
                liquid[j * 3 + i] = blocks.GetBlockRaw(bx, up, bz, BlockLayersAccess.Fluid).MatterState == EnumMatterState.Liquid;
            }
        }

        int raised = 0;
        for (int k = 0; k < 4; k++)
        {
            int dx = CornerDx[k], dz = CornerDz[k];
            int c0 = 0, c1 = 0, c2 = 0, c3 = 0;
            bool lifted = false;
            for (int b = 0; b < 2; b++)
            {
                for (int a = 0; a < 2; a++)
                {
                    int cell = (dz + b) * 3 + dx + a;
                    int l = cells[cell];
                    c0 += l & 0xFF;
                    c1 += (l >> 8) & 0xFF;
                    c2 += (l >> 16) & 0xFF;
                    c3 += (l >> 24) & 0xFF;
                    lifted |= liquid[cell];
                }
            }
            light[k] = (c0 / 4) | (c1 / 4) << 8 | (c2 / 4) << 16 | (c3 / 4) << 24;
            if (lifted) raised |= 1 << k;
        }
        return raised;
    }

    // Uploads the first `quads` quads; the rest are not drawn.
    public void Commit(int quads)
    {
        if (meshRef == null) return;
        QuadCount = Math.Clamp(quads, 0, Capacity);
        data.VerticesCount = QuadCount * 4;
        // UpdateMesh takes the new count only while Indices is not null.
        data.IndicesCount = QuadCount * 6;
        capi.Render.UpdateMesh(meshRef, data);
        Updates++;
    }

    public void Dispose()
    {
        meshRef?.Dispose();
        meshRef = null;
        QuadCount = 0;
    }
}
