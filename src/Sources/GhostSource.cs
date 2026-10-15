using System;
using ImmersiveWakes.Config;
using ImmersiveWakes.Effects;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Sources;

enum GhostPath
{
    Line,
    Circle,
}

// The hull a ghost boat goes with: that of the first boat of the type met in this world, with the code its
// boatEmission rule is looked up by.
sealed record GhostBoat(string Name, AssetLocation Code, HullShape Hull);

// ".wakes ghost": an invisible source that moves at a set speed and width over the water, so the autorun and the load
// of T05.09 need nobody in a boat. It moves 0.05 s of its path on every step of the core, whatever the frame rate,
// and hands the segment to AddTrail as a source on the surface would, with the speed in blocks per step. A line runs
// Length blocks from its start and back; a circle turns around its centre. The water is the block row found under the
// player when the ghost was made; off that water AddTrail queues nothing.
//
// A ghost boat goes bow first along its path with the hull of a boat type and draws the row of points of the type's
// boatEmission rule through SourceTracker.AddRow, as that boat would on the same course. At the end of a line it turns
// on the spot, and the step of the turn draws nothing: the row would sweep a stripe the hull's length across it,
// which the jump threshold of a real boat drops as well.
//
// A ghost spawns bow clouds as a source of its kind would: a ghost boat under boatSpawning at the cloud points of its
// type's boatEmission rule from its bow edge, a ghost of a width under playerSpawning as Wakes' pair from that width
// ahead, both along the path, so the autorun sees the threshold of T06.03 and the load of T05.09 includes the clouds.
sealed class GhostSource
{
    public const double Length = 24, Radius = 8, MaxSpeed = 40;
    public const float MaxWidth = 8;
    const double StepSeconds = 0.05;

    readonly GhostPath path;
    readonly double originX, originZ, dirX, dirZ, radius;
    double travelled;
    BoatEmissionRule rule = BoatEmissionRule.None;
    WakesConfig? ruleConfig;

    // A line starts at (x, z) and runs along (dirX, dirZ), a unit vector; a circle has its centre at (x, z).
    public GhostSource(GhostPath path, double x, double z, int y, double dirX, double dirZ, double radius, double speed, float width, GhostBoat? boat)
    {
        this.path = path;
        originX = x;
        originZ = z;
        this.dirX = dirX;
        this.dirZ = dirZ;
        this.radius = radius;
        Y = y;
        Speed = speed;
        Boat = boat;
        Width = boat != null ? 2 * boat.Hull.HalfWidth : width;
    }

    public GhostPath Path => path;
    public int Y { get; }

    // Blocks a second.
    public double Speed { get; }
    public float Width { get; }
    public GhostBoat? Boat { get; }

    // Clouds born at this ghost's bow.
    public long BowClouds { get; private set; }

    public void Step(ITrailSink core, SplashCloudPool clouds, WakesConfig config)
    {
        double from = travelled;
        travelled += Speed * StepSeconds;
        (double fromX, double fromZ) = At(from);
        (double toX, double toZ) = At(travelled);
        double velocity = Speed * StepSeconds;
        if (Boat is not { } boat)
        {
            core.AddTrail(fromX, fromZ, toX, toZ, Y, config.InitialStrength, velocity, Width);
            double dx = toX - fromX, dz = toZ - fromZ, length = Math.Sqrt(dx * dx + dz * dz);
            if (SplashCloudPool.Wants(config.PlayerRule, config, velocity) && length > 1e-9)
            {
                BowClouds += clouds.SpawnBow(toX, toZ, Y, dx / length, dz / length, Width, Width, velocity, local: false, BoatEmissionRule.None);
            }
            return;
        }
        if (path == GhostPath.Line && Leg(from) != Leg(travelled)) return;
        BoatEmissionRule emission = RuleOf(config);
        double yaw = Yaw(travelled);
        SourceTracker.AddRow(core, boat.Hull, emission, emission.Points(boat.Hull.Length), fromX, fromZ, Yaw(from),
            toX, toZ, yaw, Y, config.InitialStrength * emission.StrengthMultiplier, velocity);
        if (SplashCloudPool.Wants(config.BoatRule, config, velocity))
        {
            BowClouds += clouds.SpawnBow(toX, toZ, Y, Math.Sin(yaw), Math.Cos(yaw), boat.Hull.Bow, Width, velocity, local: false, emission);
        }
    }

    // Looked up again only after .wakes reload, as for a boat.
    public BoatEmissionRule RuleOf(WakesConfig config)
    {
        if (Boat != null && ruleConfig != config)
        {
            rule = config.EmissionFor(Boat.Code);
            ruleConfig = config;
        }
        return rule;
    }

    public (double X, double Z) Position => At(travelled);

    // The yaw a ghost boat goes with now: its bow along the path.
    public double Heading => Yaw(travelled);

    (double X, double Z) At(double s)
    {
        if (path == GhostPath.Circle)
        {
            double a = s / radius;
            return (originX + radius * Math.Cos(a), originZ + radius * Math.Sin(a));
        }
        double d = s % (2 * Length);
        if (d > Length) d = 2 * Length - d;
        return (originX + dirX * d, originZ + dirZ * d);
    }

    // Out and back along a line: even legs go along (dirX, dirZ).
    static long Leg(double s) => (long)Math.Floor(s / Length);

    // The yaw of a boat whose bow points along the path, the bow being (sin yaw, cos yaw) as for an entity.
    double Yaw(double s)
    {
        if (path == GhostPath.Circle)
        {
            double a = s / radius;
            return Math.Atan2(-Math.Sin(a), Math.Cos(a));
        }
        return Leg(s) % 2 == 0 ? Math.Atan2(dirX, dirZ) : Math.Atan2(-dirX, -dirZ);
    }

    // The surface the wake takes nearest to the player: the player's own column first, then rings out to Reach blocks.
    // A column is decided by its first fluid from the top, as in DebugSplash.
    public static bool FindWater(IBlockAccessor blocks, IWaterProbe water, double px, double feetY, double pz, out int x, out int y, out int z)
    {
        const int Reach = 8, Above = 8, Below = 16;
        int cx = (int)Math.Floor(px), cy = (int)Math.Floor(feetY), cz = (int)Math.Floor(pz);
        for (int r = 0; r <= Reach; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    for (int by = cy + Above; by >= cy - Below; by--)
                    {
                        if (blocks.GetBlockRaw(cx + dx, by, cz + dz, BlockLayersAccess.Fluid).Id == 0) continue;
                        if (water.IsSurfaceWater(cx + dx, by, cz + dz))
                        {
                            (x, y, z) = (cx + dx, by, cz + dz);
                            return true;
                        }
                        break;
                    }
                }
            }
        }
        (x, y, z) = (0, 0, 0);
        return false;
    }
}
