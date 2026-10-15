using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace ImmersiveWakes.Sources;

// Decides once, when an entity spawns or loads, whether it can leave a wake (П3): mods put hundreds of entities into
// LoadedEntities that never touch water, and asking the water under each of them on every step would cost more than
// the wake. A boat is told by its hull behaviour and a seat, not by its class: Shipwright's boats are not EntityBoat
// (fork 7, FINDINGS "Лодки Shipwright"). The bobber is an EntityProjectile, so it is told before the projectiles; it
// floats and is reeled across the water, a source like an item. A projectile is anything that implements the API's
// IProjectile: arrows and spears (EntityProjectile), thrown stones and boulders, snowballs, beenades and thrown items.
// An item is an EntityItem, dropped or thrown from the inventory. A mob is Wakes' LivingEntity in VS: a creature that
// moves itself under controlled physics, a ridden elk or horse among them (fork 21), but not one that lives under
// water or in the air: a fish keeps under the surface and a butterfly over it, and a lake holds dozens of fish
// (FINDINGS "Источники следа"). Mods mark their fish and birds the same way, Habitat comes to the client with the
// entity type. The spectator is a state, not a kind, and the tracker asks for it on every step.
static class SourceClassifier
{
    public static SourceKind? Classify(Entity entity)
    {
        if (entity is EntityPlayer) return SourceKind.Player;
        if (entity.HasBehavior<EntityBehaviorPassivePhysicsMultiBox>() && entity.GetInterface<IMountable>() != null) return SourceKind.Boat;
        if (entity is EntityBobber) return SourceKind.Bobber;
        if (entity is IProjectile) return SourceKind.Projectile;
        if (entity is EntityItem) return SourceKind.Item;
        if (entity is EntityAgent && WhyNotMob(entity) == null) return SourceKind.Mob;
        return null;
    }

    // For ".wakes debug all": why a creature is not a mob, null for a mob.
    public static string? WhyNotMob(Entity entity)
    {
        EnumHabitat habitat = entity.Properties.Habitat;
        if (habitat == EnumHabitat.Underwater) return "lives under water";
        if (habitat == EnumHabitat.Air) return "lives in the air";
        if (!entity.HasBehavior<EntityBehaviorControlledPhysics>()) return "no physics of its own";
        return null;
    }
}
