using System;
using ImmersiveWakes.Debug;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Render;

// One switch for the renderers that draw one effect between them, such as the splash clouds' step in the wake driver
// and their renderer: the first exception of one turns all of them off.
sealed class RenderGuard
{
    readonly ILogger log;
    readonly string effect;

    public RenderGuard(ILogger log, string effect)
    {
        this.log = log;
        this.effect = effect;
    }

    public bool Tripped { get; private set; }

    public void Trip(string pass, Exception e)
    {
        if (Tripped) return;
        Tripped = true;
        log.Warning("{0}", $"The {effect} failed in its {pass} pass and is off until the world is entered again: {e}");
    }
}

// Base of every renderer of the mod. The engine calls OnRenderFrame without a try (ClientEventManager.TriggerRenderStage),
// and an exception there closes the client (vs-client-thread-exceptions-close-world). Here the first one is logged by
// the guard as one Warning and the renderers sharing the guard stop drawing for the rest of the world. A subclass puts
// the GL state it changes back in its own finally, which runs before this catch.
//
// RenderOrder is read when the renderer is registered and again whenever another renderer is registered in the same
// stage (ClientEventManager.RegisterRenderer), so a renderer keeps one stage and one order; an effect that draws at two
// orders has two renderers. The engine ignores RenderRange (vs-renderrange-not-honored): a subclass gates itself.
abstract class SafeRenderer : IRenderer
{
    // The profiling name the engine puts after the stage in the FrameProfiler marks and in the glErrorChecking
    // exception ("OIT-immersivewakes").
    public const string ProfilingName = "immersivewakes";

    protected readonly ICoreClientAPI Capi;
    readonly RenderGuard guard;
    readonly EnumRenderStage stage;
    bool registered;

    protected SafeRenderer(ICoreClientAPI capi, RenderGuard guard, EnumRenderStage stage, double renderOrder, string pass)
    {
        Capi = capi;
        this.guard = guard;
        this.stage = stage;
        PassName = pass;
        RenderOrder = renderOrder;
    }

    public string PassName { get; }
    public double RenderOrder { get; }
    public int RenderRange => 0;

    // Main thread only: RegisterRenderer throws anywhere else.
    public void Register()
    {
        Capi.Event.RegisterRenderer(this, stage, ProfilingName);
        registered = true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (guard.Tripped) return;
        try
        {
            Render(deltaTime);
        }
        catch (Exception e)
        {
            guard.Trip(PassName, e);
        }
    }

    protected abstract void Render(float deltaTime);

    // On SIGINT or SIGTERM Dispose runs on the signal's thread, where the unregister throws (vs-mod-gl-resource-lifecycle);
    // the engine's renderer lists die with the world all the same.
    public virtual void Dispose()
    {
        if (!registered) return;
        registered = false;
        try
        {
            Capi.Event.UnregisterRenderer(this, stage);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

// A renderer the GL state probe sees: while the probe marks frames its draw runs inside a debug group named after its
// pass, and the probe learns whether it issued its draw.
abstract class ProbedRenderer : SafeRenderer
{
    readonly WakesSession session;

    protected ProbedRenderer(WakesSession session, RenderGuard guard, EnumRenderStage stage, double renderOrder, string pass)
        : base(session.Capi, guard, stage, renderOrder, pass)
    {
        this.session = session;
    }

    protected sealed override void Render(float deltaTime)
    {
        GlStateProbe? probe = session.Probe;
        bool group = probe?.PushGroup(PassName) == true;
        bool drew;
        try
        {
            drew = Draw();
        }
        finally
        {
            probe?.PopGroup(group);
        }
        probe?.Note(PassName, RenderOrder, drew ? DoneWord : "idle");
    }

    // Whether the pass issued its draw.
    protected abstract bool Draw();

    // The probe's word for a frame in which Draw returned true. A reference reading wants no "drew" in its frame.
    protected virtual string DoneWord => "drew";
}
