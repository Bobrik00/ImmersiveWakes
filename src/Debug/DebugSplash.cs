using System;
using System.Collections.Generic;
using ImmersiveWakes.Game;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using static System.FormattableString;

namespace ImmersiveWakes.Debug;

// ".wakes splash": puts splashes into the core's queue without an entity, for the checks of the driver, the renderer
// and the load. One splash goes where the player looks at the water; n go into random water blocks around the player.
// A splash is the entry of a body a block wide at half a block a step: the disc of AddSplash with r = 6 px at R16, an
// impulse of splashStrength * 0.5. The nodes come with the next step.
sealed class DebugSplash
{
    public const int Radius = 32, MaxCount = 10000;
    const double Velocity = 0.5;
    const float Width = 1f;
    const double Reach = 64, RayStep = 1 / 32.0;

    // Rows searched for the surface around the player's block; the first fluid from the top decides, as in
    // StillWaterScan. The sea shore of the test world stands 14 blocks over the water (tools/wakes-test.md).
    const int Above = 8, Below = 16;

    readonly ICoreClientAPI capi;
    readonly List<(int X, int Y, int Z)> cells = [];

    public DebugSplash(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // The ray of the block selection (GameMain.RayTraceForSelection: eye, Pos.Pitch, Pos.Yaw), marched in 1/32 block
    // steps until it is under the surface of a water block the core takes. A solid block or another fluid on the way
    // ends it: lava, lake ice in the water's place, flowing water.
    public string AtLook(WakeHandler core, IWaterProbe water, int strength)
    {
        EntityPlayer player = capi.World.Player.Entity;
        Vec3d eye = player.Pos.XYZ.Add(player.LocalEyePos);
        Vec3f dir = EntityPos.GetViewVector(player.Pos.Pitch, player.Pos.Yaw);
        IBlockAccessor blocks = capi.World.BlockAccessor;
        int lastX = int.MinValue, lastY = int.MinValue, lastZ = int.MinValue;
        bool surface = false;
        for (double d = 0; d <= Reach; d += RayStep)
        {
            double x = eye.X + dir.X * d, y = eye.Y + dir.Y * d, z = eye.Z + dir.Z * d;
            int bx = (int)Math.Floor(x), by = (int)Math.Floor(y), bz = (int)Math.Floor(z);
            if (bx != lastX || by != lastY || bz != lastZ)
            {
                lastX = bx;
                lastY = by;
                lastZ = bz;
                Block solid = blocks.GetBlockRaw(bx, by, bz, BlockLayersAccess.Solid);
                if (solid.Id != 0 && solid.CollisionBoxes is { Length: > 0 }) return $"splash: the look meets {solid.Code} at {bx} {by} {bz} before any water";
                Block fluid = blocks.GetBlockRaw(bx, by, bz, BlockLayersAccess.Fluid);
                surface = water.IsSurfaceWater(bx, by, bz);
                if (fluid.Id != 0 && !surface) return $"splash: the look meets {fluid.Code} at {bx} {by} {bz}, not a water surface the wake takes";
            }
            if (surface && y <= by + 7 / 8.0)
            {
                int queued = Add(core, strength, x, z, by);
                return Invariant($"splash: at {x:0.00} {by} {z:0.00}, strength {strength}, {queued} new blocks in the queue, {core.Queue.Count} in all for the next step");
            }
        }
        return $"splash: no water within {Reach} blocks of the look";
    }

    // n splashes into water blocks of the columns within Radius, picked at random with repeats.
    public string Around(WakeHandler core, IWaterProbe water, int strength, int n)
    {
        IBlockAccessor blocks = capi.World.BlockAccessor;
        FastVec3d pos = capi.World.Player.Entity.Pos.XYZFast;
        int cx = (int)Math.Floor(pos.X), cy = (int)Math.Floor(pos.Y), cz = (int)Math.Floor(pos.Z);
        cells.Clear();
        for (int x = cx - Radius; x <= cx + Radius; x++)
        {
            for (int z = cz - Radius; z <= cz + Radius; z++)
            {
                for (int y = cy + Above; y >= cy - Below; y--)
                {
                    if (blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid).Id == 0) continue;
                    if (water.IsSurfaceWater(x, y, z)) cells.Add((x, y, z));
                    break;
                }
            }
        }
        if (cells.Count == 0) return $"splash: no water surface the wake takes within {Radius} blocks";
        Random rand = capi.World.Rand;
        int queued = 0;
        for (int i = 0; i < n; i++)
        {
            (int x, int y, int z) = cells[rand.Next(cells.Count)];
            queued += Add(core, strength, x + 0.2 + 0.6 * rand.NextDouble(), z + 0.2 + 0.6 * rand.NextDouble(), y);
        }
        return $"splash: {n} in {cells.Count} water blocks within {Radius} blocks, strength {strength}, {queued} new blocks in the queue, " +
               $"{core.Queue.Count} in all for the next step";
    }

    static int Add(WakeHandler core, int strength, double x, double z, int y)
    {
        int before = core.Queue.Count;
        core.AddSplash(x, z, y, strength, Velocity, Width);
        return core.Queue.Count - before;
    }
}
