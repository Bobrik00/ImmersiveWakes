using System;
using ImmersiveWakes.Config;
using ImmersiveWakes.Game;
using ImmersiveWakes.Render;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Effects;

// The splash clouds of Wakes (SplashCloudParticle, FINDINGS "Частица `splash_cloud`") at the bow of a fast source, in
// a ring of fixed size. Every cloud lives Lifetime steps, so they die in the order they were born, and a full ring
// makes room by dropping its oldest. The driver steps the ring on each step of the core, 20 times a second, and a step
// is one tick of Wakes, without collisions: in water the cloud rises at 0.1 a step and all of its speed goes x0.92, in
// the air it falls 0.05 a step faster and goes x0.95, so it hops on the surface. In water means a liquid in the fluid
// layer of the block the cloud is in, found by floor: Wakes casts to int and tests the wrong block at negative
// coordinates. Wakes tests for water only, any liquid here: a cloud born over water flies about four blocks at most.
//
// A cloud is born only within MaxDistance of the camera, as Minecraft drops a particle farther away
// (LevelRenderer.addParticleInternal, from general knowledge of MC, not checked here): the boats out of sight do not
// fill the ring.
//
// Each cloud is kept a step ahead: Now is where it stands at the last step, Next where the next step puts it, and a
// frame draws it between them by the driver's StepFraction. A boat drawn at its live Pos then has its clouds at the same
// moment, where Wakes draws both a tick late. Alpha 1 - age / lifetime and the frame of the strip follow the fraction
// too, the frames once over the life (fork 4).
//
// The hop stays within a quarter block of the still surface, so a cloud takes the whole move of the water at its point:
// the wave weight of its block (OceanFlags.AnchorCorners), read again when it crosses into another block on the row
// of the water it rose from.
//
// The ring is the main thread's: the sources spawn into it in the driver's step, the renderer reads it in the OIT stage.
// An exception trips the guard the renderer shares, and the clouds are gone for the rest of the world; the wake goes on.
sealed class SplashCloudPool
{
    // 10 steps of life: 20 clouds for a boat of Wakes' pair, 70 for the 7 points of the sailed boat (T07.04), so room
    // for 29 sailed boats at speed; past that the oldest go.
    public const int Capacity = 2048;
    // WakeNode.maxAge / 3 ticks of Wakes, half a second.
    public const int Lifetime = SimulationParams.MaxAge / 3;
    // quadSize 0.3 of Wakes, half the side of the quad: a cloud 0.6 blocks wide.
    public const float HalfSize = 0.3f;
    public const int Sprites = 4;
    // Floats a cloud takes in Fill: centre and still surface, light, half size, alpha, wave weight and tile.
    public const int InstanceFloats = 12;
    const double MaxDistance = 32;

    struct Cloud
    {
        public double X, Y, Z, NextX, NextY, NextZ;
        // The speed that took the cloud from Now to Next, in blocks a step.
        public double Vx, Vy, Vz;
        // The water it rose from: the block row and the still surface on it, internal y.
        public int Row;
        public double SurfaceY;
        public float Wave, NextWave;
        // The block whose corner weights the cloud keeps in corners.
        public int CellX, CellZ;
        public int Age, Sprite, Light;
        public bool Local;
    }

    readonly ICoreClientAPI capi;
    readonly RenderGuard guard;
    readonly IWaterProbe water;
    readonly Cloud[] clouds = new Cloud[Capacity];
    readonly float[] corners = new float[Capacity * 4];
    readonly Random random = new();
    int first, count;

    public SplashCloudPool(ICoreClientAPI capi, RenderGuard guard, IWaterProbe water)
    {
        this.capi = capi;
        this.guard = guard;
        this.water = water;
    }

    public int Count => count;
    public bool Off => guard.Tripped;

    // Over the pool's life: clouds born, dropped from a full ring before their time and lived out.
    public long Spawned { get; private set; }
    public long Evicted { get; private set; }
    public long Expired { get; private set; }

    // Whether a source under this rule that moves `velocity` blocks a step makes clouds: the rule of the plane's tick
    // (SplashPlaneParticle.aliveTick), the planes flag, |v| / maxSplashPlaneVelocity > 0.3 with spawnParticles, above
    // 3 b/s at the default; nothing under disableMod.
    public static bool Wants(EffectSpawningRule rule, WakesConfig config, double velocity) =>
        rule.RenderPlanes && !config.DisableMod && config.SpawnParticles && velocity / config.MaxSplashPlaneVelocity > 0.3f;

    // One step of every cloud: the dead leave the head of the ring, the others move on to Next and work out the one
    // after it.
    public void Step()
    {
        if (guard.Tripped || count == 0) return;
        try
        {
            while (count > 0 && clouds[first].Age + 1 >= Lifetime)
            {
                first = (first + 1) % Capacity;
                count--;
                Expired++;
            }
            IBlockAccessor blocks = capi.World.BlockAccessor;
            int seaLevel = capi.World.SeaLevel;
            for (int k = 0; k < count; k++)
            {
                int i = (first + k) % Capacity;
                ref Cloud c = ref clouds[i];
                c.Age++;
                c.X = c.NextX;
                c.Y = c.NextY;
                c.Z = c.NextZ;
                c.Wave = c.NextWave;
                Tick(ref c, i, blocks, seaLevel);
            }
        }
        catch (Exception e)
        {
            Fail("step", e);
        }
    }

    // Wakes' pair at the bow (SplashPlaneParticle.aliveTick): from the point CloudBow blocks ahead of the leading edge, which
    // lies reach ahead of (x, z) along the unit course (fx, fz), one cloud to each side at rand * width / 4, both on the
    // still surface of block row y with one speed, 1.5 times the source's velocity in blocks a step, tipped 0..45
    // degrees down and turned up to 15 degrees off the course. Wakes takes the width of the entity's box for reach and
    // 0.3 inside it for the point (BoatEmissionRule.WakesCloudBow); a boat gives the edge of its hull it moves toward
    // (fork 19) and the points of its rule (T07.04): the pair's spread times CloudSpread, or with side points one cloud
    // at every point of the rule, all with the same speed.
    // A cloud whose point is not over a surface the wake takes is left out, as its height comes from the water under it,
    // and so is one past MaxDistance from the camera. local marks the clouds of the local player, which
    // firstPersonEffects hides in first person. Returns the clouds born.
    public int SpawnBow(double x, double z, int y, double fx, double fz, double reach, double width, double velocity, bool local,
        BoatEmissionRule rule)
    {
        if (guard.Tripped) return 0;
        try
        {
            double pitch = 45 * random.NextDouble() * GameMath.DEG2RAD;
            double turn = 30 * (random.NextDouble() - 0.5) * GameMath.DEG2RAD;
            double speed = 1.5 * velocity, flat = Math.Cos(pitch) * speed;
            double cos = Math.Cos(turn), sin = Math.Sin(turn);
            double vx = (fx * cos - fz * sin) * flat, vz = (fx * sin + fz * cos) * flat, vy = -Math.Sin(pitch) * speed;
            if (rule.CloudSides == null)
            {
                double spread = random.NextDouble() * width / 4 * rule.CloudSpread;
                double bx = x + fx * (reach + rule.CloudBow), bz = z + fz * (reach + rule.CloudBow);
                double sx = -fz * spread, sz = fx * spread;
                return Spawn(bx + sx, bz + sz, y, vx, vy, vz, local) + Spawn(bx - sx, bz - sz, y, vx, vy, vz, local);
            }
            int born = 0;
            for (int i = 0, n = rule.CloudPointCount; i < n; i++)
            {
                // Across runs along (-fz, fx), local +x of the hull, as .wakes debug hull draws it.
                (float along, float across) = rule.CloudPoint(i);
                born += Spawn(x + fx * (reach + along) - fz * across, z + fz * (reach + along) + fx * across, y, vx, vy, vz, local);
            }
            return born;
        }
        catch (Exception e)
        {
            Fail("spawn", e);
            return 0;
        }
    }

    int Spawn(double x, double z, int y, double vx, double vy, double vz, bool local)
    {
        if (capi.World.Player?.Entity is not { } player) return 0;
        Vec3d cam = player.CameraPos;
        double dx = x - cam.X, dy = y + QuadMesh.SurfaceHeight - cam.Y, dz = z - cam.Z;
        if (dx * dx + dy * dy + dz * dz > MaxDistance * MaxDistance) return 0;
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        if (!water.IsSurfaceWater(bx, y, bz)) return 0;
        if (count == Capacity)
        {
            first = (first + 1) % Capacity;
            count--;
            Evicted++;
        }
        int i = (first + count) % Capacity;
        IBlockAccessor blocks = capi.World.BlockAccessor;
        int seaLevel = capi.World.SeaLevel;
        ref Cloud c = ref clouds[i];
        c = new Cloud
        {
            X = x, Y = y + QuadMesh.SurfaceHeight, Z = z,
            Vx = vx, Vy = vy, Vz = vz,
            Row = y, SurfaceY = y + QuadMesh.SurfaceHeight,
            CellX = int.MinValue,
            Sprite = random.Next(Sprites),
            // The water's top face takes its light from the block above it, as the wake's quads do.
            Light = blocks.GetLightRGBsAsInt(bx, y + 1, bz),
            Local = local,
        };
        c.Wave = WaveAt(ref c, i, blocks, seaLevel, x, z);
        Tick(ref c, i, blocks, seaLevel);
        count++;
        Spawned++;
        return 1;
    }

    // One tick of SplashCloudParticle from Now to Next, and the wave weight at Next.
    void Tick(ref Cloud c, int i, IBlockAccessor blocks, int seaLevel)
    {
        if (StillWaterScan.IsLiquid(blocks, (int)Math.Floor(c.X), (int)Math.Floor(c.Y), (int)Math.Floor(c.Z)))
        {
            c.Vy = 0.1;
            c.Vx *= 0.92;
            c.Vy *= 0.92;
            c.Vz *= 0.92;
        }
        else
        {
            c.Vy -= 0.05;
            c.Vx *= 0.95;
            c.Vy *= 0.95;
            c.Vz *= 0.95;
        }
        c.NextX = c.X + c.Vx;
        c.NextY = c.Y + c.Vy;
        c.NextZ = c.Z + c.Vz;
        c.NextWave = WaveAt(ref c, i, blocks, seaLevel, c.NextX, c.NextZ);
    }

    float WaveAt(ref Cloud c, int i, IBlockAccessor blocks, int seaLevel, double x, double z)
    {
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        Span<float> w = corners.AsSpan(i * 4, 4);
        if (bx != c.CellX || bz != c.CellZ)
        {
            OceanFlags.AnchorCorners(blocks, seaLevel, bx, c.Row, bz, w);
            c.CellX = bx;
            c.CellZ = bz;
        }
        return OceanFlags.AnchorLerp(w, (float)(x - bx), (float)(z - bz));
    }

    // The clouds to draw, InstanceFloats each, and their number: the centre between Now and Next and the still surface
    // under it, relative to the camera (wakescloud.vsh); the light of the block over the water, a byte each as rgba;
    // the half size, the alpha, the wave weight and frame f of strip s as the tile s + f * Sprites. frames is the
    // strips' frame count. hideLocal leaves out the clouds of the local player; reach the clouds farther away
    // horizontally, as the engine culls chunks.
    public int Fill(float[] values, Vec3d camera, float fraction, int frames, bool hideLocal, double reach)
    {
        if (guard.Tripped) return 0;
        float t = Math.Clamp(fraction, 0f, 1f);
        double reachSq = reach * reach;
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            ref Cloud c = ref clouds[(first + k) % Capacity];
            if (hideLocal && c.Local) continue;
            double x = c.X + (c.NextX - c.X) * t - camera.X, z = c.Z + (c.NextZ - c.Z) * t - camera.Z;
            if (x * x + z * z > reachSq) continue;
            double y = c.Y + (c.NextY - c.Y) * t - camera.Y;
            float life = (c.Age + t) / Lifetime;
            int frame = Math.Clamp((int)(life * frames), 0, frames - 1);
            int o = n * InstanceFloats, l = c.Light;
            values[o] = (float)x;
            values[o + 1] = (float)y;
            values[o + 2] = (float)z;
            values[o + 3] = (float)(c.SurfaceY - camera.Y);
            values[o + 4] = (l & 0xFF) / 255f;
            values[o + 5] = ((l >> 8) & 0xFF) / 255f;
            values[o + 6] = ((l >> 16) & 0xFF) / 255f;
            values[o + 7] = ((l >> 24) & 0xFF) / 255f;
            values[o + 8] = HalfSize;
            values[o + 9] = 1 - life;
            values[o + 10] = c.Wave + (c.NextWave - c.Wave) * t;
            values[o + 11] = c.Sprite + frame * Sprites;
            n++;
        }
        return n;
    }

    // A cloud for the traced frame (TraceClouds): its centre and the still surface under it, both in the world, the
    // sprite and the age in steps.
    public struct TracedCloud
    {
        public double X, Y, Z, SurfaceY;
        public int Sprite, Age;
    }

    // The clouds for the traced frame, as Fill gives them to the raster but in the world and without the wave: the
    // centre at `fraction` of the way from Now to Next. Returns their number.
    public int Collect(TracedCloud[] into, Vec3d camera, float fraction, bool hideLocal, double reach)
    {
        if (guard.Tripped) return 0;
        float t = Math.Clamp(fraction, 0f, 1f);
        double reachSq = reach * reach;
        int n = 0;
        for (int k = 0; k < count && n < into.Length; k++)
        {
            ref Cloud c = ref clouds[(first + k) % Capacity];
            if (hideLocal && c.Local) continue;
            double x = c.X + (c.NextX - c.X) * t, z = c.Z + (c.NextZ - c.Z) * t;
            double dx = x - camera.X, dz = z - camera.Z;
            if (dx * dx + dz * dz > reachSq) continue;
            ref TracedCloud o = ref into[n++];
            o.X = x;
            o.Y = c.Y + (c.NextY - c.Y) * t;
            o.Z = z;
            o.SurfaceY = c.SurfaceY;
            o.Sprite = c.Sprite;
            o.Age = c.Age;
        }
        return n;
    }

    void Fail(string pass, Exception e)
    {
        guard.Trip(pass, e);
        first = count = 0;
    }
}
