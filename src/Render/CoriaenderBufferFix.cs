using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ImmersiveWakes.Render;

// Gives CoriaenderShaders its water buffer back under the water mask of a boat. The fault lies between the engine and
// Coriaender and shows without this mod as well (vs-hidewatersurface-drops-coriaender-gbuffer). Coriaender 1.2.1 makes
// BeforeOIT set seven draw buffers, ColorAttachment0-6; on OIT 0.36 it attaches its water buffer to the seventh and
// clears it, and its chunkliquid on 0.37 writes the water there. The behaviour hidewatersurface (the sailed boat,
// Shipwright's boats) draws its mask on the same 0.36 and then sets six buffers. The engine runs a renderer before the
// ones of equal order registered earlier, so the mask of a boat loaded after Coriaender started runs before it: the
// clear and the water's write both land on GL_NONE, and Coriaender draws its reflections in stripes over the water of
// old frames.
//
// After every renderer of 0.36 and before the water this sets the seven buffers again and clears the seventh as
// Coriaender does. The clear is right in the other order too, where Coriaender cleared before the mask: nothing draws
// into the buffer in between. It runs while a loaded entity has the behaviour; the engine nulls a despawned entity's
// renderer, and the mask returns before its GL calls without one.
sealed class CoriaenderBufferFix : ProbedRenderer
{
    public const string ModId = "coriaendershaders";
    // Right before the water on 0.37, after the water masks of boats and Coriaender's own clear on 0.36.
    public const double Order = 0.369;
    const string MaskBehavior = "hidewatersurface";

    static readonly DrawBuffersEnum[] Buffers =
    [
        DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2,
        DrawBuffersEnum.ColorAttachment3, DrawBuffersEnum.ColorAttachment4, DrawBuffersEnum.ColorAttachment5,
        DrawBuffersEnum.ColorAttachment6,
    ];
    // Coriaender's own clear (CoriaenderWater.OnBeforeOITTerrain) is glClearBufferiv with zeros on its RGBA16F
    // buffer; the same call leaves the buffer as it is in a frame without a mask.
    static readonly int[] Zero = new int[4];

    readonly RenderGuard guard;
    readonly Dictionary<long, Entity> masked = [];

    CoriaenderBufferFix(WakesSession session, RenderGuard guard) : base(session, guard, EnumRenderStage.OIT, Order, "Coriaender buffer")
    {
        this.guard = guard;
        Capi.Event.OnEntitySpawn += OnLoaded;
        Capi.Event.OnEntityLoaded += OnLoaded;
        Capi.Event.OnEntityDespawn += OnDespawn;
        if (Capi.World.LoadedEntities is { } loaded)
        {
            foreach (Entity entity in loaded.Values) OnLoaded(entity);
        }
    }

    // Null without Coriaender: nothing else adds the seventh buffer.
    public static CoriaenderBufferFix? TryCreate(WakesSession session)
    {
        if (!session.Capi.ModLoader.IsModEnabled(ModId)) return null;
        return new CoriaenderBufferFix(session, new RenderGuard(session.Mod.Logger, "Coriaender water buffer fix"));
    }

    // Off for comparing with the masks' state by ".wakes cori off".
    public bool Enabled { get; set; } = true;
    public bool Off => guard.Tripped;
    public int MaskedEntities => masked.Count;
    // Frames in which the buffers were set back.
    public long Restores { get; private set; }

    // It draws nothing, so a probe reference may be taken in its frames.
    protected override string DoneWord => "set";

    protected override bool Draw()
    {
        if (!Enabled || masked.Count == 0) return false;
        // DrawBuffers belongs to the bound frame buffer; the OIT stage draws into the transparent one.
        IRenderAPI render = Capi.Render;
        if (render.CurrentFrameBuffer != render.FrameBuffers[(int)EnumFrameBuffer.Transparent]) return false;
        GL.DrawBuffers(Buffers.Length, Buffers);
        GL.ClearBuffer(ClearBuffer.Color, 6, Zero);
        Restores++;
        return true;
    }

    void OnLoaded(Entity entity)
    {
        if (entity.HasBehavior(MaskBehavior)) masked[entity.EntityId] = entity;
    }

    void OnDespawn(Entity entity, EntityDespawnData reason)
    {
        if (masked.TryGetValue(entity.EntityId, out Entity? known) && known == entity) masked.Remove(entity.EntityId);
    }

    public override void Dispose()
    {
        Capi.Event.OnEntitySpawn -= OnLoaded;
        Capi.Event.OnEntityLoaded -= OnLoaded;
        Capi.Event.OnEntityDespawn -= OnDespawn;
        base.Dispose();
    }
}
