using System.Collections.Generic;
using ImmersiveWakes.Game;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Render;

// Finds the water cells the S1 tiles lie on: the top face of still water at level 7 with no liquid above, nearest
// columns first (square rings around the player). Y is internal, the dimension offset included (DESIGN П4).
sealed class StillWaterScan
{
    // Corners in the order of the water's top face in LiquidTesselator and of the overlay quad.
    public const int NW = 0, SW = 1, SE = 2, NE = 3;

    public struct Cell
    {
        public int X, Y, Z;
        // A bit per corner. LiquidTesselator lifts a level 7 corner to the top of the block when any of the three
        // cells meeting there from the sides has liquid above it, and gives the wave bit only to the other corners.
        public int Raised;
    }

    public const int Radius = 64;
    // Rows searched for the surface around the player's block. A diving player is under it, a raft deck sits a block
    // above the water, a shore can stand a few blocks higher than the lake: the search starts above all of them and
    // takes the first fluid from the top.
    const int Above = 8, Below = 8;

    readonly List<Cell> cells = new();

    public IReadOnlyList<Cell> Cells => cells;
    public int Columns { get; private set; }

    public void Run(IBlockAccessor ba, int cx, int cy, int cz, int max)
    {
        cells.Clear();
        Columns = 0;
        Column(ba, cx, cy, cz, max);
        for (int r = 1; r <= Radius && cells.Count < max; r++)
        {
            for (int d = -r; d <= r; d++)
            {
                Column(ba, cx + d, cy, cz - r, max);
                Column(ba, cx + d, cy, cz + r, max);
            }
            for (int d = -r + 1; d < r; d++)
            {
                Column(ba, cx - r, cy, cz + d, max);
                Column(ba, cx + r, cy, cz + d, max);
            }
        }
    }

    void Column(IBlockAccessor ba, int x, int cy, int z, int max)
    {
        if (cells.Count >= max) return;
        Columns++;
        for (int y = cy + Above; y >= cy - Below; y--)
        {
            Block fluid = ba.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid);
            if (fluid.Id == 0) continue;
            // The first block of the fluid layer from above decides the column: lake ice takes the place of the
            // water in that layer, and the water under it gets no tile.
            if (IsStillWater7(fluid) && !IsLiquid(ba, x, y + 1, z))
            {
                cells.Add(new Cell { X = x, Y = y, Z = z, Raised = RaisedCorners(ba, x, y, z) });
            }
            return;
        }
    }

    // The fluid a node lives on.
    public static bool IsStillWater7(Block b) => WaterProbe.IsSurfaceFluid(b);

    // The tesselator compares against any liquid, not the same one (ChunkTesselator fills isLiquidBlock by MatterState).
    public static bool IsLiquid(IBlockAccessor ba, int x, int y, int z) =>
        ba.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid).MatterState == EnumMatterState.Liquid;

    public static int RaisedCorners(IBlockAccessor ba, int x, int y, int z)
    {
        int up = y + 1;
        bool n = IsLiquid(ba, x, up, z - 1);
        bool s = IsLiquid(ba, x, up, z + 1);
        bool w = IsLiquid(ba, x - 1, up, z);
        bool e = IsLiquid(ba, x + 1, up, z);
        int raised = 0;
        if (n || w || IsLiquid(ba, x - 1, up, z - 1)) raised |= 1 << NW;
        if (s || w || IsLiquid(ba, x - 1, up, z + 1)) raised |= 1 << SW;
        if (s || e || IsLiquid(ba, x + 1, up, z + 1)) raised |= 1 << SE;
        if (n || e || IsLiquid(ba, x + 1, up, z - 1)) raised |= 1 << NE;
        return raised;
    }
}
