using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Game;

// The water colour the buckets of a node blend with. Wakes takes BiomeColors.getAverageWaterColor, here it is the
// colour map of the liquid itself: climateWaterTint for water, saltwater, boilingwater and rapidwater
// (FINDINGS "Цвет воды и декали").
static class WaterTint
{
    // Mean grey of the liquid's texture (water.png, saltwater.png, rapidwater.png of 1.22.3; boilingwater draws with
    // water.png): the water's texel is this grey times the tint.
    public const float FreshGrey = 0.539f, SaltGrey = 0.488f, RapidGrey = 0.692f;

    // ARGB through flipRb: true; with false R lands in the low byte (П6). The map reads rain and temperature at the
    // height above sea level, so it takes the logical y, as the tesselator does for the water itself: an internal y
    // stands 32768 blocks higher per dimension. A liquid without a colour map gives white. The alpha byte, which
    // Wakes never reads from the water colour, carries the grey of the liquid's texture for WakePalette.WaterShade.
    public static int Of(IClientWorldAccessor world, Block fluid, int x, int y, int z)
    {
        int argb = world.ApplyColorMapOnRgba(fluid.ClimateColorMapResolved, fluid.SeasonColorMapResolved, -1,
            x, y % BlockPos.DimensionBoundary, z, flipRb: true);
        return (int)(Grey(fluid) * 255 + 0.5f) << 24 | argb & 0xFFFFFF;
    }

    // By the prefix of the code: FirstCodePart splits the path, and the simulation thread asks for every new block.
    public static float Grey(Block fluid)
    {
        string? path = fluid.Code?.Path;
        if (path == null) return FreshGrey;
        if (path.StartsWith("saltwater-", StringComparison.Ordinal)) return SaltGrey;
        return path.StartsWith("rapidwater-", StringComparison.Ordinal) ? RapidGrey : FreshGrey;
    }
}
