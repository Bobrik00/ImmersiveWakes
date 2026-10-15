using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ImmersiveWakes.Config;
#if DEBUG
using ImmersiveWakes.Debug;
#endif
using ImmersiveWakes.Effects;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWakes.Sources;

// Players, boats, mobs, items and the fishing bobber as sources of the core, Wakes' WakeSpawnerMixin without the mixin
// (DESIGN "Принятые решения"); SourceClassifier tells them.
// The driver calls Step for each step of the core, in the Before stage after the local physics, where Pos holds
// the interpolated position of remote entities (FINDINGS "Позиция и скорость"); the trails go into the input of the
// simulation thread, which hands them to the core right before the step's tick (T05.11). Entities are classified once
// as they spawn or load and forgotten on despawn (П3).
//
// A source on a still water surface (the top of its box out of the water, its feet in it) hands the core the segment
// from its last sample to its position now, as thickNodeTrail did per tick, with a speed in blocks per 50 ms, the step
// of Wakes' formulas (П1). The speed of the local player and of the boat it steers is |Pos.Motion| * 3: their Pos moves
// in whole physics ticks of 1/60 s, so the distance over a 20 Hz window at 144 FPS spans 2 to 4 ticks and reads 0.7 to
// 1.2 of the true speed, while the Motion the local physics keeps is the true one (Wakes takes getDeltaMovement for its
// LocalPlayer too). Everything the server moves gets the snapshot speed of the interpolator (Н9), or the distance over
// the elapsed time when the snapshots say nothing. A pause stops the clock; several steps in one frame see one
// position, the first takes the whole segment and the others nothing.
//
// Where Wakes keeps prevPos, the port lets go of it: under disableMod (Wakes would draw from the old point once it is
// back), under a rule that makes neither the wake nor the clouds (the first sample after .wakes reload turns it on
// draws nothing), and when the surface block changes height. Each costs one step of trail at most.
//
// A jump of the position is a sample further from the last than JumpSlack plus the time, capped at JumpWindow, times
// the larger of JumpFloorSpeed and JumpFactor times the recent speed (П2): modded boats run at 15-20 b/s, which a fixed
// vanilla threshold would cut. A snapshot speed over the same bound is a jump of the speed: the pair of a teleported
// remote player spans the teleport, and a 5-block /tp reads 75 b/s for its 67 ms slide. Both move the sample without a
// trail; the speed jump still feeds the recent speed, so a source met already fast is taken within a few steps. The
// slide itself is not caught when it is short and slow (memory vs-player-teleport-no-isteleport-flag); boarding and
// leaving a seat mute the rider for MuteSeconds (Р8), and whether /tp needs more is И6's question in T05.08.
//
// A swimming player draws from a point swimmerOffset ahead of Pos along its motion: VS lays a swimmer's body flat, its
// head ahead of Pos, where Wakes' MC player swims upright over its position.
//
// Anyone but a boat draws as wide as its box and at least a pixel wide: where Wakes' formula gives no line, as for the
// bobber's 0.1 at R8 and R16, the core keeps the middle one (fork 21). A boat draws from each point of its boatEmission
// rule with the full width of its hull and the rule's strength (forks 7 and 19, the row of points of T07.02). A source
// whose Pos stands inside the hull of a boat leaves no trail (Н4): with PhysicsLib a player walks the deck of a
// Shipwright boat unseated, below the waterline, and would be a second source inside the hull.
//
// Every source past the skips but before the surface test goes to WaterEntryWatcher, which rings its entries into the
// water (T06.04); a projectile gets its rings and nothing else. A skip starts the rings over, the surface test does not.
//
// A source whose rule has the planes flag and that moves faster than SplashCloudPool.Wants allows spawns the bow clouds
// of T06.03 where Wakes' splash plane would: from the edge of a boat's hull it moves toward, along the hull's axis
// (fork 19), from the box width ahead of anyone else along their motion. The sample keeps the bow, and every step
// spawns its clouds, at the points of a boat's boatEmission rule (T07.04) or as Wakes' pair, the steps of a frame
// that samples once included; a step that stands lag steps before the frame takes the bow that much further back
// along the course, where the source was at the step. ONLY_PLANES makes clouds without a wake, as in Wakes.
sealed class SourceTracker : IDisposable
{
    public const int MaxGhosts = 32;
    public const double DebugRadius = 32;
    const double StepSeconds = 0.05, MuteSeconds = 0.5, RecentTau = 0.5;
    const double JumpSlack = 1, JumpFloorSpeed = 10, JumpFactor = 3, JumpWindow = 0.25;
    // The local physics and the core's accumulator drop time past half a second (EntityBehaviorPlayerPhysics).
    const float MaxFrameSeconds = 0.5f;
    const double SurfaceHeight = 7 / 8.0;
    const int DebugLines = 24;
    // Height of a deck around the hull's boxes: a floor below the waterline lies inside them (FINDINGS "Пассажиры и
    // лодка под чужим управлением"), a raised deck and a stepped-on rail within a block above.
    const double DeckSlack = 1;

    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly IWaterProbe water;
    readonly SplashCloudPool clouds;
    readonly WaterEntryWatcher entries;
    readonly Dictionary<long, SourceState> sources = [];
    readonly List<long> gone = [];
    readonly List<GhostSource> ghosts = [];
    // Every boat of a type has the same boxes; by code, for the life of the world.
    readonly Dictionary<AssetLocation, HullShape> hulls = [];
    // The boats of this step whose hulls hold a deck: loaded, alive and in the player's dimension.
    readonly List<SourceState> decks = [];
    double clock, lastSample = -1;
    // The clock right after the last frame longer than MaxFrameSeconds, and the last packet rate measured on an entity of
    // the server's physics (RemoteSpeed.Measure).
    double hitch = double.MinValue, serverRate = RemoteSpeed.DefaultRate;

    public SourceTracker(ICoreClientAPI capi, ILogger log, IWaterProbe water, SplashCloudPool clouds)
    {
        this.capi = capi;
        this.log = log;
        this.water = water;
        this.clouds = clouds;
        entries = new WaterEntryWatcher(capi.World.BlockAccessor, water);
        capi.Event.OnEntitySpawn += OnLoaded;
        capi.Event.OnEntityLoaded += OnLoaded;
        capi.Event.OnEntityDespawn += OnDespawn;
        capi.Event.EntityMounted += OnMounted;
        capi.Event.EntityUnmounted += OnUnmounted;
        if (capi.World.LoadedEntities is { } loaded)
        {
            foreach (Entity entity in loaded.Values) OnLoaded(entity);
        }
    }

    public int Count => sources.Count;

    // For the hull frames of .wakes debug hull, on the main thread.
    public Dictionary<long, SourceState>.ValueCollection States => sources.Values;
    public int GhostCount => ghosts.Count;
    // For the hull mask of the traced frame, on the main thread.
    public IReadOnlyList<GhostSource> Ghosts => ghosts;

    // Over the tracker's life, ghosts left out: steps that drew at least one segment with an impulse of at least 1, a
    // boat's row of points counting once, and jumps.
    public long Trails { get; private set; }
    public long Jumps { get; private set; }

#if DEBUG
    // .wakes trace (T08.05, И20).
    public FreezeTrace? Trace { get; set; }
#endif

    // Seconds of unpaused frames, the clock of the samples and the mutes.
    public void Advance(float deltaTime)
    {
        clock += Math.Min(deltaTime, MaxFrameSeconds);
        if (deltaTime > MaxFrameSeconds) hitch = clock;
    }

    // lag: how far the step stands before the frame, in steps (the driver's accumulator after the step's share).
    public void Step(ITrailSink core, WakesConfig config, double lag)
    {
        if (clock > lastSample)
        {
            SampleEntities(core, config);
            lastSample = clock;
        }
        SpawnBows(lag);
        if (config.DisableMod || ghosts.Count == 0) return;
        try
        {
            foreach (GhostSource ghost in ghosts) ghost.Step(core, clouds, config);
        }
        catch (Exception e)
        {
            // A debug source must not stop the step for the rest of the world. Its trails meet the water probe on the
            // simulation thread, which skips a trail that throws (WakeThread).
            log.Error("{0}", $"A wake ghost threw, the ghosts are cleared: {e}");
            ghosts.Clear();
        }
    }

    // Wakes spawns a pair on every tick; a source keeps the bow of its last sample until the next one.
    void SpawnBows(double lag)
    {
        if (clouds.Off) return;
        EntityPlayer? me = capi.World.Player?.Entity;
        foreach (SourceState s in sources.Values)
        {
            if (!s.Bow || s.Broken) continue;
            double back = s.LastVelocity * lag;
            // The rule of a boat's last sample places the points; anyone else keeps None and sprays as in Wakes.
            BoatEmissionRule rule = s.Emission;
            s.BowClouds += clouds.SpawnBow(s.PrevX - s.BowCourseX * back, s.PrevZ - s.BowCourseZ * back, s.PrevY, s.BowCourseX,
                s.BowCourseZ, s.BowReach, s.LastWidth, s.LastVelocity, s.Entity == me, rule);
        }
    }

    void SampleEntities(ITrailSink core, WakesConfig config)
    {
        EntityPlayer? me = capi.World.Player?.Entity;
        // The local player's entity may come before the tracker's handlers or without the events.
        if (me != null && !sources.ContainsKey(me.EntityId)) OnLoaded(me);
        int dimension = me?.Pos.Dimension ?? 0;
        Dictionary<long, Entity> loaded = capi.World.LoadedEntities;
        decks.Clear();
        foreach (SourceState s in sources.Values)
        {
            if (s.Hull != null && !s.Broken && s.Entity.Alive && s.Entity.Pos.Dimension == dimension &&
                loaded.TryGetValue(s.Entity.EntityId, out Entity? boat) && boat == s.Entity)
            {
                decks.Add(s);
            }
        }
        foreach (SourceState s in sources.Values)
        {
            // ClientPlayer replaces a player's entity without a despawn event (ClientPlayer.WarnIfEntityChanged).
            if (s.Entity != me && (!loaded.TryGetValue(s.Entity.EntityId, out Entity? live) || live != s.Entity))
            {
                gone.Add(s.Entity.EntityId);
                continue;
            }
            if (s.Broken) continue;
            try
            {
                if (me != null && s.Kind == SourceKind.Boat && me.MountedOn?.Entity == s.Entity)
                {
                    // boatdisconnectfix hands the helm to another seated player while the local one stays: ForceController
                    // when someone takes a seat that steers, Reconcile once a second. The boat goes back to snapshots
                    // the client has had none of since it took the helm, as after leaving it.
                    bool steering = s.Mountable?.Controller == me;
                    if (s.SteeredByMe && !steering) MarkStale(s);
                    s.SteeredByMe = steering;
                }
                // Off the surface too, so an entity has its rate when it swims out.
                if (s.Interpolator != null) RemoteSpeed.Measure(s, clock, hitch, ref serverRate);
                Sample(s, core, config, me, dimension);
#if DEBUG
                Trace?.Record(s, UsesSnapshots(s, me));
#endif
            }
            catch (Exception e)
            {
                // A mod's entity that throws from a property must not stop every other wake.
                s.Broken = true;
                log.Error("{0}", $"The wake source {s.Entity.Code} #{s.Entity.EntityId} threw and is off for this world: {e}");
            }
        }
        foreach (long id in gone) sources.Remove(id);
        gone.Clear();
    }

    void Sample(SourceState s, ITrailSink core, WakesConfig config, EntityPlayer? me, int dimension)
    {
        Entity e = s.Entity;
        s.Bow = false;
        if (config.DisableMod)
        {
            s.Rule = EffectSpawningRule.Disabled;
            Skip(s, SourceVerdict.ModDisabled);
            return;
        }
        s.Rule = RuleOf(s, config, me, out bool spectator);
        if (!e.Alive)
        {
            Skip(s, SourceVerdict.Dead);
            return;
        }
        // Blocks are read by the internal y (П4): an entity of another dimension would read this one's water.
        if (e.Pos.Dimension != dimension)
        {
            Skip(s, SourceVerdict.OtherDimension);
            return;
        }
        if (spectator)
        {
            Skip(s, SourceVerdict.Spectator);
            return;
        }
        // A seated player keeps FeetInLiquid, a remote one even the boat's Motion: the boat leaves the wake.
        if (e is EntityAgent { MountedOn: not null })
        {
            Skip(s, SourceVerdict.Passenger);
            return;
        }
        if (s.Kind != SourceKind.Boat && DeckOf(e) is { } deck)
        {
            s.LastDeck = deck.Entity;
            Skip(s, SourceVerdict.OnDeck);
            return;
        }
        if (!s.Rule.SimulateWakes && !s.Rule.RenderPlanes)
        {
            Skip(s, SourceVerdict.RuleOff);
            return;
        }
        if (clock < s.MuteUntil)
        {
            Skip(s, SourceVerdict.Muted);
            return;
        }
        bool snapshots = UsesSnapshots(s, me);
        if (snapshots && s.Stale)
        {
            if (StillStale(s))
            {
                Skip(s, SourceVerdict.StaleSnapshots);
                return;
            }
            s.Stale = false;
        }
        // Before the surface test: a source rings on its way into the water, its top out of it or not.
        entries.Watch(s, core, config, !snapshots, clock);
        if (s.Kind == SourceKind.Projectile)
        {
            Miss(s, SourceVerdict.RingsOnly);
            return;
        }
        if (!OnSurface(s, out int y))
        {
            Miss(s, SourceVerdict.OffSurface);
            return;
        }

        double x = e.Pos.X, z = e.Pos.Z;
        double remote = snapshots ? RemoteSpeed.Of(s.Interpolator!, s.PacketRate) : -1;
        double motion = Math.Sqrt(e.Pos.Motion.X * e.Pos.Motion.X + e.Pos.Motion.Z * e.Pos.Motion.Z) * 60;
        (double courseX, double courseZ) = SwimCourse(s);
        // Back on the surface, a source leaves no trail on its first sample (WakeSpawnerMixin). The recent speed starts
        // from what the entity already does, so a boat met at full speed is not taken for a jump. A remote entity's
        // Motion comes from its snapshots (HandleRemotePhysics) or the boat's own prediction.
        if (!s.HasPrev || s.PrevY != y)
        {
            s.RecentSpeed = remote >= 0 ? remote : motion;
            s.RemoteCount = 0;
            Remember(s, x, y, z, courseX, courseZ, SourceVerdict.Started);
            return;
        }

        double dt = clock - s.PrevTime;
        double dx = x - s.PrevX, dz = z - s.PrevZ;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        s.LastDistance = distance;
        s.LastDt = dt;
        float width = e.CollisionBox.XSize, strength = config.InitialStrength;
        // A swimmer draws from its point swimmerOffset ahead at the last sample to the one now, both with the offset
        // in use, so a change by .wakes swimmer draws no stripe. The point's own move stays out of the jump test: a
        // turn moves it by twice the offset at most.
        double lead = config.SwimmerOffset;
        double fromX = s.PrevX + s.PrevCourseX * lead, fromZ = s.PrevZ + s.PrevCourseZ * lead;
        double toX = x + courseX * lead, toZ = z + courseZ * lead;
        s.LastLead = s.Kind == SourceKind.Player && e.Swimming ? Math.Sqrt(courseX * courseX + courseZ * courseZ) * lead : null;
        HullShape? hull = s.Hull;
        BoatEmissionRule emission = BoatEmissionRule.None;
        int points = 1;
        if (hull != null)
        {
            // Both ends of every point from the rule in use, so an offset changed by .wakes reload draws no stripe
            // across the boat.
            emission = EmissionOf(s, config);
            points = emission.Points(hull.Length);
            width = 2 * hull.HalfWidth;
            strength *= emission.StrengthMultiplier;
            (fromX, fromZ) = hull.Emission(s.PrevX, s.PrevZ, s.PrevYaw, emission.Offset);
            (toX, toZ) = hull.Emission(x, z, e.Pos.Yaw, emission.Offset);
            // A point off Pos also sweeps with the turn: a sailed boat turning through a half-second hitch would
            // draw a chord of about 9 blocks across its turn at offset 1 while Pos moves 3. The move of a point is
            // linear in its offset, so its length peaks at an end of the row.
            double ex = toX - fromX, ez = toZ - fromZ;
            s.LastDistance = Math.Max(distance, Math.Sqrt(ex * ex + ez * ez));
            if (points > 1)
            {
                float last = emission.OffsetAt(points - 1, points);
                (double ax, double az) = hull.Emission(s.PrevX, s.PrevZ, s.PrevYaw, last);
                (double bx, double bz) = hull.Emission(x, z, e.Pos.Yaw, last);
                s.LastDistance = Math.Max(s.LastDistance, Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)));
            }
        }
        double bound = Math.Max(JumpFloorSpeed, JumpFactor * s.RecentSpeed);
        if (!(s.LastDistance <= JumpSlack + Math.Min(dt, JumpWindow) * bound))
        {
            Jumps++;
            s.Jumps++;
            Remember(s, x, y, z, courseX, courseZ, SourceVerdict.Jump);
            return;
        }
        if (remote > bound)
        {
            Smooth(s, remote, dt);
            Jumps++;
            s.Jumps++;
            s.LastSpeed = remote;
            Remember(s, x, y, z, courseX, courseZ, SourceVerdict.SpeedJump);
            return;
        }

        (double speed, SpeedSource source) = remote >= 0 ? (RemoteSpeed.Mean(s, remote), SpeedSource.Snapshots)
            : !snapshots ? (motion, SpeedSource.Motion)
            : (distance / dt, SpeedSource.Position);
        Smooth(s, speed, dt);
        double velocity = speed * StepSeconds;
        bool drawn = s.Rule.SimulateWakes && (hull == null
            ? core.AddTrail(fromX, fromZ, toX, toZ, y, strength, velocity, width)
            : AddRow(core, hull, emission, points, s.PrevX, s.PrevZ, s.PrevYaw, x, z, e.Pos.Yaw, y, strength, velocity));
        if (SplashCloudPool.Wants(s.Rule, config, velocity))
        {
            // Wakes turns the plane of a boat by the boat's yaw and of anything else by its motion. The local physics
            // keeps Motion true; for the others the sample's move is the motion. Snapshots can read a speed while Pos
            // has not moved since the last sample: without a course there is no bow.
            (double mx, double mz) = snapshots ? (dx, dz) : (e.Pos.Motion.X, e.Pos.Motion.Z);
            double cx = mx, cz = mz, reach = width;
            if (hull != null)
            {
                // The bow is the edge of the hull the boat moves toward. A boat rowed with a paddle goes where its
                // rower looks, so a raft rowed facing its stern runs stern first at full speed
                // (EntityBoat.SeatsToMotion, isLookingBackwards), and Wakes, which takes the yaw alone, would spray
                // from its trailing edge.
                (cx, cz, reach) = (Math.Sin(e.Pos.Yaw), Math.Cos(e.Pos.Yaw), hull.Bow);
                s.BowAstern = mx * cx + mz * cz < 0;
                if (s.BowAstern) (cx, cz, reach) = (-cx, -cz, hull.Stern);
            }
            double course = Math.Sqrt(cx * cx + cz * cz);
            if (course > 1e-9)
            {
                s.Bow = true;
                s.BowCourseX = cx / course;
                s.BowCourseZ = cz / course;
                s.BowReach = reach;
            }
        }
        s.LastSpeedSource = source;
        s.LastSpeed = speed;
        s.LastVelocity = velocity;
        s.LastWidth = width;
        s.LastStrength = strength;
        s.LastEmitX = toX;
        s.LastEmitZ = toZ;
        s.LastY = y;
        // The core's own cast, so the debug line shows the impulse of a trail too short to draw.
        s.LastImpulse = (int)(strength * velocity);
        if (drawn) Trails++;
        Remember(s, x, y, z, courseX, courseZ, drawn ? SourceVerdict.Trail : SourceVerdict.Still);
    }

    // The course of a swimming player, a unit vector; zero for anyone else. The swim animation lays the body flat
    // around Pos (WakesConfig.SwimmerOffset) and the engine runs it while Swimming is on; wading in shallows the body
    // stands upright and Swimming is off. Pos.Motion is kept by the local physics and taken from the snapshots by the
    // remote one (EntityBehaviorPlayerPhysics); a swimmer that stands still keeps its last course.
    static (double X, double Z) SwimCourse(SourceState s)
    {
        Entity e = s.Entity;
        if (s.Kind != SourceKind.Player || !e.Swimming) return (0, 0);
        double mx = e.Pos.Motion.X, mz = e.Pos.Motion.Z, m = Math.Sqrt(mx * mx + mz * mz);
        if (m > 1e-6) return (mx / m, mz / m);
        return s.HasPrev ? (s.PrevCourseX, s.PrevCourseZ) : (0, 0);
    }

    // A boat's step: each point of its row from where it stood at the last sample to where it stands now, with the full
    // width of the hull and the rule's strength already in strength; whether a segment was drawn. Ghost boats draw
    // through it as well, so the load of T05.09 costs what a boat costs.
    public static bool AddRow(ITrailSink core, HullShape hull, BoatEmissionRule rule, int points, double fromX, double fromZ,
        double fromYaw, double x, double z, double yaw, int y, float strength, double velocity)
    {
        float width = 2 * hull.HalfWidth;
        bool drawn = false;
        for (int i = 0; i < points; i++)
        {
            float offset = rule.OffsetAt(i, points);
            (double ax, double az) = hull.Emission(fromX, fromZ, fromYaw, offset);
            (double bx, double bz) = hull.Emission(x, z, yaw, offset);
            drawn |= core.AddTrail(ax, az, bx, bz, y, strength, velocity, width);
        }
        return drawn;
    }

    static void Smooth(SourceState s, double speed, double dt) =>
        s.RecentSpeed += (speed - s.RecentSpeed) * (1 - Math.Exp(-dt / RecentTau));

    void Remember(SourceState s, double x, int y, double z, double courseX, double courseZ, SourceVerdict verdict)
    {
        s.HasPrev = true;
        s.PrevX = x;
        s.PrevZ = z;
        s.PrevCourseX = courseX;
        s.PrevCourseZ = courseZ;
        s.PrevYaw = s.Entity.Pos.Yaw;
        s.PrevY = y;
        s.PrevTime = clock;
        s.Verdict = verdict;
    }

    // A skip before the water entry test: the source starts over for the rings as well, so leaving a seat, a deck, a
    // mute or another dimension in the water rings nothing.
    static void Skip(SourceState s, SourceVerdict verdict)
    {
        s.ResetPrev();
        s.ResetEntry();
        s.Verdict = verdict;
    }

    // No trail this step; the rings keep their last sample.
    static void Miss(SourceState s, SourceVerdict verdict)
    {
        s.ResetPrev();
        s.Verdict = verdict;
    }

    // The boat in whose hull the entity's Pos stands, if any.
    SourceState? DeckOf(Entity e)
    {
        double x = e.Pos.X, y = e.Pos.InternalY, z = e.Pos.Z;
        foreach (SourceState boat in decks)
        {
            if (boat.Entity == e) continue;
            HullShape hull = boat.Hull!;
            EntityPos p = boat.Entity.Pos;
            if (y < p.InternalY + hull.MinY - DeckSlack || y > p.InternalY + hull.MaxY + DeckSlack) continue;
            if (hull.Contains(p.X, p.Z, p.Yaw, x, z)) return boat;
        }
        return null;
    }

    // Looked up again only after .wakes reload.
    public static BoatEmissionRule EmissionOf(SourceState s, WakesConfig config)
    {
        if (s.EmissionConfig != config)
        {
            s.Emission = config.EmissionFor(s.Entity.Code);
            s.EmissionConfig = config;
        }
        return s.Emission;
    }

    // The first boat type met in this world whose code matches, with its hull; null when none has loaded yet.
    public (AssetLocation Code, HullShape Hull)? HullMatching(AssetLocation pattern)
    {
        foreach ((AssetLocation code, HullShape hull) in hulls)
        {
            if (WildcardUtil.Match(pattern, code)) return (code, hull);
        }
        return null;
    }

    // WakesUtils.getEffectRuleFromSource: the spectator gets DISABLED, the local player playerSpawning, a remote one
    // otherPlayersSpawning; a boat with a remote player on board and not ours masks boatSpawning by the other players'
    // rule, whoever of them steers. A ridden animal keeps mobSpawning whoever rides it: Wakes masks only boats.
    EffectSpawningRule RuleOf(SourceState s, WakesConfig config, EntityPlayer? me, out bool spectator)
    {
        spectator = false;
        switch (s.Kind)
        {
            case SourceKind.Player:
                var player = (EntityPlayer)s.Entity;
                // Packet 41 brings every player's game mode to every client; before it the player counts as not one.
                spectator = capi.World.PlayerByUid(player.PlayerUID)?.WorldData?.CurrentGameMode == EnumGameMode.Spectator;
                if (spectator) return EffectSpawningRule.Disabled;
                return player == me ? config.PlayerRule : config.OtherPlayersRule;
            case SourceKind.Mob:
                return config.MobRule;
            case SourceKind.Item:
            case SourceKind.Bobber:
                return config.ItemRule;
            case SourceKind.Projectile:
                return config.ProjectileRule;
        }
        EffectSpawningRule rule = config.BoatRule;
        if (s.Mountable?.Seats is not { } seats) return rule;
        bool ours = false, theirs = false;
        foreach (IMountableSeat? seat in seats)
        {
            if (seat?.Passenger is not EntityPlayer rider) continue;
            if (rider == me) ours = true;
            else theirs = true;
        }
        return theirs && !ours ? rule.Mask(config.OtherPlayersRule) : rule;
    }

    // The local player's Pos comes from its own physics, and so does the Pos of the boat it steers: the interpolator
    // leaves both before its lerp.
    static bool UsesSnapshots(SourceState s, EntityPlayer? me) =>
        s.Interpolator != null && s.Entity != me && (me == null || s.Mountable?.Controller != me);

    // While the local player steers a boat the server neither moves it nor sends its position, and the interpolator
    // keeps the last snapshot from before. The first packet after the local player leaves pairs that snapshot with the
    // new spot over an interval of up to 1/3 s: Pos jumps back to where the boat was boarded and slides to where it is,
    // at three times the trip's speed. The pair is skipped until the interpolator holds two snapshots after it. The
    // engine fixes the same for mounts that are animals (EntityBehaviorRideable), not for EntityBoat.DidUnmount.
    static bool StillStale(SourceState s)
    {
        EntityBehaviorInterpolatePosition interpolator = s.Interpolator!;
        return Same(interpolator.pL, s.StaleSnapshot) || Same(interpolator.pN, s.StaleSnapshot);
    }

    static bool Same(PositionSnapshot a, PositionSnapshot b) =>
        a.x == b.x && a.y == b.y && a.z == b.z && a.interval == b.interval;

    // WakeSpawnerMixin's surface test on VS water: the box's top stands over a still surface the core takes and its
    // feet are under it, so a diver under the surface leaves nothing and a wader in shallows leaves a trail, as in
    // Wakes. Only the surface block passes IsSurfaceWater, the water under it has liquid above, so the column scan
    // finds at most one. Wakes reads the fluid level across the whole box; the column under Pos is enough for vanilla
    // boats, whose waterline lies inside the box. y is internal (П4).
    bool OnSurface(SourceState s, out int y)
    {
        Entity e = s.Entity;
        double baseY = e.Pos.InternalY;
        double feet = baseY + e.CollisionBox.Y1, top = baseY + e.CollisionBox.Y2;
        s.LastFeet = feet;
        s.LastTop = top;
        s.LastSurface = int.MinValue;
        int bx = (int)Math.Floor(e.Pos.X), bz = (int)Math.Floor(e.Pos.Z);
        for (int by = (int)Math.Floor(top); by >= (int)Math.Floor(feet); by--)
        {
            if (!water.IsSurfaceWater(bx, by, bz)) continue;
            double surface = by + SurfaceHeight;
            y = by;
            s.LastSurface = by;
            return feet < surface && top > surface;
        }
        y = 0;
        return false;
    }

    // Spawn and Loaded both come for some entities. An entity loaded again under its id is a new object.
    void OnLoaded(Entity entity)
    {
        if (sources.TryGetValue(entity.EntityId, out SourceState? known) && known.Entity == entity) return;
        if (SourceClassifier.Classify(entity) is { } kind)
        {
            sources[entity.EntityId] = new SourceState(entity, kind, kind == SourceKind.Boat ? HullOf(entity) : null);
        }
        else sources.Remove(entity.EntityId);
    }

    HullShape? HullOf(Entity entity)
    {
        if (hulls.TryGetValue(entity.Code, out HullShape? hull)) return hull;
        hull = HullShape.Of(entity, out string? reason);
        if (hull == null) return null;
        if (reason != null)
        {
            log.Warning("{0}", $"The hull of {entity.Code} cannot be read ({reason}); its wake takes the square hitbox, {hull.MaxX - hull.MinX:0.##} blocks");
        }
        hulls[entity.Code] = hull;
        return hull;
    }

    void OnDespawn(Entity entity, EntityDespawnData reason)
    {
        if (sources.TryGetValue(entity.EntityId, out SourceState? known) && known.Entity == entity) sources.Remove(entity.EntityId);
    }

    // The rider's position jumps into the seat and, on leaving it, to a dry spot the server picks, which a remote
    // observer sees as a slide. Both events come for remote players too (memory vs-player-teleport-no-isteleport-flag);
    // a rider already seated when it loads gets its event before the tracker knows it, and MountedOn skips it anyway.
    // A stale mark outlives boarding: while the local player steers, the boat runs on the local physics and the mark is
    // not read, and the next time the player leaves the helm it is set anew. Taken in another seat, the boat goes on
    // with the snapshots the mark was made for.
    void OnMounted(EntityAgent rider, IMountableSeat seat) => Mute(rider);

    // Only a boat the local player steered is left without snapshots: the client moves it, and the server sends its
    // position to everyone else (StillStale). Leaving another seat makes no mark: the boat has gone on with fresh
    // snapshots, a remote helmsman's among them.
    // Whether the player steered comes from the last sample and from the controller here. In vanilla the controller is
    // still the rider at this event (EntityBoat.SeatsToMotion sets it on every frame, DidUnmount leaves it), which
    // catches a helm taken since the last sample; boatdisconnectfix hands it to the next seated player in a postfix of
    // EntityBoat.DidUnmount, before the event (BoatControllerSync.Reconcile), which the sample catches.
    // A ridden animal needs no stale mark: EntityBehaviorRideable.DidUnmount moves its interpolator to where it stands.
    void OnUnmounted(EntityAgent rider, IMountableSeat seat)
    {
        Mute(rider);
        if (rider != capi.World.Player?.Entity || Boat(seat) is not { Kind: SourceKind.Boat } boat) return;
        bool steered = boat.SteeredByMe || boat.Mountable?.Controller == rider;
        boat.SteeredByMe = false;
        if (steered) MarkStale(boat);
    }

    static void MarkStale(SourceState boat)
    {
        if (boat.Interpolator is not { } interpolator) return;
        boat.Stale = true;
        boat.StaleSnapshot = interpolator.pN;
        boat.ResetPrev();
    }

    void Mute(EntityAgent rider)
    {
        if (!sources.TryGetValue(rider.EntityId, out SourceState? s)) return;
        s.ResetPrev();
        s.MuteUntil = clock + MuteSeconds;
    }

    SourceState? Boat(IMountableSeat seat) =>
        seat.Entity is { } mount && sources.TryGetValue(mount.EntityId, out SourceState? s) && s.Entity == mount ? s : null;

    // count ghosts with speeds evenly from speedFrom to speedTo, the last at speedTo exactly; a ghost boat takes the
    // width of its hull. Circles share their centre, the one at speedFrom innermost.
    public string AddGhosts(GhostPath path, double speedFrom, double speedTo, float width, GhostBoat? boat, int count, WakesConfig config)
    {
        if (capi.World.Player?.Entity is not { } me) return "ghost: no player";
        // Subtracted, so a count near int.MaxValue cannot wrap the sum.
        if (count > MaxGhosts - ghosts.Count) return $"ghost: {ghosts.Count} ghosts already, {MaxGhosts} at most";
        if (!GhostSource.FindWater(capi.World.BlockAccessor, water, me.Pos.X, me.Pos.InternalY, me.Pos.Z, out int x, out int y, out int z))
        {
            return "ghost: no still water surface the wake takes within 8 blocks of you";
        }
        if (boat != null) width = 2 * boat.Hull.HalfWidth;
        // The player's look on the horizontal; a line runs along it, the next lines beside it.
        Vec3f look = EntityPos.GetViewVector(0, me.Pos.Yaw);
        double length = Math.Sqrt(look.X * look.X + look.Z * look.Z);
        double dirX = length > 0 ? look.X / length : 0, dirZ = length > 0 ? look.Z / length : 1;
        double spacing = width + 3;
        for (int i = 0; i < count; i++)
        {
            double offset = (i - (count - 1) / 2.0) * spacing;
            double speed = count == 1 ? speedFrom : i == count - 1 ? speedTo : speedFrom + (speedTo - speedFrom) * i / (count - 1);
            ghosts.Add(path == GhostPath.Line
                ? new GhostSource(path, x + 0.5 - dirZ * offset, z + 0.5 + dirX * offset, y, dirX, dirZ, 0, speed, width, boat)
                : new GhostSource(path, x + 0.5, z + 0.5, y, dirX, dirZ, GhostSource.Radius + i * spacing, speed, width, boat));
        }
        string shape = path == GhostPath.Line
            ? F($"{GhostSource.Length} blocks along your look and back")
            : F($"circles of radius {GhostSource.Radius}") + (count > 1 ? F($" to {GhostSource.Radius + (count - 1) * spacing:0.##}") : "");
        string speeds = count > 1 && speedTo != speedFrom ? F($"{speedFrom:0.##}..{speedTo:0.##}") : F($"{speedFrom:0.##}");
        string what = boat == null ? F($"width {width:0.##}") : GhostBoatText(boat, config);
        return F($"ghost: {count} {(path == GhostPath.Line ? "line" : "circle")} at {speeds} b/s, {what}, from water block {x} {y} {z}, ") +
               $"{shape}; {ghosts.Count} ghosts in all" + (config.DisableMod ? "; disableMod is on, they stand still" : "");
    }

    static string GhostBoatText(GhostBoat boat, WakesConfig config)
    {
        BoatEmissionRule rule = config.EmissionFor(boat.Code);
        return F($"{boat.Name} hull of {boat.Code}, width {2 * boat.Hull.HalfWidth:0.##}, emission ") + EmissionText(rule, boat.Hull) +
               F($", strength x{rule.StrengthMultiplier:0.##}");
    }

    public string ClearGhosts()
    {
        int n = ghosts.Count;
        ghosts.Clear();
        return $"ghost: {n} cleared; their nodes fade on their own";
    }

    // ".wakes debug": a line for each source within DebugRadius of the player, nearest first: players and boats, then
    // mobs, items and bobbers, then projectiles; with all, the other entities there follow. At most DebugLines lines:
    // the fish of a lake buried the boats in the author's first look, a pen holds dozens of animals, and arrows stay in
    // the ground for minutes.
    public string Report(WakesConfig config, bool all)
    {
        if (capi.World.Player?.Entity is not { } me) return "debug: no player";
        var near = new List<(int Rank, double Distance, Entity Entity)>();
        foreach (Entity e in capi.World.LoadedEntities.Values)
        {
            double d = e.Pos.DistanceTo(me.Pos);
            if (d > DebugRadius) continue;
            int rank = !sources.TryGetValue(e.EntityId, out SourceState? s) || s.Entity != e ? NotSource : Rank(s.Kind);
            near.Add((rank, d, e));
        }
        near.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Distance.CompareTo(b.Distance));
        int others = 0;
        foreach ((int rank, _, _) in near) if (rank == NotSource) others++;
        var sb = new StringBuilder(F($"debug: {sources.Count} sources tracked, {ghosts.Count} ghosts; entity trails {Trails}, jumps {Jumps}, ") +
                                   F($"rings {entries.Rings} (and {entries.WeakRings} too weak) so far; ") +
                                   $"disableMod {(config.DisableMod ? "on" : "off")}; within {DebugRadius:0} blocks {near.Count - others} sources and {others} other entities" +
                                   (all || others == 0 ? "" : " ('.wakes debug all' lists them)"));
        if (!all) near.RemoveRange(near.Count - others, others);
        for (int i = 0; i < near.Count && i < DebugLines; i++)
        {
            (int rank, double d, Entity e) = near[i];
            sb.Append('\n').Append(Name(e, me)).Append(F($" #{e.EntityId} at {d:0.0}: "));
            sb.Append(rank == NotSource ? NotSourceText(e) : Describe(sources[e.EntityId], config));
        }
        if (near.Count > DebugLines) sb.Append($"\n... and {near.Count - DebugLines} more");
        entries.Report(sb, clock);
        foreach (GhostSource g in ghosts)
        {
            (double gx, double gz) = g.Position;
            float strength = config.InitialStrength * g.RuleOf(config).StrengthMultiplier;
            // Grouped as AddTrail groups it: a speed of the spread such as 14.999999999999998 reads 14 the other way.
            int impulse = config.DisableMod ? 0 : (int)(strength * (g.Speed * StepSeconds));
            sb.Append(F($"\nghost {(g.Path == GhostPath.Line ? "line" : "circle")} at {gx:0.0} {g.Y} {gz:0.0}, {g.Speed:0.##} b/s, "))
              .Append(g.Boat is { } boat ? GhostBoatText(boat, config) : F($"width {g.Width:0.##}")).Append($", impulse {impulse}, bow clouds {g.BowClouds} so far");
        }
        return sb.ToString();
    }

    const int NotSource = 3;

    static int Rank(SourceKind kind) => kind switch
    {
        SourceKind.Player or SourceKind.Boat => 0,
        SourceKind.Projectile => 2,
        _ => 1,
    };

    static string NotSourceText(Entity e) =>
        e is EntityAgent && SourceClassifier.WhyNotMob(e) is { } why ? $"not a source: {why}" : "not a source";

    // ".wakes debug hull": each boat within DebugRadius, nearest first, with its hull in its own axes, its course and
    // the corners and emission point the frames of HullDebugRenderer draw, in world x and z, and its cloud points.
    // framesOn is null when the frame renderer is off after an error.
    public string HullReport(WakesConfig config, bool? framesOn)
    {
        if (capi.World.Player?.Entity is not { } me) return "hull: no player";
        var near = new List<(double Distance, SourceState Boat)>();
        foreach (SourceState s in sources.Values)
        {
            if (s.Hull == null || s.Entity.Pos.Dimension != me.Pos.Dimension) continue;
            double d = s.Entity.Pos.DistanceTo(me.Pos);
            if (d <= DebugRadius) near.Add((d, s));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        string frames = framesOn switch
        {
            null => "off after an error, see client-main.log",
            true => "on, '.wakes debug hull off' hides them",
            false => "off",
        };
        var sb = new StringBuilder($"hull: frames {frames}; {near.Count} boats within {DebugRadius:0} blocks");
        for (int i = 0; i < near.Count && i < DebugLines; i++)
        {
            (double d, SourceState s) = near[i];
            HullShape h = s.Hull!;
            EntityPos p = s.Entity.Pos;
            BoatEmissionRule rule = EmissionOf(s, config);
            (double ex, double ez) = h.Emission(p.X, p.Z, p.Yaw, rule.Offset);
            sb.Append('\n').Append(Name(s.Entity, me)).Append(F($" #{s.Entity.EntityId} at {d:0.0}: pos {p.X:0.00} {p.Z:0.00}, yaw {p.Yaw * GameMath.RAD2DEG:0.0}, course {Course(p.Yaw)}; "));
            sb.Append(F($"hull {2 * h.HalfWidth:0.##} x {h.Length:0.##} (x {h.MinX:0.##}..{h.MaxX:0.##}, z {h.MinZ:0.##}..{h.MaxZ:0.##} in its axes, bow {h.Bow:0.##} ahead of pos)"));
            if (h.FromHitbox) sb.Append(" from the hitbox");
            sb.Append("; corners bow left ").Append(Corner(p, h.MinX, h.MinZ)).Append(", bow right ").Append(Corner(p, h.MaxX, h.MinZ))
              .Append(", stern right ").Append(Corner(p, h.MaxX, h.MaxZ)).Append(", stern left ").Append(Corner(p, h.MinX, h.MaxZ));
            sb.Append("; emission ").Append(EmissionText(rule, h)).Append(F($", first at {ex:0.00} {ez:0.00}, strength x{rule.StrengthMultiplier:0.##}"))
              .Append("; clouds ").Append(CloudText(rule));
        }
        if (near.Count > DebugLines) sb.Append($"\n... and {near.Count - DebugLines} more");
        return sb.ToString();
    }

    static string Corner(EntityPos p, double x, double z)
    {
        (double dx, double dz) = HullShape.Turn(x, z, p.Yaw);
        return F($"{p.X + dx:0.00} {p.Z + dz:0.00}");
    }

    // Degrees clockwise from north of the bow's direction (sin yaw, cos yaw), with the nearest of eight points.
    static string Course(float yaw)
    {
        // Rounded first: 359.6 would print as 360.
        int degrees = (int)Math.Round(180 - yaw * GameMath.RAD2DEG) % 360;
        if (degrees < 0) degrees += 360;
        return $"{degrees} ({Points[(int)Math.Round(degrees / 45.0) % 8]})";
    }

    static readonly string[] Points = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];

    string Name(Entity e, EntityPlayer me)
    {
        if (e is EntityPlayer p) return (capi.World.PlayerByUid(p.PlayerUID)?.PlayerName ?? p.Code.ToShortString()) + (p == me ? " (you)" : "");
        return e.Code.ToShortString();
    }

    string Describe(SourceState s, WakesConfig config)
    {
        string kind = s.Kind switch
        {
            SourceKind.Boat => "boat",
            SourceKind.Mob => "mob",
            SourceKind.Item => "item",
            SourceKind.Bobber => "bobber",
            SourceKind.Projectile => "projectile",
            SourceKind.Player => "player",
            _ => s.Kind.ToString(),
        };
        string head = $"{kind}, rule {(s.Verdict == SourceVerdict.ModDisabled ? "-" : s.Rule.Name)}";
        if (s.Hull is { } hull) head += F($", hull {2 * hull.HalfWidth:0.##} x {hull.Length:0.##}") + (hull.FromHitbox ? " from the hitbox" : "");
        if (s.Broken) return $"{head}, off after an error, see client-main.log";
        string what = s.Verdict switch
        {
            SourceVerdict.NotYet => "not sampled yet",
            SourceVerdict.ModDisabled => "skipped: disableMod",
            SourceVerdict.Dead => "skipped: dead",
            SourceVerdict.OtherDimension => "skipped: another dimension",
            SourceVerdict.Spectator => "skipped: spectator",
            SourceVerdict.Passenger => $"skipped: seated in {(s.Entity as EntityAgent)?.MountedOn?.Entity?.Code.ToShortString() ?? "something"}",
            SourceVerdict.OnDeck => $"skipped: inside the hull of {s.LastDeck?.Code.ToShortString()} #{s.LastDeck?.EntityId}",
            SourceVerdict.RuleOff => "skipped: the rule makes no wake and no clouds",
            SourceVerdict.Muted => F($"skipped: muted {Math.Max(0, s.MuteUntil - clock):0.0} s more after a seat change"),
            SourceVerdict.StaleSnapshots => "skipped: its snapshots are from before you steered it",
            SourceVerdict.OffSurface => F($"skipped: not at a still water surface the wake takes; box {s.LastFeet:0.00}..{s.LastTop:0.00}, ") +
                (s.LastSurface == int.MinValue ? "no surface block in the box's column" : F($"surface {s.LastSurface + SurfaceHeight:0.000}")),
            SourceVerdict.Started => "first sample on the surface",
            SourceVerdict.Jump => F($"skipped: {(s.Hull != null ? "the position or the emission point" : "the position")} jumped {s.LastDistance:0.00} blocks in a step"),
            SourceVerdict.SpeedJump => F($"skipped: the snapshots read {s.LastSpeed:0.0} b/s against {s.RecentSpeed:0.0} lately"),
            SourceVerdict.Still => $"no trail drawn: {Numbers(s, config)}",
            SourceVerdict.Trail => $"trail: {Numbers(s, config)}",
            SourceVerdict.RingsOnly => s.Rule.SimulateWakes ? "rings only, no wake" : "skipped: the rule makes no rings",
            _ => s.Verdict.ToString(),
        };
        string rings = $"rings {s.Rings}" + (s.WeakRings > 0 ? $" and {s.WeakRings} too weak to draw" : "");
        return s.Kind == SourceKind.Projectile ? $"{head}, {what}; {rings}" : $"{head}, {what}; {rings}; jumps {s.Jumps}";
    }

    static string Numbers(SourceState s, WakesConfig config)
    {
        string numbers = F($"{s.LastSpeed:0.00} b/s from {SpeedName(s)}, {s.LastVelocity:0.000} blocks a step, ") +
                         F($"strength {s.LastStrength:0.##}, impulse {s.LastImpulse}, width {s.LastWidth:0.00} ") +
                         F($"(trail {0.8 * s.LastWidth:0.00}, {WakeHandler.TrailPixels(s.LastWidth, config.WakeResolution)} px), ") +
                         F($"surface y {s.LastY}, moved {s.LastDistance:0.000}");
        if (!s.Rule.SimulateWakes) numbers += ", the rule makes no wake";
        if (s.Rule.RenderPlanes) numbers += F($"; bow clouds {(!s.Bow ? "off" : s.BowAstern ? "on at the stern edge, running stern first" : "on")}, {s.BowClouds} so far");
        if (s.Kind == SourceKind.Player)
        {
            numbers += s.LastLead is { } lead ? F($"; swimming, the trail ends {lead:0.00} ahead of pos") : "; not swimming, the trail ends at pos";
        }
        if (s.Hull is not { } hull) return numbers;
        BoatEmissionRule rule = s.Emission;
        return numbers + "; emission " + EmissionText(rule, hull) + F($", first at {s.LastEmitX:0.00} {s.LastEmitZ:0.00}, ") +
               F($"strength x{rule.StrengthMultiplier:0.##} by ") + (rule == BoatEmissionRule.None ? "no boatEmission rule" : $"rule {rule.Code}");
    }

    // "0.5..-0.329 hull lengths (4.40..-2.90 blocks) ahead of pos, 9 points", or the one offset of a single point.
    public static string EmissionText(BoatEmissionRule rule, HullShape hull)
    {
        int n = rule.Points(hull.Length);
        if (n == 1) return F($"{rule.Offset:0.###} hull lengths ({rule.Offset * hull.Length:0.00} blocks) ahead of pos");
        float to = rule.OffsetAt(n - 1, n);
        return F($"{rule.Offset:0.###}..{to:0.###} hull lengths ({rule.Offset * hull.Length:0.00}..{to * hull.Length:0.00} blocks) ahead of pos, {n} points");
    }

    // "bow point 0.3, Wakes' pair spread across a quarter of the hull's width x1.5" or the bow point and the side points.
    public static string CloudText(BoatEmissionRule rule) => rule.CloudSides is { } s
        ? F($"bow point {rule.CloudBow:0.###}, side points {s[0]:0.###} along and {s[1]:0.###} across with {rule.CloudBetween} between on each side, {rule.CloudPointCount} clouds a step")
        : F($"bow point {rule.CloudBow:0.###}, Wakes' pair spread across a quarter of the hull's width x{rule.CloudSpread:0.##}");

    static string SpeedName(SourceState s) => s.LastSpeedSource switch
    {
        SpeedSource.Motion => "motion",
        SpeedSource.Snapshots => F($"snapshots at {s.PacketRate:0.#} packets a second"),
        _ => "position",
    };

    static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        capi.Event.OnEntitySpawn -= OnLoaded;
        capi.Event.OnEntityLoaded -= OnLoaded;
        capi.Event.OnEntityDespawn -= OnDespawn;
        capi.Event.EntityMounted -= OnMounted;
        capi.Event.EntityUnmounted -= OnUnmounted;
        sources.Clear();
        ghosts.Clear();
    }
}
