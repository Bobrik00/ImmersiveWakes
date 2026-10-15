using System;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// Where a boat type's water mask lies seen from above (T11.05), in the boat's own axes: the plates of its
// hidewatersurface element, tessellated as EntityBehaviorHideWaterSurface.OnTesselated does and turned as
// EntityShapeRenderer.loadModelMatrix turns the entity, as a mask of TexelsPerBlock texels a block. In the raster the
// plates are a depth mask in front of the water, the foam and the clouds (FINDINGS "Ванильные лодки"); vspt builds its
// water mask from the same element (EntityLink.WaterMaskMesh) and hides only the water with it. The sailed boat's
// plates lie at 0.625 and 0.875 above Pos, inside the hull at the waterline: 0.19 from the outer planks along the sides
// and 0.12-0.25 at the bow and the stern (reference/wf3/verify-platform-perf/shapepos.py's transform, 2026-10-15).
// The collision boxes would mask 2.8 x 8.8 where the plates cover 2.5 x 7.6, and the reed boats of Shipwright mask
// only their middle: their raised ends show the water under them in both frames.
//
// A local point (x, y, z) lies at Pos + RotateY(yaw + Turn)(x, y, z) in the world, with RotateY as Mat4f's:
// (x cos + z sin, y, -x sin + z cos); x runs along the boat, the sailed boat's bow at -5.31, and z across it. Local is
// the tessellated model less (0.5, 0, 0.5), times the entity's size. The tilts EntityBoat gives the renderer (zangle:
// the sway and the forward pitch, 4.3 degrees under sail at 6.9 b/s; xangle: the sway and the lean into a turn, 5-6
// degrees) move the plates sideways by 0.06 at most about the pivot at half the selection box, and the foam's mask
// leaves them out; they lift the bow's plates by up to 0.4, which TraceHulls.Deck adds for the clouds. A shape turned
// about X or Z would not be a turn about the vertical, and such a type gets no mask.
sealed class HullFootprint
{
    public const int TexelsPerBlock = 32;
    // Past this the type gets no mask: 32 blocks by 32, longer than any boat known (drakkar 18.8).
    const int MaxTexels = 1024;

    HullFootprint(AssetLocation code, float turn, float minX, float minZ, int width, int height, byte[] mask, float[] top, float plateTop,
        Vec3f probe, Vec3f probeLocal)
    {
        Code = code;
        Turn = turn;
        MinX = minX;
        MinZ = minZ;
        Width = width;
        Height = height;
        Mask = mask;
        Top = top;
        PlateTop = plateTop;
        Probe = probe;
        ProbeLocal = probeLocal;
    }

    public AssetLocation Code { get; }
    // Radians added to the yaw: the shape's rotateY plus 90 degrees, as loadModelMatrix adds them.
    public float Turn { get; }
    // The mask's corner at the least x and z, and its size in texels: x along the boat, z across it.
    public float MinX { get; }
    public float MinZ { get; }
    public int Width { get; }
    public int Height { get; }
    public float MaxX => MinX + (float)Width / TexelsPerBlock;
    public float MaxZ => MinZ + (float)Height / TexelsPerBlock;
    // 255 for a texel whose centre lies under a plate, by rows of z.
    public byte[] Mask { get; }
    // The height above Pos of the highest plate over each texel, NaN off the mask.
    public float[] Top { get; }
    public float PlateTop { get; }
    // A vertex of the plates in the model's space and in the local axes: the check of the turn against the renderer's
    // ModelMat in .wakes vspt.
    public Vec3f Probe { get; }
    public Vec3f ProbeLocal { get; }
    // How far the still surface stands above Pos when a boat of the type floats, learnt from the first one seen on the
    // surface; ghost boats, which have no Pos, take their height from it.
    public double? Draft { get; set; }
    // The corner of the mask in TraceHulls' mask texture, -1 until it has a place there.
    public int CellX { get; set; } = -1;
    public int CellY { get; set; } = -1;

    public int Texels => Mask.Length;

    // The hideWaterElement of the entity's hidewatersurface behaviour, null without one.
    public static string? MaskElement(Entity entity)
    {
        JsonObject[]? behaviors = entity.Properties?.Client?.BehaviorsAsJsonObj;
        if (behaviors == null) return null;
        foreach (JsonObject b in behaviors)
        {
            if (b?["code"].AsString() == "hidewatersurface") return b["hideWaterElement"].AsString();
        }
        return null;
    }

    // What the footprint depends on besides the element: the types of one boat in other woods share it. Null without a
    // shape.
    public static string? ShapeKey(Entity entity, string element)
    {
        EntityClientProperties? client = entity.Properties?.Client;
        if (client?.Shape is not { } c) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"{c.Base}|{element}|{c.rotateX},{c.rotateY},{c.rotateZ}|{c.offsetX},{c.offsetY},{c.offsetZ}|{client.Size}|{c.QuantityElements}");
    }

    // Null with the reason; retry tells whether a later call may build it (the shape not loaded yet).
    public static HullFootprint? Build(ICoreClientAPI capi, Entity entity, string element, out string why, out bool retry)
    {
        retry = false;
        EntityClientProperties? client = entity.Properties?.Client;
        CompositeShape? composite = client?.Shape;
        if (client == null || composite == null)
        {
            why = "it has no shape";
            return null;
        }
        // Set in Entity.Initialize; a type whose shape failed to load keeps it null.
        if (client.LoadedShapeForEntity is not { } shape)
        {
            why = "its shape is not loaded";
            retry = true;
            return null;
        }
        if (Math.Abs(composite.rotateX) > 0.01f || Math.Abs(composite.rotateZ) > 0.01f)
        {
            why = $"its shape is turned about X or Z ({composite.rotateX}, {composite.rotateZ})";
            return null;
        }
        var meta = new TesselationMetaData
        {
            QuantityElements = composite.QuantityElements,
            SelectiveElements = [element],
            TexSource = new AnyTexture(),
            TypeForLogging = "entity",
            Rotation = new Vec3f(composite.rotateX, composite.rotateY, composite.rotateZ),
        };
        capi.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
        mesh.Translate(composite.offsetX, composite.offsetY, composite.offsetZ);
        if (mesh.VerticesCount < 3 || mesh.IndicesCount < 3)
        {
            why = $"its element {element} has no faces";
            return null;
        }
        float size = client.Size;
        float[] xyz = mesh.xyz;
        int vertices = mesh.VerticesCount;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, plateTop = float.MinValue;
        var local = new float[vertices * 3];
        for (int v = 0; v < vertices; v++)
        {
            float x = (xyz[v * 3] - 0.5f) * size, y = xyz[v * 3 + 1] * size, z = (xyz[v * 3 + 2] - 0.5f) * size;
            local[v * 3] = x;
            local[v * 3 + 1] = y;
            local[v * 3 + 2] = z;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
            plateTop = Math.Max(plateTop, y);
        }
        int x0 = (int)Math.Floor(minX * TexelsPerBlock), z0 = (int)Math.Floor(minZ * TexelsPerBlock);
        int width = (int)Math.Ceiling(maxX * TexelsPerBlock) - x0, height = (int)Math.Ceiling(maxZ * TexelsPerBlock) - z0;
        if (width < 1 || height < 1 || width > MaxTexels || height > MaxTexels)
        {
            why = $"its mask would be {width} x {height} texels";
            return null;
        }
        var mask = new byte[width * height];
        var top = new float[width * height];
        Array.Fill(top, float.NaN);
        int[] indices = mesh.Indices;
        for (int i = 0; i + 2 < mesh.IndicesCount; i += 3)
        {
            Fill(local, indices[i], indices[i + 1], indices[i + 2], x0, z0, width, height, mask, top);
        }
        float firstX = xyz[0], firstY = xyz[1], firstZ = xyz[2];
        why = "";
        return new HullFootprint(entity.Code, (composite.rotateY + 90) * GameMath.DEG2RAD, (float)x0 / TexelsPerBlock, (float)z0 / TexelsPerBlock,
            width, height, mask, top, plateTop, new Vec3f(firstX, firstY, firstZ), new Vec3f(local[0], local[1], local[2]));
    }

    // The texels whose centres lie inside the triangle seen from above, edges included; a face seen edge on covers none.
    static void Fill(float[] p, int a, int b, int c, int x0, int z0, int width, int height, byte[] mask, float[] top)
    {
        float ax = p[a * 3], az = p[a * 3 + 2], bx = p[b * 3], bz = p[b * 3 + 2], cx = p[c * 3], cz = p[c * 3 + 2];
        float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
        if (Math.Abs(area) < 1e-6f) return;
        float y = Math.Max(p[a * 3 + 1], Math.Max(p[b * 3 + 1], p[c * 3 + 1]));
        const float t = TexelsPerBlock;
        int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)) * t) - x0);
        int i1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)) * t) - x0);
        int j0 = Math.Max(0, (int)Math.Floor(Math.Min(az, Math.Min(bz, cz)) * t) - z0);
        int j1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(az, Math.Max(bz, cz)) * t) - z0);
        float sign = Math.Sign(area), eps = 1e-5f * Math.Abs(area);
        for (int j = j0; j <= j1; j++)
        {
            float z = (z0 + j + 0.5f) / t;
            for (int i = i0; i <= i1; i++)
            {
                float x = (x0 + i + 0.5f) / t;
                float e0 = ((bx - ax) * (z - az) - (bz - az) * (x - ax)) * sign;
                float e1 = ((cx - bx) * (z - bz) - (cz - bz) * (x - bx)) * sign;
                float e2 = ((ax - cx) * (z - cz) - (az - cz) * (x - cx)) * sign;
                if (e0 < -eps || e1 < -eps || e2 < -eps) continue;
                int k = j * width + i;
                mask[k] = 255;
                if (!(top[k] >= y)) top[k] = y;
            }
        }
    }

    // The texel under a local point, -1 off the mask.
    public int TexelAt(double x, double z)
    {
        int i = (int)Math.Floor((x - MinX) * TexelsPerBlock), j = (int)Math.Floor((z - MinZ) * TexelsPerBlock);
        if ((uint)i >= (uint)Width || (uint)j >= (uint)Height) return -1;
        int k = j * Width + i;
        return Mask[k] != 0 ? k : -1;
    }

    // The tessellator asks the source for every face's texture; the plates' are #null and nothing samples them.
    sealed class AnyTexture : ITexPositionSource
    {
        static readonly TextureAtlasPosition Whole = new() { x1 = 0, y1 = 0, x2 = 1, y2 = 1 };
        public Size2i AtlasSize { get; } = new(2048, 2048);
        public TextureAtlasPosition this[string textureCode] => Whole;
    }
}
