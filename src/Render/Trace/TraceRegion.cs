using System;
using System.Globalization;
using System.Linq;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;

namespace ImmersiveWakes.Render.Trace;

// The foam's region in the vanilla entity atlas (AtlasRegion): the slots of the core's atlas (WakeAtlasCpu), 64 a row
// of 32 texels each, laid out as in the wake texture, so slot s sits at column s % 64 and row s / 64 of the region.
//
// The height is the slot rows of the node limit rounded up to a power of two, so that the copy of rows [0, H), whose H
// is rounded the same way, never leaves the region: H past it would overwrite other entities' textures in the raster and
// in the tracer (refute-cost, section 3). A core rebuilt later with a higher limit keeps this region; its nodes past it
// are not traced.
sealed class TraceRegion : IDisposable
{
    public const int Texels = WakeAtlasCpu.GameTexels, Columns = WakeAtlasCpu.SlotsPerRow;
    public const int Width = Columns * Texels;
    // 64 rows hold every slot of the core's atlas.
    const int MaxRows = WakeAtlasCpu.SlotsPerRow;

    readonly ICoreClientAPI capi;
    readonly AtlasRegion area;

    TraceRegion(ICoreClientAPI capi, AtlasRegion area, int rows)
    {
        this.capi = capi;
        this.area = area;
        Rows = rows;
    }

    public int SubId => area.SubId;
    // Slot rows; the region holds Rows * 64 slots.
    public int Rows { get; }
    public int Height => Rows * Texels;
    public int Slots => Rows * Columns;
    public int PageTextureId => area.PageTextureId;
    public int PageWidth => area.PageWidth;
    public int PageHeight => area.PageHeight;

    // Slot rows for a node limit: at least one, a power of two, all of them past 2048 nodes.
    public static int RowsFor(int maxNodes)
    {
        int rows = Math.Clamp((maxNodes + Columns - 1) / Columns, 1, MaxRows);
        return (int)Math.Min(MaxRows, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)rows));
    }

    // Slot rows to copy for the slots in use below top: a power of two, so that the rectangles vspt collects for the
    // page stay few, and never past the region. EntityLink.OnTextureWritten collects them until a frame it sends in
    // (under the loading cover Skip is up and nothing is sent), and the 65th different one marks the whole page for a
    // full send with a new bake of its materials.
    public int CopyRows(int top)
    {
        int rows = Math.Clamp((top + Columns - 1) / Columns, 1, Rows);
        return (int)Math.Min(Rows, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)rows));
    }

    // Null with the reason when no region is taken; nothing is taken then. The composition of the atlas must have
    // ended (AtlasSpace.Composed).
    public static TraceRegion? Take(ICoreClientAPI capi, int rows, out string why)
    {
        AtlasRegion? area = AtlasRegion.Take(capi, Width, rows * Texels, out why);
        return area == null ? null : new TraceRegion(capi, area, rows);
    }

    // vspt's bake makes an entry of every position that is not null; its line "takes authored materials: N of M
    // entries" at the world's entry counts this region in M when the region was there in time.
    public string Describe() => area.Describe() +
        string.Create(CultureInfo.InvariantCulture, $"; the atlas has {capi.EntityTextureAtlas.Positions.Count(p => p != null)} positions");

    // The page's texture coordinates of the corner of slot s at -X -Z; v grows with z on the quads, as in the wake
    // texture, and the copy keeps the rows' order.
    public (float U, float V) SlotCorner(int slot) => area.Uv(slot % Columns * Texels, slot / Columns * Texels);

    // Rows [0, rows * 32) of source into the region (AtlasRegion.Copy).
    public void Copy(LoadedTexture source, int rows) => area.Copy(source, Width, rows * Texels);

    public void Dispose() => area.Dispose();
}
