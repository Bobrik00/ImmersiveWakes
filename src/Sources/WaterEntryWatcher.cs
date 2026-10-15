using System;
using System.Globalization;
using System.Text;
using ImmersiveWakes.Config;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ImmersiveWakes.Sources;

// Rings where a source enters the water, Wakes' hook on doWaterSplashEffect with placeFallSplash: a filled disc of
// splashStrength * |dy| around the entity, dy its vertical move in blocks per 50 ms (FINDINGS "Как движение сущности
// превращается в импульсы"). The tracker calls Watch on every sample of a source that passed its skips, the surface
// test not among them: a diver who goes under rings as well. Leaving the water rings nothing, as in Wakes.
//
// No Harmony on Entity.OnCollideWithLiquid and no FeetInLiquid (Н1). The prefix would see the server's copies of the
// entities in single player, and the flag of an entity the server moves is taken at its newest snapshot, 66-133 ms
// ahead of the picture. The watcher repeats the engine's test, a liquid block at the feet (ApplyTests), on the Pos
// that is drawn. A ring needs a still surface the core takes above the feet within MaxSurfaceScan blocks.
//
// A projectile is watched on the path its interpolator draws instead: straight segments from snapshot to snapshot
// (EntityBehaviorInterpolatePosition: Pos is a lerp of pL and pN). The server reflects a thrown stone off the water
// within one substep of its physics, 1/300 s, inside the top eighth of the water block (EntityThrownItem.
// OnCollideWithLiquid for a stone falling under 18 b/s, EntityThrownStone for a boulder at any speed;
// BehaviorPassivePhysics.OnPhysicsTick). A snapshot every 1/30 s (every 1/15 s from a server that lags) almost never
// catches the stone in the water, so the drawn stone turns over the water instead: a corner of its path that is out of
// the water, lower than both of its neighbours and within BounceBand above the top of a water block is a bounce (fork
// 21), judged as the interpolator leaves it, when the drawn stone stands there. A segment from a corner out of the water
// to one in a liquid block is an entry, rung when the drawn feet pass the top of the water, or when the segment ends,
// should no sample fall inside the dip. An arrow that sticks in the bottom gets no next snapshot for up to a second, which is why
// the entry is not left to the corner. The engine may leave two corners in one frame (it speeds up to catch up), so the
// corner the path was heading to at the last sample is judged on its own when the next sample finds it passed. Three
// or more between two samples (under 10 FPS, or the queue cleared past 20 snapshots) lose the ones between: the
// segment that stands for them reads its fall over one interval, too fast, unless it is long enough to count as a jump.
//
// A bite rings the fishing bobber as well (fork 21), though it never leaves its water block: the server pulls the
// floating bobber down, Motion.Y -= 0.1 in EntityBobber.playCatchEffects, and by the physics of its density it dives
// 0.47 blocks under the surface within a quarter of a second, comes up 0.07 over it and settles after a second dip of
// 0.1 (reference/wf3/verify-assumptions/bobberbite.py). The ring goes as its feet pass BiteDepth under the surface;
// the next one waits until they float within BiteRearm of it without sinking, which the plunge after a cast, rung as
// an entry, and the second dip do not do.
sealed class WaterEntryWatcher
{
    const double StepSeconds = 0.05;
    const double SurfaceHeight = 7 / 8.0;
    const double BiteDepth = 0.2, BiteRearm = 0.1;

    // A fall of 4 blocks in a step is 80 b/s, past any fall but an arrow's.
    const int MaxSurfaceScan = 4;

    // The drawn turn of a bounce is the lower of the snapshots around it: at most d * u * T / (d + u) over the top of
    // the water, with a stone falling d = 18 b/s and leaving at u = 6 b/s (the clamp of 0.1 a tick) and T = 1/15 s from
    // a server that lags, 0.3 blocks; 0.15 at the 1/30 s of one that keeps up (RemoteSpeed).
    const double BounceBand = 0.5;

    // Moves of the drawn feet further than this in a step are a teleport, not a fall (П2; a fall is under 40 b/s). An
    // arrow flies at 60 b/s and is held to a bound of its own: a segment of its path longer than JumpSlack plus the
    // snapshot's interval at PathJumpSpeed is a jump. The interpolator clears a teleport's flag from both ends of the
    // path (OnReceivedServerPos pushes two copies without it), so length is all there is to tell.
    const double JumpSlack = 1, JumpSpeed = 60, JumpWindow = 0.25, PathJumpSpeed = 120;

    const int HistoryLength = 8;

    public enum RingKind { Entry, Bounce, Bite }

    public readonly record struct Ring(double Time, string Source, RingKind Kind, double X, int Y, double Z, double Velocity, int Impulse, int Radius);

    readonly IBlockAccessor blocks;
    readonly IWaterProbe water;
    readonly Ring[] history = new Ring[HistoryLength];
    int historyCount, historyNext;

    public WaterEntryWatcher(IBlockAccessor blocks, IWaterProbe water)
    {
        this.blocks = blocks;
        this.water = water;
    }

    // Over the tracker's life: rings drawn, and entries, bounces and bites whose impulse rounded below 1.
    public long Rings { get; private set; }
    public long WeakRings { get; private set; }

    // motionDriven: the local physics moves the source, and Pos.Motion is true (SourceTracker.UsesSnapshots).
    public void Watch(SourceState s, ITrailSink core, WakesConfig config, bool motionDriven, double clock)
    {
        if (!s.Rule.SimulateWakes)
        {
            s.ResetEntry();
            return;
        }
        if (s.Kind == SourceKind.Projectile && s.Interpolator != null) WatchPath(s, core, config, clock);
        else WatchFeet(s, core, config, motionDriven, clock);
    }

    void WatchFeet(SourceState s, ITrailSink core, WakesConfig config, bool motionDriven, double clock)
    {
        Entity e = s.Entity;
        double x = e.Pos.X, z = e.Pos.Z, feet = e.Pos.InternalY + e.CollisionBox.Y1;
        int bx = (int)Math.Floor(x), by = (int)Math.Floor(feet), bz = (int)Math.Floor(z);
        bool wet = InLiquid(bx, by, bz);
        if (!wet || !s.EntryHasPrev || !s.EntryWet) s.BiteArmed = false;
        if (wet && s.EntryHasPrev && !s.EntryWet)
        {
            double dt = clock - s.EntryTime;
            double dx = x - s.EntryX, dy = s.EntryFeet - feet, dz = z - s.EntryZ;
            double bound = s.Kind == SourceKind.Projectile ? PathJumpSpeed : JumpSpeed;
            bool jump = !(Math.Sqrt(dx * dx + dy * dy + dz * dz) <= JumpSlack + Math.Min(dt, JumpWindow) * bound);
            if (!jump && SurfaceOver(bx, by, bz) is int top)
            {
                // Wakes takes the speed of the tick of the entry, before the water slows it: the local physics keeps
                // Motion true, and its last sample out of the water holds that speed. The drawn move of everything
                // else is the picture's own.
                double velocity = motionDriven ? Math.Max(Math.Abs(s.EntryMotionY), Math.Abs(e.Pos.Motion.Y)) * 3
                    : dt > 0 ? Math.Abs(dy) / dt * StepSeconds : 0;
                double t = Crossing(s.EntryFeet, feet, top);
                Splash(s, core, config, s.EntryX + dx * t, top, s.EntryZ + dz * t, velocity, RingKind.Entry, clock);
            }
        }
        else if (wet && s.EntryHasPrev && s.Kind == SourceKind.Bobber) WatchBite(s, core, config, x, z, feet, bx, by, bz, clock);
        s.EntryHasPrev = true;
        s.EntryWet = wet;
        s.EntryX = x;
        s.EntryZ = z;
        s.EntryFeet = feet;
        s.EntryMotionY = e.Pos.Motion.Y;
        s.EntryTime = clock;
    }

    // A bobber whose feet stood in the water at the last sample as well. The ring takes the drawn sink of the step.
    void WatchBite(SourceState s, ITrailSink core, WakesConfig config, double x, double z, double feet, int bx, int by, int bz, double clock)
    {
        if (SurfaceOver(bx, by, bz) is not int top) return;
        double depth = top + SurfaceHeight - feet, sink = s.EntryFeet - feet;
        if (depth < BiteRearm && sink < BiteRearm) s.BiteArmed = true;
        else if (s.BiteArmed && depth > BiteDepth)
        {
            s.BiteArmed = false;
            double dt = clock - s.EntryTime;
            Splash(s, core, config, x, top, z, dt > 0 ? Math.Max(sink, 0) / dt * StepSeconds : 0, RingKind.Bite, clock);
        }
    }

    void WatchPath(SourceState s, ITrailSink core, WakesConfig config, double clock)
    {
        EntityBehaviorInterpolatePosition interpolator = s.Interpolator!;
        PositionSnapshot left = interpolator.pL, next = interpolator.pN;
        Entity e = s.Entity;
        // Snapshots carry the outer y; blocks are read by the internal one (П4).
        double lift = e.Pos.InternalY - e.Pos.Y + e.CollisionBox.Y1;
        if (!s.HasCorner || !Same(left, s.Corner))
        {
            // The first pop leaves the zeros of an empty pL; the snapshot of Initialize after them is the spawn point.
            if (Empty(left))
            {
                s.HasCorner = false;
                return;
            }
            if (!s.HasCorner) Start(s, left, lift, InLiquid(e));
            else
            {
                if (!Same(left, s.Next) && !Same(s.Next, s.Corner) && !Empty(s.Next)) Leave(s, core, config, s.Next, left, lift, clock);
                Leave(s, core, config, left, next, lift, clock);
            }
        }
        // Kept on every sample with a corner, so the next one tells a corner left unseen from a stale target.
        if (!Same(next, s.Next))
        {
            s.Next = next;
            s.NextWet = !Empty(next) && InLiquid(next, lift);
        }
        if (Empty(next) || Same(next, s.Corner) || s.SegmentRung || s.CornerWet || !s.NextWet) return;
        // On the segment into the water: the ring goes once the drawn feet are down to the top of the water.
        if (SurfaceOver(next, lift) is not int top || e.Pos.InternalY + e.CollisionBox.Y1 > top + 1) return;
        s.SegmentRung = true;
        if (Fall(s.Corner, next, s.PacketRate, out double fall))
        {
            double t = Crossing(s.Corner.y + lift, next.y + lift, top);
            Splash(s, core, config, s.Corner.x + (next.x - s.Corner.x) * t, top, s.Corner.z + (next.z - s.Corner.z) * t, fall, RingKind.Entry, clock);
        }
    }

    // drawnWet: the drawn feet stand in a liquid block already, and an entry on this segment came before the watcher
    // looked: after a skip the first sample only arms it, as in WatchFeet.
    void Start(SourceState s, PositionSnapshot corner, double lift, bool drawnWet)
    {
        s.HasCorner = true;
        s.Corner = corner;
        s.CornerWet = InLiquid(corner, lift);
        s.CornerFall = 0;
        s.SegmentRung = drawnWet;
    }

    // The drawn path leaves corner on its way to after: the segment from the last corner to it ends, with its entry if
    // no sample rang it, and corner may be a bounce.
    void Leave(SourceState s, ITrailSink core, WakesConfig config, PositionSnapshot corner, PositionSnapshot after, double lift, double clock)
    {
        PositionSnapshot from = s.Corner;
        bool wet = InLiquid(corner, lift);
        bool whole = Fall(from, corner, s.PacketRate, out double fall);
        double feetFrom = from.y + lift, feet = corner.y + lift, feetAfter = after.y + lift;
        int bx = (int)Math.Floor(corner.x), by = (int)Math.Floor(feet), bz = (int)Math.Floor(corner.z);
        if (whole && !s.SegmentRung && !s.CornerWet && wet && SurfaceOver(bx, by, bz) is int top)
        {
            double t = Crossing(feetFrom, feet, top);
            Splash(s, core, config, from.x + (corner.x - from.x) * t, top, from.z + (corner.z - from.z) * t, fall, RingKind.Entry, clock);
        }
        // Both segments whole: the drawn stone never stands at a corner a teleport jumped from.
        else if (whole && !wet && !Empty(after) && feetFrom > feet && feetAfter > feet && feet - by < BounceBand &&
                 Fall(corner, after, s.PacketRate, out _) && water.IsSurfaceWater(bx, by - 1, bz))
        {
            // The lowest snapshot often comes after the turn, and the segment into it is then part fall, part rise; the
            // segment before it is all fall.
            Splash(s, core, config, corner.x, by - 1, corner.z, Math.Max(fall, s.CornerFall), RingKind.Bounce, clock);
        }
        s.Corner = corner;
        s.CornerWet = wet;
        s.CornerFall = whole ? fall : 0;
        s.SegmentRung = false;
    }

    // Blocks per 50 ms down the segment from one corner to the next, which spans the snapshot's packets at the entity's
    // rate (RemoteSpeed); false for a segment too long to be flown, a jump.
    static bool Fall(PositionSnapshot from, PositionSnapshot to, double packetRate, out double fall)
    {
        double interval = RemoteSpeed.Seconds(to, packetRate);
        double dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
        fall = -dy / interval * StepSeconds;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= JumpSlack + interval * PathJumpSpeed;
    }

    // How far from the last sample to this one the feet crossed the top of the water block at top, 0..1; 1 when they
    // did not come down to it.
    static double Crossing(double fromFeet, double toFeet, int top) =>
        fromFeet > toFeet ? Math.Clamp((fromFeet - (top + 1)) / (fromFeet - toFeet), 0, 1) : 1;

    bool InLiquid(int x, int y, int z) => blocks.GetBlockRaw(x, y, z, BlockLayersAccess.Fluid).MatterState == EnumMatterState.Liquid;

    bool InLiquid(PositionSnapshot p, double lift) =>
        InLiquid((int)Math.Floor(p.x), (int)Math.Floor(p.y + lift), (int)Math.Floor(p.z));

    bool InLiquid(Entity e) =>
        InLiquid((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.InternalY + e.CollisionBox.Y1), (int)Math.Floor(e.Pos.Z));

    // The still surface the core takes over a liquid block at the feet: the top of the column of liquid above them.
    int? SurfaceOver(int x, int y, int z)
    {
        for (int top = y; top <= y + MaxSurfaceScan; top++)
        {
            if (InLiquid(x, top + 1, z)) continue;
            return water.IsSurfaceWater(x, top, z) ? top : null;
        }
        return null;
    }

    int? SurfaceOver(PositionSnapshot p, double lift) =>
        SurfaceOver((int)Math.Floor(p.x), (int)Math.Floor(p.y + lift), (int)Math.Floor(p.z));

    void Splash(SourceState s, ITrailSink core, WakesConfig config, double x, int y, double z, double velocity, RingKind kind, double clock)
    {
        // A boat splashes as wide as its hull, as its wake is; anything else by its box (getBbWidth).
        float width = s.Hull is { } hull ? 2 * hull.HalfWidth : s.Entity.CollisionBox.XSize;
        if (!core.AddSplash(x, z, y, config.SplashStrength, velocity, width))
        {
            s.WeakRings++;
            WeakRings++;
            return;
        }
        s.Rings++;
        Rings++;
        int radius = WakeHandler.SplashRadius(width, config.WakeResolution);
        history[historyNext] = new Ring(clock, $"{s.Entity.Code.ToShortString()} #{s.Entity.EntityId}", kind, x, y, z, velocity,
            (int)(config.SplashStrength * Math.Abs(velocity)), radius);
        historyNext = (historyNext + 1) % HistoryLength;
        if (historyCount < HistoryLength) historyCount++;
    }

    static bool Same(PositionSnapshot a, PositionSnapshot b) =>
        a.x == b.x && a.y == b.y && a.z == b.z && a.interval == b.interval;

    static bool Empty(PositionSnapshot p) => p.x == 0 && p.y == 0 && p.z == 0;

    // ".wakes debug": the last rings drawn, newest first, y internal.
    public void Report(StringBuilder sb, double clock)
    {
        for (int i = 0; i < historyCount; i++)
        {
            Ring r = history[(historyNext - 1 - i + HistoryLength) % HistoryLength];
            sb.Append('\n').Append(string.Create(CultureInfo.InvariantCulture,
                $"ring {clock - r.Time:0.0} s ago, {r.Kind.ToString().ToLowerInvariant()} of {r.Source} at {r.X:0.00} {r.Y} {r.Z:0.00}: {r.Velocity:0.000} blocks a step, impulse {r.Impulse}, radius {r.Radius} px"));
        }
    }
}
