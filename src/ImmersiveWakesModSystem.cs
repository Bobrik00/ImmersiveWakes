using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes;

public class ImmersiveWakesModSystem : ModSystem
{
    // A new ModSystem is created for every world while the assembly lives for the whole process, so all
    // per-world state hangs off this instance field rather than a static (DESIGN П7).
    WakesSession? session;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        try
        {
            // Assigned before Start so Cleanup can dispose whatever Start managed to build before a throw.
            session = new WakesSession(api, Mod);
            session.Start();
            Mod.Logger.Notification("{0}", $"ImmersiveWakes started, v{Mod.Info.Version}");
        }
        catch (Exception e)
        {
            // Not rethrown: the loader never calls Dispose on a system that threw here, and a FormatException
            // escaping this method takes the whole client down (ModLoader.TryRunModPhase).
            Mod.Logger.Error("{0}", $"Failed to start, the mod is off for this world: {e}");
            Cleanup();
        }
    }

    // Also called for a world cancelled on the loading screen, when StartClientSide never ran.
    public override void Dispose()
    {
        Cleanup();
        base.Dispose();
    }

    void Cleanup()
    {
        session?.Dispose();
        session = null;
    }
}
