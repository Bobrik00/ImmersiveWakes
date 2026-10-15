namespace ImmersiveWakes.Simulation;

// The core's view of the world's water; the game's probe comes with the driver (T05.02).
interface IWaterProbe
{
    // validPos of Wakes: a node lives only on a water source block with no liquid right above it. The node asks on
    // insertion and every tick.
    bool IsSurfaceWater(int x, int y, int z);

    // BiomeColors.getAverageWaterColor, ARGB: the tint a node's bucket colours blend with, asked when a block without
    // a node is queued, where Wakes built the node, and without SimulationParams.CacheWaterTint again on every tick
    // of every live node (cacheNodeWaterColor of Wakes).
    int WaterTint(int x, int y, int z);
}
