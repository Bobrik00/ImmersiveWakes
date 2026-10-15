using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Sources;

// A boat's hull in its own axes (fork 7): the union of the collision boxes of passivephysicsmultibox, x across the boat,
// y up from Pos, z along it with the bow toward -z. The engine turns the boxes by yaw + pi (adjustBoxesToYaw), so a
// point (x, z) of the boat lies at (-x cos yaw - z sin yaw, x sin yaw - z cos yaw) from Pos, and the bow points along
// (sin yaw, cos yaw), the boat's Motion (FINDINGS "Yaw", "Ванильные лодки"). The engine moves only the centres of its
// world boxes and keeps their sizes on the world axes, so the boxes are read in the boat's axes, from
// OrigCollisionBoxes: raft x -0.75..0.75, z -1.5..1.5; sailed x -1.4..1.4, z -5.9..2.9.
sealed class HullShape
{
    // Protected (BehaviorPassivePhysicsMultiBox.cs:40); read from the live behaviour, they are the boxes its physics turns.
    static readonly FieldInfo? OrigBoxes = typeof(EntityBehaviorPassivePhysicsMultiBox)
        .GetField("OrigCollisionBoxes", BindingFlags.Instance | BindingFlags.NonPublic);

    HullShape(float minX, float maxX, float minY, float maxY, float minZ, float maxZ, bool fromHitbox)
    {
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
        MinZ = minZ;
        MaxZ = maxZ;
        FromHitbox = fromHitbox;
    }

    public float MinX { get; }
    public float MaxX { get; }
    public float MinY { get; }
    public float MaxY { get; }
    public float MinZ { get; }
    public float MaxZ { get; }

    // The boxes could not be read and the square hitbox stands in.
    public bool FromHitbox { get; }

    // The trail takes max|x| on both sides, as Wakes takes the full width of the box.
    public float HalfWidth => Math.Max(Math.Abs(MinX), Math.Abs(MaxX));
    public float Length => MaxZ - MinZ;

    // How far ahead of Pos the bow edge lies; the anchor of the bow effects of E06.
    public float Bow => -MinZ;

    // How far behind Pos the stern edge lies; the leading edge of a boat that runs stern first.
    public float Stern => MaxZ;

    // Null for an entity without the behaviour. reason says why the hitbox stands in, null when the boxes were read.
    public static HullShape? Of(Entity entity, out string? reason)
    {
        reason = null;
        if (entity.GetBehavior<EntityBehaviorPassivePhysicsMultiBox>() is not { } behavior) return null;
        Cuboidf[]? boxes = OrigBoxes?.GetValue(behavior) as Cuboidf[];
        if (OrigBoxes == null) reason = "the engine has no OrigCollisionBoxes field";
        else if (boxes == null || boxes.Length == 0) reason = "the behaviour has no collision boxes";
        if (reason != null)
        {
            Cuboidf hit = entity.CollisionBox;
            return new HullShape(hit.X1, hit.X2, hit.Y1, hit.Y2, hit.Z1, hit.Z2, fromHitbox: true);
        }
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Cuboidf box in boxes!)
        {
            minX = Math.Min(minX, Math.Min(box.X1, box.X2));
            maxX = Math.Max(maxX, Math.Max(box.X1, box.X2));
            minY = Math.Min(minY, Math.Min(box.Y1, box.Y2));
            maxY = Math.Max(maxY, Math.Max(box.Y1, box.Y2));
            minZ = Math.Min(minZ, Math.Min(box.Z1, box.Z2));
            maxZ = Math.Max(maxZ, Math.Max(box.Z1, box.Z2));
        }
        return new HullShape(minX, maxX, minY, maxY, minZ, maxZ, fromHitbox: false);
    }

    // The world offset from Pos of the boat's point (x, z) at this yaw.
    public static (double X, double Z) Turn(double x, double z, double yaw)
    {
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        return (-x * c - z * s, x * s - z * c);
    }

    // The point of a boat's wake (fork 19): offset hull lengths from Pos along the course, toward the bow when positive.
    public (double X, double Z) Emission(double posX, double posZ, double yaw, float offset)
    {
        double along = offset * Length;
        return (posX + along * Math.Sin(yaw), posZ + along * Math.Cos(yaw));
    }

    // Whether the point (x, z) lies inside the hull of a boat standing at (posX, posZ) with this yaw (Н4). The offset
    // goes back into the boat's axes by the transpose of the turn.
    public bool Contains(double posX, double posZ, double yaw, double x, double z)
    {
        double dx = x - posX, dz = z - posZ;
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        double lx = -dx * c + dz * s, lz = -dx * s - dz * c;
        return lx >= MinX && lx <= MaxX && lz >= MinZ && lz <= MaxZ;
    }
}
