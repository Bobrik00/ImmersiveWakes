using System;
using System.Collections.Generic;
using ImmersiveWakes.Game;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render;

// The S1 tiles of ".wakes tiles": static quads over still water with a synthetic ring wave in the bucket colours, kept
// as a debug aid: T07.02 picked the emission point next to their white crest, T08.02 holds one against Coriaender.
// They have a mesh and a texture of their own: the core hands out the wake atlas's slots, and the tiles switch between
// 16 and 32 texels per block (.wakes res).
// Mesh, texture and pixels are made by the first tiles laid, so a world without tiles does not pay the 16 MiB of
// texture and 16 MiB of pixels.
sealed class S1Tiles : IDisposable
{
    public const int Capacity = 1000;

    // The wave moves a corner by waterWaveIntensity / div plus a wind term, div 5 on a lake and down to 1 on the open
    // sea (FINDINGS "Волна"): under half a block on a lake, about 1.8 blocks at sea at wind 1, and up to about 2.4 in a
    // storm with the player high over the sea, where the surface wind reaches 1.5 (WeatherSimulationRegion.GetWindSpeed).
    const double WaveMargin = 3;

    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly StillWaterScan scan = new();
    readonly int[] light = new int[4];
    int[] pixels = [];
    // Where the tiles were laid from: the centre of the synthetic ring, kept for a redraw on res and tint.
    double ringX, ringZ;

    public S1Tiles(ICoreClientAPI capi, ILogger log)
    {
        this.capi = capi;
        this.log = log;
    }

    public QuadMesh? Mesh { get; private set; }
    public WakeTexture? Texture { get; private set; }
    public int Count => Mesh?.QuadCount ?? 0;
    public int Res { get; private set; } = 32;
    public S1Tint Tint { get; private set; } = S1Tint.Bare;
    // Filter for the texture to come, and the state of the one there is.
    public bool Mipmaps { get; private set; } = true;
    public IReadOnlyList<StillWaterScan.Cell> Cells => scan.Cells;
    public int Columns => scan.Columns;
    // Sphere around the quads as laid, absolute, for the renderer's own frustum and distance gate. The wave moves the
    // corners after this, so it is widened by WaveMargin.
    public Vec3d BoundsCenter { get; } = new();
    public double BoundsRadius { get; private set; }

    // Quad i takes slot i: column i % 64, row i / 64. Corners are read as the wake's are (QuadMesh.ReadCorners), so the
    // check of .wakes tiles, which holds the mesh against StillWaterScan, checks the wake's rule too. Light is baked now,
    // and the tiles stay as they are until the next call. Returns the number laid.
    public int Lay(IBlockAccessor blocks, FastVec3d at, int asked)
    {
        int cx = (int)Math.Floor(at.X), cy = (int)Math.Floor(at.Y), cz = (int)Math.Floor(at.Z);
        scan.Run(blocks, cx, cy, cz, asked);
        Mesh ??= new QuadMesh(capi, Capacity);
        int quads = Math.Min(scan.Cells.Count, Capacity);
        Mesh.Origin.Set(cx, cy, cz);
        float side = 1f / WakeTexture.Slots;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int q = 0; q < quads; q++)
        {
            StillWaterScan.Cell c = scan.Cells[q];
            int raised = QuadMesh.ReadCorners(blocks, c.X, c.Y, c.Z, light);
            int ocean = OceanFlags.Corners(blocks, capi.World.SeaLevel, c.X, c.Y, c.Z);
            Mesh.SetQuad(q, c.X - cx, c.Y - cy, c.Z - cz, q % WakeTexture.Slots * side, q / WakeTexture.Slots * side, side, raised, ocean, light);
            minX = Math.Min(minX, c.X);
            minY = Math.Min(minY, c.Y);
            minZ = Math.Min(minZ, c.Z);
            maxX = Math.Max(maxX, c.X + 1);
            maxY = Math.Max(maxY, c.Y + 1);
            maxZ = Math.Max(maxZ, c.Z + 1);
        }
        if (quads > 0)
        {
            BoundsCenter.Set((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
            BoundsRadius = Math.Sqrt(Sq(maxX - minX) + Sq(maxY - minY) + Sq(maxZ - minZ)) / 2 + WaveMargin;
        }
        Mesh.Commit(quads);
        ringX = at.X;
        ringZ = at.Z;
        Redraw();
        return quads;
    }

    static double Sq(double d) => d * d;

    public void Clear() => Mesh?.Commit(0);

    // Makes a new texture at once only when there is one to replace.
    public void SetResolution(int res)
    {
        Res = res;
        if (Texture == null) return;
        Texture.SetResolution(res);
        Redraw();
    }

    public void SetTint(S1Tint tint)
    {
        Tint = tint;
        if (Texture != null) Redraw();
    }

    public void SetMipmaps(bool on)
    {
        Mipmaps = on;
        Texture?.SetMipmaps(on);
    }

    // The tiles on the mesh are the first Count cells of the last scan.
    void Redraw()
    {
        Texture ??= new WakeTexture(capi, log, "S1 tile texture", Res, Mipmaps);
        int size = Texture.Size;
        if (pixels.Length != size * size) pixels = new int[size * size];
        int count = Count;
        S1TileGenerator.Draw(pixels, Res, capi.World, scan.Cells, count, ringX, ringZ, Tint);
        int rows = Math.Min(size, (count + WakeTexture.Slots - 1) / WakeTexture.Slots * Res);
        Texture.Upload(pixels, 0, rows);
    }

    // The sphere of the tiles in the frustum and, measured horizontally as the chunks are culled, within reach.
    public bool InView(FrustumCulling culler, Vec3d camera, double reach)
    {
        if (Count == 0 || Texture == null || Mesh?.Ref == null) return false;
        Vec3d c = BoundsCenter;
        double r = BoundsRadius;
        if (!culler.SphereInFrustum(c.X, c.Y, c.Z, r)) return false;
        double dx = c.X - camera.X, dz = c.Z - camera.Z, limit = reach + r;
        return dx * dx + dz * dz <= limit * limit;
    }

    public void Dispose()
    {
        Mesh?.Dispose();
        Mesh = null;
        Texture?.Dispose();
        Texture = null;
    }
}
