using System;
using System.Diagnostics;
using ImmersiveWakes.Config;
using ImmersiveWakes.Debug;
using ImmersiveWakes.Effects;
using ImmersiveWakes.Render;
using ImmersiveWakes.Simulation;
using ImmersiveWakes.Sources;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Game;

// Steps the core 20 times a second of game time (DESIGN "Принятые решения"). A tick listener fires once a frame at
// most, about 15 Hz at 60 FPS, so the step runs off an accumulator of frame time, in the Before stage after the local
// player's physics on 1.0, where the sources of T05.03 read positions (FINDINGS "Тайминг клиента", "Позиция и
// скорость"). The render stages run on pause with a live dt: the accumulator stands while the game is paused, or every
// wake would fade in the Esc menu. Time past 0.5 s (10 steps) is dropped as the player's physics drops it, and the
// wakes do not age through it. SpeedOfTime is not in the step.
//
// The core ticks on a thread of its own (fork 20, T05.11). Each frame that finds the thread idle first puts the output
// of its last job on the GPU (WakeUpload: the atlas strip and the quads), then the sources record the frame's steps
// into a StepInput, and the input goes to the thread. A frame that finds the thread busy records its steps for a later
// hand-off and touches neither the core nor the GPU copy. The wake shows a frame after its step at the earliest.
// Commands change the core between frames, after WaitForCore.
//
// The splash clouds step on the main thread with the steps of the core: first the clouds already out, then the sources,
// which spawn new ones at their bows; a new cloud works out its first tick at once and makes it on the next step, as
// in Wakes.
//
// The driver owns the core: builds it from the config, hands it new parameters and colours when the config reloads
// (.wakes reload, a change of the file) and builds a new one for a new resolution or node limit. The core modes of
// .wakes mode live as long as the world (the author's choice, T05.02): a static to keep them for the process would
// break П7.
sealed class WakeDriver : SafeRenderer
{
    public const double Order = 1.1;
    public const double StepSeconds = 0.05;
    const double MaxBacklogSeconds = 0.5;

    // Steps kept for a thread that is still busy, the same half second as the frame backlog; past it the oldest go,
    // and the wake skips them. A frame that finds the thread idle hands all of its steps over.
    const int MaxPendingSteps = 10;

    // How long a command waits for the running job. A job of 10 sailed boats takes about 3 ms.
    const int CommandWaitMs = 1000, StopWaitMs = 2000;

    readonly RenderGuard guard;
    readonly ILogger log;
    readonly WakesSession session;
    readonly WakesConfigFile config;
    readonly WaterProbe water, coreWater;
    readonly WakeUpload upload;
    readonly WakeThread thread;
    StepInput pending = new(), handed = new();
    // Every core of the world gets these until .wakes mode changes them: the two edits of T07.05, the author's choice
    // of 2026-10-10 (DESIGN, "Решения без номера развилки"). A new WakeHandler itself starts with None, as Wakes.
    CoreModes modes = CoreModes.EagerNeighbours | CoreModes.SoftEdge;
    // Goes into every palette built from the config until .wakes look shade changes it.
    float waterShade = WakePalette.DefaultWaterShade;
    double accumulator;
    // threadOff is set on the main thread once it sees the thread's fault: the thread's Fault is read in one place, and
    // the overlay and the clouds, which stop with it, all see one value within a frame.
    bool output, threadOff, trailFaultLogged;

    // Main-thread Stopwatch ticks of the steps in pending and of the job handed, and when that job went.
    long pendingMain, handedMain, handedAt;
    int handedSteps;

    // cloudGuard is the clouds' renderer's: an exception in their step turns off the clouds, not the wake.
    public WakeDriver(ICoreClientAPI capi, ILogger log, WakesSession session, WakesConfigFile config, RenderGuard cloudGuard)
        : this(capi, log, new RenderGuard(log, "wake simulation"), session, config, cloudGuard)
    {
    }

    WakeDriver(ICoreClientAPI capi, ILogger log, RenderGuard guard, WakesSession session, WakesConfigFile config, RenderGuard cloudGuard)
        : base(capi, guard, EnumRenderStage.Before, Order, "step")
    {
        this.guard = guard;
        this.log = log;
        this.session = session;
        this.config = config;
        water = new WaterProbe(capi.World);
        coreWater = new WaterProbe(capi.World);
        upload = new WakeUpload(capi, log, session);
        Clouds = new SplashCloudPool(capi, cloudGuard, water);
        LastApplied = Apply(config.Current);
        // After Apply: the tracker subscribes to entity events, and a constructor that throws is never disposed.
        Sources = new SourceTracker(capi, log, water, Clouds);
        config.Changed += OnConfigChanged;
        // Last: a thread started before a throw above would wait for jobs for the rest of the process.
        thread = new WakeThread("immersivewakes core");
    }

    // Main thread only, and while a job runs only for its reference: everything in it belongs to the thread then.
    public WakeHandler Core { get; private set; } = null!;

    // Cores built in this world; the stats tell a rebuilt core by it without holding the old one and its pool.
    public int CoreBuilds { get; private set; }

    // For the main thread's callers: the sources, the clouds, .wakes splash and .wakes clouds. The core asks a probe of
    // its own.
    public IWaterProbe Water => water;

    public SourceTracker Sources { get; }
    // The splash clouds the driver steps; the sources spawn into it, the cloud renderer draws it.
    public SplashCloudPool Clouds { get; }

    // How far the frame stands past the last step toward the next, 0..1: the clouds are drawn that far along their
    // step. It stands still on pause, as the accumulator does.
    public float StepFraction => (float)(accumulator / StepSeconds);

    // After an exception in the step or in a tick on the thread the wakes stand still for the rest of the world.
    public bool Off => guard.Tripped || threadOff;

    // After an exception in the upload the wake is not drawn for the rest of the world.
    public bool UploadOff => upload.Off;

    // What the last config did to the core, for the reply of .wakes reload.
    public string LastApplied { get; private set; }

    // Steps and Before frames since the world was entered, paused frames included; the stats count a window of them.
    public long Steps { get; private set; }
    public long Frames { get; private set; }

    // Steps the thread has ticked, steps recorded while it was busy, steps dropped for a thread that fell half a
    // second behind, and replayed trails that threw.
    public long Ticked { get; private set; }
    public long Deferred { get; private set; }
    public long Dropped { get; private set; }
    public long TrailFailures { get; private set; }

    public CoreModes Modes => modes;

    // False when the core stayed busy; the modes then stay as they were. A switch of the soft edge repaints the live
    // nodes at once, as new colours do, so it shows on pause too.
    public bool SetModes(CoreModes value)
    {
        if (!WaitForCore()) return false;
        bool repaint = ((modes ^ value) & CoreModes.SoftEdge) != 0;
        modes = value;
        Core.Modes = value;
        if (repaint) Core.Redraw();
        return true;
    }

    public float WaterShade => waterShade;

    // False when the core stayed busy. The live nodes are repainted at once, as with new colours.
    public bool SetWaterShade(float value)
    {
        if (!WaitForCore()) return false;
        // The palette checks the value before the field takes it, or every later Apply would throw on it.
        Core.Palette = Core.Palette.WithWaterShade(value);
        waterShade = value;
        Core.Redraw();
        return true;
    }

    // A command touches the core between frames, once the running job has ended; the thread takes no new one before
    // the next frame. False after a second, which only a thread stuck in a job gives.
    public bool WaitForCore()
    {
        if (thread.Idle) return true;
        long start = Stopwatch.GetTimestamp();
        bool idle = thread.WaitIdle(CommandWaitMs);
        session.Stats?.AddCommandWait(Stopwatch.GetTimestamp() - start);
        if (!idle) log.Warning("{0}", $"The wake thread has run one job for over {CommandWaitMs} ms; the command left the core alone");
        return idle;
    }

    protected override void Render(float deltaTime)
    {
        Frames++;
        // A file saved by ConfigLib's window or by hand reloads here, paused too, before the upload of the frame, as a
        // command between frames would.
        if (config.Poll() is { Taken: true } report)
        {
            log.Notification("{0}", $"{WakesConfigFile.FileName} changed on disk, reloaded: {report.Summary}; {LastApplied}");
        }
        bool idle = thread.Idle;
        if (idle) Collect();
        if (Off) return;
        bool stepped = false;
        if (!Capi.IsGamePaused)
        {
            Sources.Advance(deltaTime);
            accumulator = Math.Min(accumulator + deltaTime, MaxBacklogSeconds);
            while (accumulator >= StepSeconds)
            {
                accumulator -= StepSeconds;
                Step(idle);
                stepped = true;
            }
        }
        // The clouds of VS Path Tracing's frame, once their steps of the frame are made; nothing without vspt.
        session.Trace?.StepClouds(stepped);
        if (idle && pending.Ticks > 0) HandOff();
    }

    // The output of the last job goes up while the thread is idle; an upload of a repaint outside the step, or of a
    // new core's empty mesh, goes up with it or alone.
    void Collect()
    {
        if (threadOff) return;
        if (thread.Fault is { } fault)
        {
            threadOff = true;
            log.Warning("{0}", $"The wake simulation failed on its thread and is off until the world is entered again: {fault}");
            session.Wakes?.Clear();
            return;
        }
        WakesStats? stats = session.Stats;
        if (thread.TakeFinished())
        {
            output = true;
            Ticked += thread.Costs.Count;
            foreach (WakeThread.TickCost c in thread.Costs) stats?.AddTick(c.TrailTicks, c.TickTicks, c.PaintTicks, c.AllocatedBytes);
            if (thread.TrailFailures > 0)
            {
                TrailFailures += thread.TrailFailures;
                if (!trailFaultLogged)
                {
                    trailFaultLogged = true;
                    log.Warning("{0}", $"A wake trail or splash threw on the simulation thread; such calls are skipped and counted by .wakes stats, the other wakes go on: {thread.TrailFault}");
                }
            }
        }
        long start = Stopwatch.GetTimestamp();
        upload.Publish(Core, CoreBuilds, output);
        if (!output) return;
        long end = Stopwatch.GetTimestamp();
        stats?.AddJob(handedSteps, handedMain + end - start, end - handedAt);
        output = false;
    }

    // One step of the clouds and the sources, the sources' trails recorded for the thread. The accumulator holds how far
    // this step stands before the frame, which the sources' bows go back by.
    void Step(bool idle)
    {
        if (!idle && pending.Ticks >= MaxPendingSteps)
        {
            pending.DropOldestStep();
            Dropped++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        Clouds.Step();
        long clouds = Stopwatch.GetTimestamp();
        Sources.Step(pending, config.Current, accumulator / StepSeconds);
        pending.Tick();
        long ticks = Stopwatch.GetTimestamp() - start;
        Steps++;
        if (!idle) Deferred++;
        pendingMain += ticks;
        session.Stats?.AddSources(ticks, clouds - start, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    void HandOff()
    {
        long start = Stopwatch.GetTimestamp();
        (pending, handed) = (handed, pending);
        pending.Clear();
        handedSteps = handed.Ticks;
        if (!thread.Start(Core, handed)) return;
        long end = Stopwatch.GetTimestamp();
        handedMain = pendingMain + end - start;
        pendingMain = 0;
        handedAt = start;
        session.Stats?.AddHandOff(end - start);
    }

    // On a reload of the config, on the main thread. The config is a new object each time and does not say what
    // changed, so the driver compares with the core it has.
    // Should Apply throw, the config file logs it as an Error, and the reply of .wakes reload must not show the last
    // result as this one's.
    void OnConfigChanged(WakesConfig changed)
    {
        LastApplied = "the core did not take this config, see client-main.log";
        if (!WaitForCore())
        {
            LastApplied = "the core stayed busy and did not take this config; .wakes reload again";
            return;
        }
        LastApplied = Apply(changed);
    }

    // The resolution sizes the node arrays and the queue, the node limit the queue's pool and the mesh: either needs a
    // new core, and the live wakes go with the old one; the next upload clears their quads and sizes the mesh anew.
    // Wave factors, flood fill and the tint rule go into the live core, as Wakes reads them on every tick. New colours
    // repaint the live nodes at once, outside the step, so an edit made on pause shows before the pause ends (DESIGN
    // "Принятые решения"). The thread is idle: in the constructor it does not run yet, on reload WaitForCore came first.
    string Apply(WakesConfig c)
    {
        var parameters = new SimulationParams(c.WakeResolution, c.WavePropagationFactor, c.WaveDecayFactor,
            c.FloodFillDistance, c.FloodFillTickDelay, c.CacheNodeWaterColor);
        var palette = new WakePalette(c.Colors, c.WakeColorIntervals, c.BlendStrength, waterShade);
        WakeHandler? old = Core;
        pending.Resolution = handed.Resolution = parameters.Resolution;
        if (old == null || old.Params.Resolution != parameters.Resolution || old.MaxNodes != c.MaxNodes)
        {
            Core = new WakeHandler(parameters, coreWater, c.MaxNodes, atlasPixels: old?.Atlas.Pixels) { Modes = modes, Palette = palette };
            CoreBuilds++;
            return old == null
                ? $"core R{parameters.Resolution}, {Core.MaxNodes} nodes at most"
                : $"new core R{parameters.Resolution}, {Core.MaxNodes} nodes at most (was R{old.Params.Resolution}, {old.MaxNodes}); " +
                  $"the {old.NodeCount} live nodes are gone";
        }
        old.Params = parameters;
        if (old.Palette.SameAs(palette)) return $"core R{parameters.Resolution} keeps its {old.NodeCount} nodes, colours unchanged";
        old.Palette = palette;
        old.Redraw();
        return $"core R{parameters.Resolution} keeps its {old.NodeCount} nodes, repainted with the new colours";
    }

    // The thread goes first: its job reads the world through the core's probe.
    public override void Dispose()
    {
        config.Changed -= OnConfigChanged;
        if (!thread.Stop(StopWaitMs)) log.Warning("{0}", $"The wake thread did not stop within {StopWaitMs} ms of leaving the world");
        Sources.Dispose();
        base.Dispose();
    }
}
