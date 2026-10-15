using System;
using System.Collections.Generic;
using ImmersiveWakes.Game;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Render;

enum S1Tint { Bare, Grey }

// Synthetic S1 tiles: one static ring wave around the point the tiles were laid from, coloured with the Wakes buckets
// 1:1 and blended with the water tint of each tile at opacity 1. T07.02 picked the emission point by their white crest
// #FFFFFFFF; the bucket brightness against the water (scene C14) is judged on them.
static class S1TileGenerator
{
    // h(d) = Amplitude * exp(-d / Decay) * cos(2 pi d / Wavelength) in blocks from the centre. Every bucket shows up
    // within 1000 tiles (about 16 blocks): 8 at the centre, the troughs at 2, 6 and 10 blocks in buckets 1, 2 and 3,
    // crests of bucket 7 with white flanks near the centre, and all-white crest tops at 16 and 20 blocks.
    const double Amplitude = 24, Decay = 12, Wavelength = 4;

    // pixels is the tiles' texture of res texels per block, 64 slots a row, ARGB.
    public static void Draw(int[] pixels, int res, IClientWorldAccessor world, IReadOnlyList<StillWaterScan.Cell> cells,
        int count, double centreX, double centreZ, S1Tint tint)
    {
        Array.Clear(pixels);
        int size = WakeTexture.Slots * res;
        WakePalette palette = WakePalette.Wakes;
        Span<int> buckets = stackalloc int[WakePalette.ColorCount];
        count = Math.Min(count, Math.Min(cells.Count, WakeTexture.Slots * WakeTexture.Slots));
        for (int q = 0; q < count; q++)
        {
            StillWaterScan.Cell c = cells[q];
            palette.BlendBuckets(WaterColor(world, c, tint), 1f, buckets);
            int x0 = q % WakeTexture.Slots * res, z0 = q / WakeTexture.Slots * res;
            for (int j = 0; j < res; j++)
            {
                double dz = c.Z + (j + 0.5) / res - centreZ;
                int row = (z0 + j) * size + x0;
                for (int i = 0; i < res; i++)
                {
                    double dx = c.X + (i + 0.5) / res - centreX;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    float h = (float)(Amplitude * Math.Exp(-d / Decay) * Math.Cos(2 * Math.PI * d / Wavelength));
                    pixels[row + i] = buckets[palette.ResolveBucket(h)];
                }
            }
        }
    }

    // climateWaterTint at the cell, ARGB with flipRb: true. Bare is the tint alone, as Wakes blends with the biome
    // water colour; grey multiplies it by the mean grey of the water texture, as the water itself is drawn.
    static int WaterColor(IClientWorldAccessor world, StillWaterScan.Cell c, S1Tint tint)
    {
        int argb = world.ApplyColorMapOnRgba("climateWaterTint", null, -1, c.X, c.Y, c.Z, flipRb: true);
        if (tint == S1Tint.Bare) return argb;
        float grey = WaterTint.Grey(world.BlockAccessor.GetBlockRaw(c.X, c.Y, c.Z, BlockLayersAccess.Fluid));
        int r = (int)(((argb >> 16) & 0xFF) * grey), g = (int)(((argb >> 8) & 0xFF) * grey), b = (int)((argb & 0xFF) * grey);
        return (int)(argb & 0xFF000000) | r << 16 | g << 8 | b;
    }
}
