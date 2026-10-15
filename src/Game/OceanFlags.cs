using System;
using ImmersiveWakes.Render;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Game;

// Oceanity of the water's top face by corner, as ChunkTesselator bakes it into bits 2-9 of waterFlagsIn and
// chunkliquid.vsh divides the wave by 5 * max(0.2, 1 - oceanity): on the open sea the water waves five times higher
// than on a lake (FINDINGS "Тесселяция и высота поверхности", "Волна"). Only a cell on the row seaLevel - 1 gets it,
// by its logical y as the tesselator takes it, so a copy of the sea in dimension 2 waves as the sea of dimension 0 at
// the same x and z. A corner gets it when the two side cells and the diagonal cell meeting there hold the very same
// fluid block as the cell, and its value is lerped from the four values the ocean map gives the chunk of 32 x 32
// blocks (ClientWorldMap.LoadOceanityCorners), then cut to a byte. The weak wave bit (29) comes only from liquids in
// containers, no world tesselator sets it, so the surface never carries it.
//
// The values come out as the engine's to the bit: the same float operations on the same map, read from the world as it
// is now. The engine's mesh can differ for a while in two cases. Where the map region is not loaded, the tesselator
// keeps the corners of the chunk it did before; here the oceanity is 0. A diagonal neighbour chunk that arrives after
// the chunk was tesselated does not make it tesselate again, so a corner cell may keep the chunk-corner rule below for
// air that is land by now.
//
// The accessor must come from BlockAccessorBase (capi.World.BlockAccessor, GetBlockAccessor): the lock-free one throws
// NotImplementedException on GetMapRegion.
static class OceanFlags
{
    public const int ChunkSize = 32;

    // Oceanity 0..255 of the corners NW, SW, SE, NE of the water in block (x, y, z), a byte each from the low one; 0
    // off the sea row, for a block without fluid and without a loaded ocean map. y is internal.
    public static int Corners(IBlockAccessor blocks, int seaLevel, int x, int y, int z)
    {
        if (y % BlockPos.DimensionBoundary != seaLevel - 1) return 0;
        Block fluid = blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid);
        if (fluid.Id == 0) return 0;
        Span<float> map = stackalloc float[4];
        // Inland the map gives 0 at every corner of the chunk, and a lake on the sea row reads no neighbours.
        if (!ChunkCorners(blocks, x >> 5, z >> 5, map) || map[0] == 0 && map[1] == 0 && map[2] == 0 && map[3] == 0) return 0;

        int same = SameFluidCorners(blocks, fluid, x, y, z);
        int lx = x & (ChunkSize - 1), lz = z & (ChunkSize - 1);
        int packed = 0;
        for (int k = 0; k < 4; k++)
        {
            if ((same & (1 << k)) == 0) continue;
            float value = GameMath.BiLerp(map[0], map[1], map[2], map[3],
                (float)(lx + QuadMesh.CornerDx[k]) / ChunkSize, (float)(lz + QuadMesh.CornerDz[k]) / ChunkSize);
            packed |= (byte)value << (8 * k);
        }
        return packed;
    }

    // A bit per corner NW, SW, SE, NE whose side cells and diagonal cell hold the fluid of the cell, on any row. The
    // tesselator compares the fluid blocks themselves: another level or flow of the same water fails.
    public static int SameFluidCorners(IBlockAccessor blocks, Block fluid, int x, int y, int z)
    {
        // Fluid ids of the cells x - 1 .. x + 1, z - 1 .. z + 1 around the cell, each read once.
        Span<int> ids = stackalloc int[9];
        for (int j = 0; j < 3; j++)
        {
            for (int i = 0; i < 3; i++)
            {
                if (i != 1 || j != 1) ids[j * 3 + i] = blocks.GetBlockRaw(x - 1 + i, y, z - 1 + j, BlockLayersAccess.Fluid).Id;
            }
        }
        int lx = x & (ChunkSize - 1), lz = z & (ChunkSize - 1);
        int same = 0;
        for (int k = 0; k < 4; k++)
        {
            // Column and row of the cells toward the corner: the side cell along z, the one along x, the diagonal one.
            int dx = QuadMesh.CornerDx[k], dz = QuadMesh.CornerDz[k], i = dx * 2, j = dz * 2;
            int diagonal = ids[j * 3 + i];
            if (ids[j * 3 + 1] == fluid.Id && ids[3 + i] == fluid.Id
                && (diagonal == fluid.Id || diagonal == 0 && ChunkCornerGap(blocks, x - 1 + i, y, z - 1 + j, lx, lz, dx, dz)))
            {
                same |= 1 << k;
            }
        }
        return same;
    }

    // The wave weight 1 / div of a point on the still water of block (x, y, z), for a mesh anchored there
    // (wakesanchorwarp.vsh): 0.2 on a lake, up to 1 on the open sea, 0 where the water stands still or there is none.
    // The water moves each corner of its top face by noise / div, 0 at a raised corner, and the GPU lerps the corner
    // heights across the two triangles NW-SW-SE and NW-SE-NE of the face (LiquidTesselator), so the weight is lerped from
    // the corners' weights the same way. Lerping the oceanity instead would put a cloud on a shore cell up to a third of
    // a block off the water. Raised corners by the rule of level 7. Coordinates are absolute, y internal.
    public static float AnchorWave(IBlockAccessor blocks, int seaLevel, double x, int y, double z)
    {
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        Span<float> w = stackalloc float[4];
        AnchorCorners(blocks, seaLevel, bx, y, bz, w);
        return AnchorLerp(w, (float)(x - bx), (float)(z - bz));
    }

    // The wave weights of the corners NW, SW, SE, NE of the water's top face in block (x, y, z), all 0 without fluid,
    // for a caller that keeps them while its point stays in the block.
    public static void AnchorCorners(IBlockAccessor blocks, int seaLevel, int x, int y, int z, Span<float> w)
    {
        if (blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid).Id == 0)
        {
            w[..4].Clear();
            return;
        }
        int ocean = Corners(blocks, seaLevel, x, y, z);
        int raised = StillWaterScan.RaisedCorners(blocks, x, y, z);
        for (int k = 0; k < 4; k++)
        {
            w[k] = (raised & (1 << k)) != 0 ? 0 : 1f / Divisor(((ocean >> (8 * k)) & 0xFF) / 255f);
        }
    }

    // The weight at (fx, fz) of the block from the weights of its corners, over the triangle the point lies in.
    public static float AnchorLerp(ReadOnlySpan<float> w, float fx, float fz)
    {
        // Corners NW 0, SW 1, SE 2, NE 3; the diagonal NW-SE is fx == fz, the SW triangle lies at fz >= fx.
        return fz >= fx
            ? w[0] + (w[2] - w[1]) * fx + (w[1] - w[0]) * fz
            : w[0] + (w[3] - w[0]) * fx + (w[2] - w[3]) * fz;
    }

    // wakesWaveDivisor of wakesanchorwarp.vsh, the divisor chunkliquid gives a corner of oceanity 0..1.
    public static float Divisor(float oceanity) => 5f * Math.Max(0.2f, 1f - oceanity);

    // The four values of the ocean map at the corners of chunk (chunkX, chunkZ), 0..255 in the order TL, TR, BL, BR of
    // ClientWorldMap.LoadOceanityCorners; false without a loaded map. One map pixel to the next is the next corner, and
    // with 32 blocks a pixel that is the next chunk.
    public static bool ChunkCorners(IBlockAccessor blocks, int chunkX, int chunkZ, Span<float> corners)
    {
        int regionSize = blocks.RegionSize;
        int chunksPerRegion = regionSize / ChunkSize;
        IntDataMap2D? map = blocks.GetMapRegion(chunkX / chunksPerRegion, chunkZ / chunksPerRegion)?.OceanMap;
        if (map == null || map.InnerSize <= 0) return false;
        float u = (float)(chunkX * ChunkSize % regionSize) / regionSize * map.InnerSize;
        float v = (float)(chunkZ * ChunkSize % regionSize) / regionSize * map.InnerSize;
        corners[0] = map.GetIntLerpedCorrectly(u, v);
        corners[1] = map.GetIntLerpedCorrectly(u + 1f, v);
        corners[2] = map.GetIntLerpedCorrectly(u, v + 1f);
        corners[3] = map.GetIntLerpedCorrectly(u + 1f, v + 1f);
        return true;
    }

    // A cell in a corner of its chunk takes the diagonal cell (x, y, z) across the chunk corner as the same fluid when
    // that cell, already without fluid, is empty in the solid layer too, which an unloaded neighbour chunk also reads as.
    static bool ChunkCornerGap(IBlockAccessor blocks, int x, int y, int z, int lx, int lz, int dx, int dz)
    {
        if (lx != (dx == 0 ? 0 : ChunkSize - 1) || lz != (dz == 0 ? 0 : ChunkSize - 1)) return false;
        return blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Solid).Id == 0;
    }
}
