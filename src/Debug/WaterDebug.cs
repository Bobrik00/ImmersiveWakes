using System;
using System.Text;
using ImmersiveWakes.Game;
using ImmersiveWakes.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using static System.FormattableString;

namespace ImmersiveWakes.Debug;

// ".wakes debug water": the flags the wake's quads give the water's top face (QuadMesh.ReadCorners, OceanFlags), for
// the cell the look meets or for the column under the player, which an autorun can ask for without aiming. 'here' adds
// the oceanity of the cells around the player on the sea row, where the shore cuts it to 0.
static class WaterDebug
{
    const double Reach = 64, RayStep = 1 / 32.0;
    // Rows searched for the first fluid from the top around the player's block, as in DebugSplash.
    const int Above = 8, Below = 16;
    const int GridRadius = 4;

    // The ray of the block selection, as DebugSplash.AtLook marches it, up to the first block with a fluid of any kind.
    public static string AtLook(ICoreClientAPI capi)
    {
        EntityPlayer player = capi.World.Player.Entity;
        Vec3d eye = player.Pos.XYZ.Add(player.LocalEyePos);
        Vec3f dir = EntityPos.GetViewVector(player.Pos.Pitch, player.Pos.Yaw);
        IBlockAccessor blocks = capi.World.BlockAccessor;
        int lastX = int.MinValue, lastY = int.MinValue, lastZ = int.MinValue;
        for (double d = 0; d <= Reach; d += RayStep)
        {
            double x = eye.X + dir.X * d, y = eye.Y + dir.Y * d, z = eye.Z + dir.Z * d;
            int bx = (int)Math.Floor(x), by = (int)Math.Floor(y), bz = (int)Math.Floor(z);
            if (bx == lastX && by == lastY && bz == lastZ) continue;
            lastX = bx;
            lastY = by;
            lastZ = bz;
            // The fluid first: a water plant stands in the solid layer of a water block.
            if (blocks.GetBlockRaw(bx, by, bz, BlockLayersAccess.Fluid).Id != 0) return Cell(capi, bx, by, bz, x, z);
            Block solid = blocks.GetBlockRaw(bx, by, bz, BlockLayersAccess.Solid);
            if (solid.Id != 0 && solid.CollisionBoxes is { Length: > 0 }) return $"water: the look meets {solid.Code} at {bx} {by} {bz} before any fluid";
        }
        return $"water: no fluid within {Reach} blocks of the look";
    }

    // The first fluid from the top in the player's column, then the grid around it on the sea row of the player's
    // dimension.
    public static string Here(ICoreClientAPI capi)
    {
        IBlockAccessor blocks = capi.World.BlockAccessor;
        FastVec3d pos = capi.World.Player.Entity.Pos.XYZFast;
        int cx = (int)Math.Floor(pos.X), cy = (int)Math.Floor(pos.Y), cz = (int)Math.Floor(pos.Z);
        var sb = new StringBuilder();
        int y = cy + Above;
        while (y >= cy - Below && blocks.GetBlockRaw(cx, y, cz, BlockLayersAccess.Fluid).Id == 0) y--;
        sb.Append(y >= cy - Below
            ? Cell(capi, cx, y, cz, pos.X, pos.Z)
            : $"water: no fluid in the column {cx} {cz} from y {cy + Above} down to {cy - Below}");
        int seaRow = cy - cy % BlockPos.DimensionBoundary + capi.World.SeaLevel - 1;
        return sb.Append('\n').Append(Grid(blocks, capi.World.SeaLevel, cx, seaRow, cz)).ToString();
    }

    // The cell's fluid, the ocean map of its chunk and the corners as the wake's quad would carry them; the anchor is the
    // point the look met or the player stands over.
    static string Cell(ICoreClientAPI capi, int x, int y, int z, double anchorX, double anchorZ)
    {
        IBlockAccessor blocks = capi.World.BlockAccessor;
        int seaLevel = capi.World.SeaLevel;
        Block fluid = blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid);
        bool surface = WaterProbe.IsSurfaceFluid(fluid)
            && blocks.GetBlockRaw(x, y + 1, z, BlockLayersAccess.Fluid).MatterState != EnumMatterState.Liquid;
        var sb = new StringBuilder($"water: {fluid.Code} at {x} {y} {z}, logical y {y % BlockPos.DimensionBoundary}, " +
                                   $"sea row {seaLevel - 1}; the wake {(surface ? "takes" : "does not take")} this cell");

        int chunkX = x >> 5, chunkZ = z >> 5, perRegion = blocks.RegionSize / OceanFlags.ChunkSize;
        Span<float> map = stackalloc float[4];
        sb.Append($"\nchunk {chunkX} {chunkZ} of region {chunkX / perRegion} {chunkZ / perRegion}: ");
        sb.Append(OceanFlags.ChunkCorners(blocks, chunkX, chunkZ, map)
            ? Invariant($"ocean map at the chunk corners TL TR BL BR {map[0]:0.##} {map[1]:0.##} {map[2]:0.##} {map[3]:0.##}")
            : "no ocean map loaded");

        Span<int> light = stackalloc int[4];
        int raised = QuadMesh.ReadCorners(blocks, x, y, z, light);
        int same = OceanFlags.SameFluidCorners(blocks, fluid, x, y, z);
        int ocean = OceanFlags.Corners(blocks, seaLevel, x, y, z);
        string[] names = ["NW", "SW", "SE", "NE"];
        for (int k = 0; k < 4; k++)
        {
            bool up = (raised & (1 << k)) != 0;
            int oceanity = (ocean >> (8 * k)) & 0xFF;
            int flags = (up ? 0 : 1) | oceanity << 2;
            string wave = up ? "raised, no wave" : Invariant($"wave divisor {OceanFlags.Divisor(oceanity / 255f):0.00}");
            sb.Append($"\n{names[k]}: same fluid around {((same & (1 << k)) != 0 ? "yes" : "no")}, oceanity {oceanity}, {wave}, quad flags 0x{flags:X}");
        }
        float weight = OceanFlags.AnchorWave(blocks, seaLevel, anchorX, y, anchorZ);
        sb.Append(Invariant($"\nanchor at {anchorX:0.00} {anchorZ:0.00}: wave weight {weight:0.000}") +
                  (weight > 0 ? Invariant($", divisor {1 / weight:0.00}") : ", no wave"));
        return sb.ToString();
    }

    // The NW corner's oceanity of every cell within GridRadius, rows north to south; # for a cell without fluid.
    static string Grid(IBlockAccessor blocks, int seaLevel, int cx, int y, int cz)
    {
        var sb = new StringBuilder($"NW corner oceanity on the sea row y {y}, x {cx - GridRadius}..{cx + GridRadius} across, " +
                                   $"z {cz - GridRadius}..{cz + GridRadius} down, # without fluid:");
        for (int z = cz - GridRadius; z <= cz + GridRadius; z++)
        {
            sb.Append('\n');
            for (int x = cx - GridRadius; x <= cx + GridRadius; x++)
            {
                string cell = blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid).Id == 0
                    ? "#"
                    : (OceanFlags.Corners(blocks, seaLevel, x, y, z) & 0xFF).ToString();
                sb.Append(cell.PadLeft(4));
            }
        }
        return sb.ToString();
    }
}
