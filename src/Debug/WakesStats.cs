using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ImmersiveWakes.Effects;
using ImmersiveWakes.Game;
using ImmersiveWakes.Render;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Debug;

// What ".wakes stats" prints over a window, which each printout closes and starts again, with the setup the measures
// were taken in, so one reply of the autorun report stands on its own. The core part: steps a second; the jobs of the
// simulation thread (T05.11), the steps that waited for a busy thread and those dropped; CPU time on the main thread
// per step (the sources' sampling with the clouds' step and their births, the hand-off and, per step of a job, everything the job cost the main thread with
// the upload of its output, the 2 ms of the budget), CPU time on the thread per step (the replay of the trails, the
// core's tick and drawing the atlas inside it: stretching the heights and colouring), the time from a hand-off to its
// output on the GPU, the waits of commands for a running job; bytes allocated per step on either thread and gen0
// collections for И15 of T05.09, the nodes with their ages and a hash of their texels, which shows a repaint on pause. The render part: the wake's quads and chunks, what the last colour pass drew, and per upload of
// WakeUpload the CPU time of the strip and of the mesh write (corners and UpdateMesh), their sum for an upload with a
// mesh write, which follows the steps of a frame (the 0.5 ms of T05.05 at 300 nodes), the quads written, the rows of
// the strip and the bytes each part allocated; GPU time from GpuTimer, read when the line is built.
// The GPU time of the strip is not the frame's cost: at 1000-2000 nodes single queries read 85-94 ms while the frames
// ran at over 100 FPS (T05.05). A guess, not checked: the query also spans the transfer from the CPU and the wait for
// the next command batch. Oceanity of the player's column tells a lag of the quads behind the water from a failure of
// the wave: the shader divides the wave by 5 without it until T06.01. The clouds part: the clouds alive in the pool, those
// born, lived out and dropped from a full ring in the window, the clouds drawn in the last frame, the CPU time of their
// step on the main thread and of filling and sending their instances per frame with the bytes that allocated, and the
// GPU time of their pass with the upload of the instances. The main thread's cost of a job with the upload takes in the
// traced foam's step, which runs in the upload. With VS Path Tracing the trace part (TraceFoam.StatsLine):
// frames under Silent and those that drew the traced foam, its steps with the CPU time of the bake, the copy and the
// mesh, the mesh writes with the bytes the Recorder's copy allocated, and the walks that kept the same set; then the
// traced clouds (TraceClouds.StatsLine): frames that drew them, their walks with the CPU time of collecting the clouds
// and writing the mesh, the mesh writes and the copies of their images into the atlas.
sealed class WakesStats : IDisposable
{
    readonly ICoreClientAPI capi;
    readonly WakesSession session;
    readonly List<WakeNode> nodes = [];
    // Dimensions 0 to 2 (Dimensions.AltWorld); a node above them counts in the last.
    readonly int[] perDimension = new int[3];
    long windowStart = Stopwatch.GetTimestamp();
    long colourFramesAtStart, stepsAtStart, framesAtStart, refusedAtStart, cornerReadsAtStart, trailsAtStart, jumpsAtStart;
    long tickedAtStart, deferredAtStart, droppedAtStart, trailFailuresAtStart;
    long cloudsSpawnedAtStart, cloudsExpiredAtStart, cloudsEvictedAtStart, cloudDrawsAtStart;
    int gen0AtStart = GC.CollectionCount(0), coreAtStart;
    Cpu upload, mesh, both, sources, cloudStep, cloudFill, handOff, mainPerStep, trails, tick, paint, latency, commandWait;
    Tally mainAllocated, threadAllocated, stepsPerJob, stripAllocated, meshAllocated, rows, written, cloudFillBytes;

    public WakesStats(ICoreClientAPI capi, WakesSession session)
    {
        this.capi = capi;
        this.session = session;
    }

    public GpuTimer Gpu { get; } = new();

    struct Cpu
    {
        public int Count;
        public long Sum, Max;

        public void Add(long ticks)
        {
            Count++;
            Sum += ticks;
            Max = Math.Max(Max, ticks);
        }

        public readonly string Show() => Count == 0 ? "-" : F($"{Ms(Sum) / Count:0.000}/{Ms(Max):0.000}");
    }

    struct Tally
    {
        public int Count;
        public long Sum, Max;

        public void Add(long value)
        {
            Count++;
            Sum += value;
            Max = Math.Max(Max, value);
        }

        public readonly string Show() => Count == 0 ? "-" : F($"{(double)Sum / Count:0}/{Max}");
    }

    // One step recorded on the main thread: Stopwatch ticks of the whole step, of the clouds' step within it, and the
    // bytes it allocated.
    public void AddSources(long ticks, long cloudTicks, long allocatedBytes)
    {
        sources.Add(ticks);
        cloudStep.Add(cloudTicks);
        mainAllocated.Add(allocatedBytes);
    }

    // One hand-off of recorded steps to the thread.
    public void AddHandOff(long ticks) => handOff.Add(ticks);

    // One step the thread ticked, forwarded by the main thread once the job has ended: the replay of its trails, the
    // tick, the drawing inside the tick, the bytes the thread allocated in the step.
    public void AddTick(long trailTicks, long tickTicks, long paintTicks, long allocatedBytes)
    {
        trails.Add(trailTicks);
        tick.Add(tickTicks);
        paint.Add(paintTicks);
        threadAllocated.Add(allocatedBytes);
    }

    // A frame of the cloud renderer that had clouds: Stopwatch ticks of filling and sending the instances and the bytes
    // that allocated.
    public void AddCloudFill(long ticks, long allocatedBytes)
    {
        cloudFill.Add(ticks);
        cloudFillBytes.Add(allocatedBytes);
    }

    // A job whose output went up: its steps, what it cost the main thread (the sources of its steps, the hand-off, the
    // upload) and the time from the hand-off to the end of the upload.
    public void AddJob(int steps, long mainTicks, long latencyTicks)
    {
        if (steps <= 0) return;
        stepsPerJob.Add(steps);
        mainPerStep.Add(mainTicks / steps);
        latency.Add(latencyTicks);
    }

    // A command that waited for a running job.
    public void AddCommandWait(long ticks) => commandWait.Add(ticks);

    // One upload of WakeUpload: Stopwatch ticks of the strip and of the mesh write, -1 for a part it skipped, the rows
    // of the strip, the quads written and the bytes each part allocated. A mesh write follows the steps of a frame or a
    // new core.
    public void AddUpload(long stripTicks, int stripRows, long stripBytes, long meshTicks, int quads, long meshBytes)
    {
        if (stripTicks >= 0)
        {
            upload.Add(stripTicks);
            rows.Add(stripRows);
            stripAllocated.Add(stripBytes);
        }
        if (meshTicks >= 0)
        {
            mesh.Add(meshTicks);
            both.Add(Math.Max(0, stripTicks) + meshTicks);
            written.Add(quads);
            meshAllocated.Add(meshBytes);
        }
    }

    // The first window starts the GPU timer, which stays on for the rest of the world.
    public void Reset()
    {
        upload = mesh = both = sources = cloudStep = cloudFill = handOff = mainPerStep = trails = tick = paint = latency = commandWait = default;
        mainAllocated = threadAllocated = stepsPerJob = stripAllocated = meshAllocated = rows = written = cloudFillBytes = default;
        Gpu.Enabled = true;
        Gpu.Reset();
        windowStart = Stopwatch.GetTimestamp();
        colourFramesAtStart = ColourFrames();
        gen0AtStart = GC.CollectionCount(0);
        WakeDriver? driver = session.Driver;
        stepsAtStart = driver?.Steps ?? 0;
        framesAtStart = driver?.Frames ?? 0;
        coreAtStart = driver?.CoreBuilds ?? 0;
        tickedAtStart = driver?.Ticked ?? 0;
        deferredAtStart = driver?.Deferred ?? 0;
        droppedAtStart = driver?.Dropped ?? 0;
        trailFailuresAtStart = driver?.TrailFailures ?? 0;
        // The core is the thread's while a job runs; a stuck job leaves the last count.
        if (driver != null && driver.WaitForCore()) refusedAtStart = driver.Core.Refused;
        cornerReadsAtStart = session.Wakes?.CornerReads ?? 0;
        trailsAtStart = driver?.Sources.Trails ?? 0;
        jumpsAtStart = driver?.Sources.Jumps ?? 0;
        SplashCloudPool? clouds = session.Clouds;
        cloudsSpawnedAtStart = clouds?.Spawned ?? 0;
        cloudsExpiredAtStart = clouds?.Expired ?? 0;
        cloudsEvictedAtStart = clouds?.Evicted ?? 0;
        cloudDrawsAtStart = session.CloudRenderer?.Draws ?? 0;
        session.Trace?.ResetWindow();
    }

    // Frames in which the colour pass drew.
    long ColourFrames() => session.Overlay?.OitDraws ?? 0;

    public string Line()
    {
        if (session.Driver is { } driver && !driver.WaitForCore()) return "stats: the wake thread is stuck in a job, see client-main.log";
        Gpu.CollectAll();
        double seconds = Ms(Stopwatch.GetTimestamp() - windowStart) / 1000;
        string line = F($"stats: {BuildKind()}; window {seconds:0.0} s, gen0 collections {GC.CollectionCount(0) - gen0AtStart}") +
            $"\n{CoreLine(seconds)}\n{RenderLine()}\n{CloudLine()}";
        // Only with VS Path Tracing; Reset below closes its window too.
        if (session.Trace is { } trace) line += "\n" + trace.StatsLine();
        Reset();
        return line;
    }

    string RenderLine()
    {
        OverlayRenderer? overlay = session.Overlay;
        WakeMesh? wakes = session.Wakes;
        WakeTexture? atlas = session.Atlas;
        string filter = atlas == null ? "-" : atlas.Mipmaps ? "mip" : "nearest";
        long frames = ColourFrames() - colourFramesAtStart;
        string state = overlay?.Off == true ? "overlay off after an error" : session.Driver?.UploadOff == true ? "upload off after an error" : "on";

        GpuTimer.Sample oit = Gpu[GpuChannel.Oit];
        int dropped = 0;
        foreach (GpuChannel c in Enum.GetValues<GpuChannel>()) dropped += Gpu[c].Dropped;

        return $"render: {state}, filter {filter}; " +
               $"wake quads {wakes?.QuadCount ?? 0} of {wakes?.Capacity ?? 0} in {wakes?.Chunks ?? 0} chunks, " +
               $"last colour pass {overlay?.LastQuads ?? 0} quads in view in {overlay?.LastChunks ?? 0} chunks; tiles {session.Tiles?.Count ?? 0}; " +
               F($"{frames} colour frames, glErrorChecking ") + (capi.Settings.Bool["glErrorChecking"] ? "on" : "off") +
               $"; uploads: strip rows mean/max {rows.Show()}, quads per mesh write mean/max {written.Show()}, " +
               $"nodes with corners read {(wakes?.CornerReads ?? 0) - cornerReadsAtStart}" +
               $"; cpu ms mean/max: strip {upload.Show()}, mesh {mesh.Show()}, both per mesh write {both.Show()}" +
               $"; bytes allocated mean/max: strip {stripAllocated.Show()}, mesh {meshAllocated.Show()}" +
               $"; gpu ms mean/max/samples: upload {Show(Gpu[GpuChannel.Upload])}, mesh {Show(Gpu[GpuChannel.Mesh])}, " +
               $"oit {Show(oit)}, dropped {dropped}" +
               $"; probe {session.Probe?.LastResult ?? "not run"}; {Oceanity()}";
    }

    string CloudLine()
    {
        if (session.Clouds is not { } pool) return "clouds: no pool";
        SplashCloudRenderer? renderer = session.CloudRenderer;
        string state = pool.Off ? "off after an error, see client-main.log"
            : session.CloudTexture == null ? "no texture, see client-main.log"
            : session.Programs?.Cloud == null ? "no program, see client-main.log" : "on";
        return $"clouds: {state}; {pool.Count} alive of {SplashCloudPool.Capacity}, in the window {pool.Spawned - cloudsSpawnedAtStart} born, " +
               $"{pool.Expired - cloudsExpiredAtStart} lived out, {pool.Evicted - cloudsEvictedAtStart} dropped from a full pool; " +
               $"{(renderer?.Draws ?? 0) - cloudDrawsAtStart} frames drew, {renderer?.LastDrawn ?? 0} in the last; " +
               $"cpu ms mean/max: step {cloudStep.Show()}, instances per frame {cloudFill.Show()}; " +
               $"bytes allocated by the instances per frame mean/max {cloudFillBytes.Show()}; gpu ms mean/max/samples {Show(Gpu[GpuChannel.Cloud])}";
    }

    string CoreLine(double seconds)
    {
        if (session.Driver is not { } driver) return "core: no driver";
        WakeHandler core = driver.Core;
        long steps = driver.Steps - stepsAtStart, frames = driver.Frames - framesAtStart;
        long ticked = driver.Ticked - tickedAtStart, deferred = driver.Deferred - deferredAtStart, dropped = driver.Dropped - droppedAtStart;
        long failed = driver.TrailFailures - trailFailuresAtStart;
        // A core built during the window counts its refusals from zero.
        long refused = core.Refused - (driver.CoreBuilds == coreAtStart ? refusedAtStart : 0);
        string state = driver.Off ? "off after an error, see client-main.log" : capi.IsGamePaused ? "paused" : "running";
        return F($"core: {state}; {steps} steps, {PerSecond(steps, seconds):0.0}/s, in {frames} frames, {PerSecond(frames, seconds):0.0}/s; ") +
               $"thread: {ticked} steps ticked in {stepsPerJob.Count} jobs, steps per job mean/max {stepsPerJob.Show()}, " +
               $"{deferred} steps recorded while it was busy, {dropped} dropped, {failed} trails that threw; " +
               F($"R{core.Params.Resolution}, ") +
               $"modes {ModeNames(core.Modes)}; nodes {core.NodeCount} of {core.MaxNodes}, chunks {core.ChunkCount}, " +
               $"refused {refused} in the window and {core.Refused} since the core was built, {Nodes(core)}; " +
               $"sources {driver.Sources.Count}, entity trails {driver.Sources.Trails - trailsAtStart} and jumps {driver.Sources.Jumps - jumpsAtStart} in the window, ghosts {driver.Sources.GhostCount}; " +
               $"main thread cpu ms per step mean/max: sources with the clouds' step {sources.Show()}, hand-off per job {handOff.Show()}, " +
               $"all of a job per step with the upload {mainPerStep.Show()}; " +
               $"thread cpu ms per step mean/max: trails {trails.Show()}, tick {tick.Show()}, of it drawing the atlas {paint.Show()}; " +
               $"ms from hand-off to the output uploaded mean/max {latency.Show()}; " +
               $"commands that waited for the thread {commandWait.Count}, ms max {(commandWait.Count == 0 ? "-" : F($"{Ms(commandWait.Max):0.000}"))}; " +
               $"bytes allocated per step mean/max: main thread {mainAllocated.Show()}, thread {threadAllocated.Show()}";
    }

    static double PerSecond(long count, double seconds) => seconds > 0 ? count / seconds : 0;

    public static string ModeNames(CoreModes modes) =>
        $"fade {((modes & CoreModes.AmplitudeFade) != 0 ? "amp" : "age")}, halo {((modes & CoreModes.SyncHalo) != 0 ? "sync" : "lag")}, " +
        $"flood {((modes & CoreModes.EagerNeighbours) != 0 ? "eager" : "wait")}, edge {((modes & CoreModes.SoftEdge) != 0 ? "soft" : "hard")}";

    // Ages of the live nodes and an FNV-1a hash of their slots' texels in traversal order: a repaint of the same
    // nodes changes the hash and leaves the ages. The internal y of the nodes and their count in each dimension tell
    // a wake in dimension 2 from one laid on the same water of dimension 0 (П4, T05.09).
    string Nodes(WakeHandler core)
    {
        core.CollectNodes(nodes);
        if (nodes.Count == 0) return "no live nodes";
        int min = int.MaxValue, max = 0, minY = int.MaxValue, maxY = int.MinValue;
        long sum = 0;
        uint hash = 2166136261;
        WakeAtlasCpu atlas = core.Atlas;
        int[] pixels = atlas.Pixels;
        int t = atlas.SlotTexels, size = atlas.Size;
        Array.Clear(perDimension);
        foreach (WakeNode n in nodes)
        {
            min = Math.Min(min, n.Age);
            max = Math.Max(max, n.Age);
            sum += n.Age;
            minY = Math.Min(minY, n.Y);
            maxY = Math.Max(maxY, n.Y);
            perDimension[Math.Clamp(FloorMath.FloorDiv(n.Y, BlockPos.DimensionBoundary), 0, perDimension.Length - 1)]++;
            (int x0, int z0) = atlas.SlotCorner(n.Slot);
            for (int z = 0; z < t; z++)
            {
                int row = (z0 + z) * size + x0;
                for (int x = 0; x < t; x++) hash = (hash ^ (uint)pixels[row + x]) * 16777619;
            }
        }
        var dims = new List<string>(perDimension.Length);
        for (int d = 0; d < perDimension.Length; d++)
        {
            if (perDimension[d] > 0) dims.Add($"{d}: {perDimension[d]}");
        }
        string text = F($"ages min/mean/max {min}/{(double)sum / nodes.Count:0.0}/{max}, texel hash {hash:x8}, y {minY}..{maxY}, ") +
                      $"by dimension {string.Join(", ", dims)}";
        nodes.Clear();
        return text;
    }

    static string Show(GpuTimer.Sample s) => s.Count == 0 ? "-" : F($"{s.MeanMs:0.000}/{s.MaxMs:0.000}/{s.Count}");

    // The flag the tesselator bakes into the north-west corner of the water cell in the player's column, as
    // ChunkTesselator does it: the region's ocean map read at the four corners of the chunk, then lerped across the
    // chunk. Only water at y seaLevel - 1 gets the flag (FINDINGS "Тесселяция и высота поверхности").
    string Oceanity()
    {
        if (capi.World.Player?.Entity is not { } player) return "oceanity -";
        IBlockAccessor ba = capi.World.BlockAccessor;
        int x = (int)Math.Floor(player.Pos.X), z = (int)Math.Floor(player.Pos.Z);
        int regionSize = ba.RegionSize, chunk = GlobalConstants.ChunkSize;
        IntDataMap2D? map = ba.GetMapRegion(x / regionSize, z / regionSize)?.OceanMap;
        if (map == null || map.InnerSize <= 0) return "oceanity: no ocean map here";
        float fx = (float)(x / chunk * chunk % regionSize) / regionSize * map.InnerSize;
        float fz = (float)(z / chunk * chunk % regionSize) / regionSize * map.InnerSize;
        float value = GameMath.BiLerp(
            map.GetIntLerpedCorrectly(fx, fz), map.GetIntLerpedCorrectly(fx + 1, fz),
            map.GetIntLerpedCorrectly(fx, fz + 1), map.GetIntLerpedCorrectly(fx + 1, fz + 1),
            (float)(x % chunk) / chunk, (float)(z % chunk) / chunk);
        byte flag = (byte)value;
        return F($"oceanity {flag / 255f:0.000} ({flag}/255) in this column, for water at y {capi.World.SeaLevel - 1} only");
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // Timings mean little from an unoptimized build, so every line names the build.
    public static string BuildKind()
    {
        // The compiler marks an unoptimized assembly with DebuggingModes.DisableOptimizations.
        var attr = typeof(WakesStats).Assembly.GetCustomAttribute<DebuggableAttribute>();
        bool optimized = attr == null || !attr.IsJITOptimizerDisabled;
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        return $"{config} optimize={(optimized ? "true" : "false")}";
    }

    public void Dispose() => Gpu.Dispose();
}
