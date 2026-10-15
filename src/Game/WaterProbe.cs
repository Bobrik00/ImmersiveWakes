using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Game;

// The core's view of the world's water (FINDINGS "Поверхность воды"). Block coordinates carry the internal y, the
// dimension included (П4); an entity's Pos.XYZ is on it already. GetBlockRaw never throws and gives air for a block
// that is not loaded, so a node over an unloaded chunk dies on its next tick with no code of its own.
//
// The core asks from the simulation thread (T05.11), the sources from the main thread, each through a probe of its
// own. The relaxed accessor takes the chunk map's lock, unpacks a packed chunk under the chunk's own lock and reads a
// layer of 2 bits or more under its read lock (a layer of 0 or 1 bit is one int read), in the lock order of the
// tesselation threads; it holds no state between calls. The lock-free accessor would be cheaper but never marks a
// chunk as read: the client packs a chunk two or more chunks away from the player after 8 s without a read
// (SystemCompressChunks, optimizeRamMode 1), and from then on it gives air, which would kill every node of a wake out
// there. The tint reads the lerped climate maps under their lock and the map regions from a ConcurrentDictionary
// (ClientWorldMap.ApplyColorMapOnRgba, GetClimate); survival's hemisphere is GenMaps.getLatitude, arithmetic on data
// replaced by reference, and water has no season map. The driver stops the thread before the world goes.
sealed class WaterProbe : IWaterProbe
{
    readonly IClientWorldAccessor world;
    readonly IBlockAccessor blocks;

    public WaterProbe(IClientWorldAccessor world)
    {
        this.world = world;
        blocks = world.GetBlockAccessor(synchronize: false, relight: false, strict: false);
    }

    // validPos of Wakes takes a still source of its water. Here that is level 7 of flow "still" of water, saltwater
    // (the whole sea) or boilingwater; rapidwater carries the code "water" with the same still source and passes,
    // rivulet has no flow group and lava another code. Lake ice takes the water's place in the fluid layer, so a
    // frozen block fails on its own.
    public static bool IsSurfaceFluid(Block fluid) =>
        fluid.LiquidLevel == 7 && fluid.LiquidCode is "water" or "saltwater" or "boilingwater" && fluid.Variant["flow"] == "still";

    // The tesselator draws the top face only without a liquid above, of any kind (LiquidTesselator).
    public bool IsSurfaceWater(int x, int y, int z)
    {
        return IsSurfaceFluid(blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid))
            && blocks.GetBlockRaw(x, y + 1, z, BlockLayersAccess.Fluid).MatterState != EnumMatterState.Liquid;
    }

    public int WaterTint(int x, int y, int z) =>
        Game.WaterTint.Of(world, blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid), x, y, z);
}
