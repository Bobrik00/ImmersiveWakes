using ImmersiveWakes.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ImmersiveWakes.Sources;

enum SourceKind
{
    Player,
    Boat,
    // A creature under mobSpawning, a ridden one too (fork 21).
    Mob,
    // Under itemSpawning, as the bobber.
    Item,
    // The fishing bobber, under itemSpawning: it floats and is reeled across the water like an item (fork 21).
    Bobber,
    // Rings alone, no wake (fork 21).
    Projectile,
}

// What the last step did with a source, for ".wakes debug". The numbers next to it in SourceState belong to the
// verdict that set them.
enum SourceVerdict
{
    NotYet,
    ModDisabled,
    Dead,
    OtherDimension,
    Spectator,
    Passenger,
    OnDeck,
    RuleOff,
    Muted,
    StaleSnapshots,
    OffSurface,
    Started,
    Jump,
    SpeedJump,
    Still,
    Trail,
    // A projectile past the skips: it gets its rings and no wake.
    RingsOnly,
}

// Where the speed of the last sample came from.
enum SpeedSource
{
    // Pos.Motion, which the local physics keeps true for the local player and the boat it steers.
    Motion,
    // The interpolator's last two snapshots of an entity the server moves.
    Snapshots,
    // The distance over the time between the samples, when the snapshots say nothing.
    Position,
}

// One source between steps (WakeSpawnerMixin's fields): the last sample on the surface, a recent speed for the jump
// threshold and the end of the mute after boarding or leaving a seat. Classification is done once, so the kind, the
// seats, the hull and the interpolator are kept here (П3).
sealed class SourceState
{
    public SourceState(Entity entity, SourceKind kind, HullShape? hull)
    {
        Entity = entity;
        Kind = kind;
        Hull = hull;
        Mountable = entity.GetInterface<IMountable>();
        Interpolator = entity.GetBehavior<EntityBehaviorInterpolatePosition>();
    }

    public Entity Entity { get; }
    public SourceKind Kind { get; }
    public IMountable? Mountable { get; }

    // A boat's, shared by every boat of its type; null for the others.
    public HullShape? Hull { get; }

    // The boatEmission rule of the config object it was looked up in; a .wakes reload brings a new object.
    public WakesConfig? EmissionConfig;
    public BoatEmissionRule Emission = BoatEmissionRule.None;

    // Null for an entity without one; the local player keeps it, unused.
    public EntityBehaviorInterpolatePosition? Interpolator { get; }

    // The last sample on the surface: x and z of Pos, its yaw, the surface block's internal y, the tracker's clock, and
    // a swimming player's course, a unit vector, zero for anyone else.
    public bool HasPrev;
    public double PrevX, PrevZ, PrevTime, PrevCourseX, PrevCourseZ;
    public float PrevYaw;
    public int PrevY;

    // Blocks a second, smoothed over accepted samples and speed jumps, never over a jump of the position.
    public double RecentSpeed;

    // Packets a second the server sends while the entity moves (RemoteSpeed.Measure): whether it was set and whether it is
    // a player's or its mount's, passed on from its client; the packet number and the tracker's clock where the window
    // measuring it began, the clock negative when there is none; the last packet number seen and when it came.
    public double PacketRate = RemoteSpeed.DefaultRate;
    public bool RateKnown, RateRelayed;
    public int RatePacket, SeenPacket;
    public double RateTime = -1, SeenTime;

    // The speeds of the last snapshot pairs the samples took, a ring of RemoteSpeed.Recent; RemoteCount counts the
    // pairs since the source came back to the surface.
    public readonly double[] RemoteSpeeds = new double[RemoteSpeed.Recent];
    public int RemoteCount;

    public double MuteUntil;

    // The snapshot a boat's interpolator held when the local player left its helm. While steered by the local player the
    // boat gets no position packets, so the first pair after that spans the whole trip (see SourceTracker).
    public bool Stale;
    public PositionSnapshot StaleSnapshot;
    // Whether the local player steered this boat at the last sample that found the player in it; kept after the player
    // leaves, for the stale mark.
    public bool SteeredByMe;

    // An exception in this source's step turns it off for the rest of the world; the others go on.
    public bool Broken;

    public SourceVerdict Verdict;
    public EffectSpawningRule Rule;
    public SpeedSource LastSpeedSource;
    // LastDistance of a boat is the larger of the moves of Pos and of the emission point, over LastDt seconds of the
    // tracker's clock.
    public double LastSpeed, LastVelocity, LastDistance, LastDt;
    // The box and the surface block of the last surface test, y internal; the block is int.MinValue when the column has none.
    public double LastFeet, LastTop;
    public int LastSurface;
    public float LastWidth, LastStrength;
    // Where the last trail of a boat ended at its first emission point, the one at Offset.
    public double LastEmitX, LastEmitZ;
    // Blocks ahead of Pos the last trail of a swimming player ended (swimmerOffset); null for anyone else.
    public double? LastLead;
    public int LastY, LastImpulse;
    // The boat whose hull the source stood in at its last step.
    public Entity? LastDeck;
    public long Jumps;

    // Whether the last sample makes bow clouds, its course, a unit vector, and how far ahead of Pos along it the leading
    // edge lies; the tracker spawns them on every step until the next sample from the sample's PrevX, PrevZ, PrevY,
    // LastVelocity, LastWidth and a boat's Emission (SplashCloudPool.SpawnBow). BowClouds counts the clouds born at this
    // source.
    public bool Bow, BowAstern;
    public double BowCourseX, BowCourseZ, BowReach;
    public long BowClouds;

    // WaterEntryWatcher's last sample: Pos x and z, the feet's internal y, whether they stood in a liquid block, the
    // tracker's clock and Motion.Y.
    public bool EntryHasPrev, EntryWet;
    public double EntryX, EntryZ, EntryFeet, EntryTime, EntryMotionY;

    // A bobber floating at rest, so its next dive is a bite (WaterEntryWatcher.WatchBite).
    public bool BiteArmed;

    // A projectile's path as its interpolator draws it: the corner it left last and whether its feet stood in a liquid
    // block there, how fast it came down to it (blocks per 50 ms, 0 when unknown), the corner it was heading to at the
    // last sample and the same test there, and whether the segment between them has rung its entry.
    public bool HasCorner, CornerWet, NextWet, SegmentRung;
    public PositionSnapshot Corner, Next;
    public double CornerFall;

    // Rings drawn at this source, and entries, bounces and bites whose impulse rounded below 1.
    public long Rings, WeakRings;

    public void ResetPrev() => HasPrev = false;

    public void ResetEntry()
    {
        EntryHasPrev = false;
        HasCorner = false;
    }
}
