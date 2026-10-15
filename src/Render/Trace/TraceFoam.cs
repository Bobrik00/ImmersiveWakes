using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ImmersiveWakes.Config;
using ImmersiveWakes.Simulation;
#if DEBUG
using OpenTK.Graphics.OpenGL;
#endif
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// The wake in the frame of VS Path Tracing (E11, T11.02): with vspt of a tested version the core's live nodes go into
// the traced scene as quads over a region of the vanilla entity atlas, whose texels are those of the wake texture with
// the raster's opacity and fade baked in, drawn from the OIT stage with the region flagged as a blended layer, so the
// tracer stops a ray at a texel with probability alpha (reference/vspt-audit/wf2-A-recorder-foam.md with the changes
// of T10.01 and T11.01 in Plan/11-path-tracing-harmony/02-foam-from-nodes.md).
//
// At the mod's start: the Harmony postfix on vspt (BlendedLayerPatch), then the region (TraceRegion) and the splash
// clouds' region (TraceClouds). vspt bakes the entity atlas' materials at its first full send of the atlas, after
// LevelFinalize, and the flag reaches only a region that exists by then; the regions are taken once the atlas'
// composition has ended (AtlasSpace.Composed), or at BlockTexturesLoaded when it has not
// (vs-atlas-allocate-before-compose-ends). Without vspt, with its engine patch missing, with another version, with no
// room in the atlas or with pathTracingFoam off there is nothing of this but one line in the log, and the mod runs as
// 0.9.1. The clouds go on this path with a switch of their own: without room for their region, or after an error in
// them, the foam goes on without them.
//
// Each step under vspt's Skip (the vanilla frame is not shown), from the wake upload in Before after the strip went
// into the wake texture: the factors per slot, the bake (TraceBake), the copy into the region and the mesh (TraceMesh),
// in that order, so vspt sends the texels and the mesh of a step in one frame (EntityLink sends atlas rectangles before
// poses). Out of Skip nothing runs: the region and the mesh stand, and the first step back in writes them all again.
// The renderer draws only under Silent. Errors here turn this off for the world and leave the raster's foam alone:
// the raster has a switch of its own in WakeUpload. The clouds step from WakeDriver after its steps (StepClouds).
// The hull mask (TraceHulls, T11.05) is refreshed once a frame under Skip by the first of the bake and StepClouds, which
// refreshes it on every frame under Skip while the foam or the clouds are on, so the boats met with no wake around them
// have their footprints by the time a ghost boat of their type needs one.
sealed class TraceFoam : ITracedDraw, IDisposable
{
    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly WakesSession session;
    readonly VsptState vspt;
    readonly RenderGuard guard, cloudGuard;
    readonly int wantRows;
    readonly List<WakeNode> nodes = [];
    // Per slot: the factor, the bits of the hulls over its node, and the node's x and z from the hulls' origin.
    readonly float[] factors = new float[TraceRegion.Columns * TraceRegion.Columns * TraceBake.SlotFloats];
    readonly float[] model = Mat4f.Create();

    BlendedLayerPatch? patch;
    TraceRegion? region;
    TraceMesh? mesh;
    TraceBake? bake;
    TraceHulls? hulls;
    TraceRenderer<TraceFoam>? renderer;
    // Null until the regions are taken, and when the clouds' region was not; cloudState says why.
    TraceClouds? clouds;
    string cloudState = "not taken";
    // waitingForAtlas while subscribed to BlockTexturesLoaded, which the engine raises once a world; waited once it has
    // come, so a region it still cannot take turns the path off instead of waiting again.
    bool waitingForAtlas, waited, levelReady, wasSilent;
    // The region and the mesh may hold what the tracer should no longer see: written again at the next step, drawn
    // only after it.
    bool stale = true;
    int lastBuild = -1;
    // The driver's Before frame of the last step that wrote the region and the mesh.
    long writtenFrame = -1;
    string state;

    // Since the world was entered: steps under Skip, those of them that baked, their CPU ticks and bytes; frames under
    // Silent and draws.
    long steps, bakes, stepTicks, stepMaxTicks, stepBytes, silentFrames, draws;
    // The window of .wakes stats.
    long stepsAt, bakesAt, stepTicksAt, stepBytesAt, silentFramesAt, drawsAt, writesAt, writeTicksAt, writeBytesAt, unchangedAt;
    long stepMaxWindow;
    int lastRows, lastNodes;
#if DEBUG
    // GL errors are read after the first bakes and then every so many: glGetError waits for a threaded driver.
    const int CheckedFirst = 40, CheckEvery = 200;
    long checkedSteps, glErrors;
    ErrorCode firstGlError;
    // .wakes vspt mask: the next bake runs twice, without the hulls' bits and with them, and compares the texels.
    bool maskCheckArmed;
    string maskCheck = "not run";
    float[]? unmasked;
    int[]? before;
#endif

    public TraceFoam(ICoreClientAPI capi, ILogger log, WakesSession session, VsptState vspt)
    {
        this.capi = capi;
        this.log = log;
        this.session = session;
        this.vspt = vspt;
        guard = new RenderGuard(log, "traced foam");
        cloudGuard = new RenderGuard(log, "traced splash clouds");
        wantRows = TraceRegion.RowsFor(session.Config?.Current.MaxNodes ?? WakeHandler.DefaultMaxNodes);
        state = "starting";
        PatchState = "not tried";
        // An exception out of here would take the whole mod off for the world.
        try
        {
            Start();
        }
        catch (Exception e)
        {
            log.Warning("{0}", $"Starting the foam for VS Path Tracing failed, the foam is not drawn in the traced frame and the raster foam is as before: {e}");
            TurnOff($"off, the start failed: {e.GetType().Name}: {e.Message}", notify: false);
        }
    }

    // "on", "paused" by the config, "waiting for the entity atlas", or "off" with the reason.
    public string State => guard.Tripped ? "off after an error, see client-main.log"
        : region != null && session.Config?.Current.TraceFoamOn == false ? $"paused: {WakesConfig.Key(nameof(WakesConfig.PathTracingFoam))} is off in the config" : state;
    string PatchState { get; set; }
    public bool On => region != null && !guard.Tripped;
    public int PageTextureId => region?.PageTextureId ?? 0;

    // Whether a step under Skip would bake: the wake upload leaves the strip alone under Skip when not, and the rows
    // add up in WakeAtlasCpu until the next upload takes them.
    public bool Bakes => On && levelReady && session.Config?.Current.TraceFoamOn != false && session.Programs?.TraceBake != null;

    void Start()
    {
        string version = vspt.Version ?? "?";
        if (session.Config?.Current.TraceFoamOn == false)
        {
            state = $"off: {WakesConfig.Key(nameof(WakesConfig.PathTracingFoam))} is off in the config";
            log.Notification("{0}", $"VS Path Tracing {version} found; {state}, so the foam is not drawn in the traced frame");
            return;
        }
        if (!vspt.Patched)
        {
            state = "off: vspt's scene system does not load, the engine is not patched for it";
            log.Notification("{0}", $"VS Path Tracing {version} found without its engine patch: the foam is not drawn in the traced frame");
            return;
        }
        patch = BlendedLayerPatch.TryApply(capi, log, vspt, out string why);
        PatchState = why;
        if (patch == null)
        {
            state = "off: blended-layer patch " + why;
            return;
        }
        // Wanted before the shader reload of the world's entry, which builds the programs.
        if (session.Programs is { } programs) programs.TraceWanted = true;
        capi.Event.LevelFinalize += OnLevelFinalize;
        // .reload textures redraws every page from the CPU's arrays, the region with zeros.
        capi.Event.ReloadTextures += OnReloadTextures;
        // A new wakeOpacity changes the texels without a repaint.
        if (session.Config is { } config) config.Changed += OnConfigChanged;
        TakeRegion();
    }

    void OnReloadTextures()
    {
        stale = true;
        clouds?.OnReloadTextures();
    }

    // firstPersonEffects reaches the clouds this way on pause, where they do not walk.
    void OnConfigChanged(WakesConfig changed)
    {
        stale = true;
        clouds?.Refresh();
    }

    // At the mod's start the entity atlas is normally composed already (in vspt-blend of 2026-10-14 its last stage ended
    // 2.6 s before StartClientSide), but that stage runs on a pool thread, and an allocation in its middle ends the
    // process (AtlasSpace.Composed). Until it has ended the region waits for BlockTexturesLoaded at the end of the asset
    // load, still before LevelFinalize and vspt's first bake. Nothing throws out of here: neither StartClientSide nor
    // the engine's event would catch it with the patch on.
    void TakeRegion()
    {
        try
        {
            bool? composed = AtlasSpace.Composed(capi.EntityTextureAtlas, out _);
            if (composed != true && !waited)
            {
                waitingForAtlas = true;
                capi.Event.BlockTexturesLoaded += OnBlockTexturesLoaded;
                state = "waiting for the entity atlas";
                return;
            }
            // At BlockTexturesLoaded an unknown state goes ahead: the composition has long ended by then.
            if (composed == false)
            {
                TurnOff("off: the entity atlas was still not composed at BlockTexturesLoaded", notify: true);
                return;
            }
            region = TraceRegion.Take(capi, wantRows, out string why);
            if (region == null)
            {
                TurnOff($"off: {why}", notify: true);
                return;
            }
            mesh = new TraceMesh(capi, region.Slots);
            hulls = new TraceHulls(capi, log, session);
            renderer = new TraceRenderer<TraceFoam>(capi, guard, this, TraceRenderer.FoamOrder, "traced foam");
            renderer.Register();
            TakeClouds();
            BlendedLayerPatch.SubIds = clouds != null ? [region.SubId, clouds.SubId] : [region.SubId];
            state = "on";
            string cloudLine = clouds != null ? $"the splash clouds through the {clouds.Describe()}" : $"the splash clouds are not ({cloudState})";
            log.Notification("{0}", $"VS Path Tracing {vspt.Version}: the foam is drawn in the traced frame through the {region.Describe()}; {cloudLine}; blended-layer patch {PatchState}");
        }
        catch (Exception e)
        {
            log.Warning("{0}", $"Taking the region for VS Path Tracing in the entity atlas failed, the foam is not drawn in the traced frame: {e}");
            TurnOff($"off, the region failed: {e.GetType().Name}: {e.Message}", notify: false);
        }
    }

    // After the foam's region, which is the larger: the packer takes the first free leaf that fits. A failure here
    // leaves the foam on.
    void TakeClouds()
    {
        try
        {
            clouds = TraceClouds.TryCreate(capi, session, vspt, cloudGuard, out string why);
            cloudState = clouds != null ? "on" : $"off: {why}";
        }
        catch (Exception e)
        {
            clouds = null;
            cloudState = $"off, the region failed: {e.GetType().Name}: {e.Message}";
            log.Warning("{0}", $"Taking the splash clouds' region for VS Path Tracing failed, the clouds are not drawn in the traced frame: {e}");
        }
    }

    void OnBlockTexturesLoaded()
    {
        capi.Event.BlockTexturesLoaded -= OnBlockTexturesLoaded;
        waitingForAtlas = false;
        waited = true;
        if (patch != null && region == null) TakeRegion();
    }

    // The atlas framebuffer RenderTextureIntoAtlas draws through exists from BlockTexturesLoaded on; the world from here.
    void OnLevelFinalize() => levelReady = true;

    // Everything taken goes, the patch before the regions: the postfix must not flag a sub id the atlas has handed back.
    // The regions last: what could throw there leaves nothing else behind.
    void TurnOff(string reason, bool notify)
    {
        state = reason;
        if (notify) log.Notification("{0}", $"VS Path Tracing {vspt.Version}: the foam is not drawn in the traced frame ({reason}); the raster foam is as before");
        if (session.Programs is { } programs) programs.TraceWanted = false;
        renderer?.Dispose();
        renderer = null;
        if (patch != null)
        {
            patch.Dispose();
            patch = null;
            PatchState = "taken off";
        }
        mesh?.Dispose();
        mesh = null;
        bake?.Dispose();
        bake = null;
        hulls?.Dispose();
        hulls = null;
        TraceClouds? takenClouds = clouds;
        clouds = null;
        if (takenClouds != null) cloudState = "off with the foam";
        TraceRegion? taken = region;
        region = null;
        try
        {
            takenClouds?.Dispose();
        }
        finally
        {
            taken?.Dispose();
        }
    }

    // From WakeUpload, once the strip of the frame is in the wake texture: ticked when a job of the thread ticked the
    // core, uploaded when rows of the strip went up (a repaint on pause too), coreBuild the core's number, skip vspt's
    // Skip of the frame.
    public void Publish(WakeHandler core, int coreBuild, bool ticked, bool uploaded, bool skip)
    {
        if (!On || mesh == null || !levelReady) return;
        if (!skip || session.Config?.Current.TraceFoamOn == false)
        {
            stale = true;
            return;
        }
        // Without the bake program the region would stand still under a mesh that goes on moving: nothing is drawn.
        if (session.Programs?.TraceBake is not { } prog || session.Atlas is not { TextureId: not 0 } atlas)
        {
            stale = true;
            return;
        }
        bool rebuilt = coreBuild != lastBuild;
        if (!ticked && !uploaded && !rebuilt && !stale) return;
        try
        {
#if DEBUG
            bool check = bakes < CheckedFirst || bakes % CheckEvery == 0;
            if (check) DrainGlErrors();
#endif
            Step(core, prog, atlas.TextureId, force: stale || rebuilt);
#if DEBUG
            if (check) CheckGlError();
#endif
            lastBuild = coreBuild;
            stale = false;
            writtenFrame = session.Driver?.Frames ?? -1;
        }
        catch (Exception e)
        {
            guard.Trip("step", e);
        }
    }

    // From WakeDriver once a Before frame, after the frame's steps; stepped when it made steps of the clouds. The clouds
    // walk only under Skip with the path on, as the foam steps; the hulls are refreshed on every such frame.
    // Nothing throws out of here: the driver's guard would take the whole wake off.
    public void StepClouds(bool stepped)
    {
        if (!levelReady) return;
        bool live = vspt.Skip && session.Config?.Current.TraceFoamOn != false;
        // TraceHulls catches its own errors, the camera's among them.
        if (live && hulls != null && (On || clouds != null)) hulls.Refresh(session.Driver?.Frames ?? -1);
        if (clouds == null) return;
        try
        {
            // Without the program nothing is drawn, and a walk would hand the Recorder copies for nothing.
            if (!live || session.Programs?.Trace == null)
            {
                clouds.Stand();
                return;
            }
            clouds.Step(stepped, hulls);
        }
        catch (Exception e)
        {
            cloudGuard.Trip("step", e);
        }
    }

    void Step(WakeHandler core, IShaderProgram prog, int wakeTexture, bool force)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        Vec3d camera = capi.World.Player.Entity.CameraPos;
        core.CollectNodes(nodes);
        try
        {
            lastNodes = nodes.Count;
            hulls!.Refresh(session.Driver?.Frames ?? -1);
            int top = Factors(camera, hulls);
            if (top == 0)
            {
                mesh!.Clear();
                lastRows = 0;
                return;
            }
            int rows = region!.CopyRows(top);
            bake ??= new TraceBake(capi, TraceRegion.Width, region.Height);
            // The scissor flag is not read back (glIsEnabled waits for a threaded driver): it is on while the GUI's
            // scissor stack holds a rectangle, and it would clip both draws.
            bool scissor = capi.Render.ScissorStack.Count > 0, copied = false;
            if (scissor) capi.Render.GlScissorFlag(false);
            try
            {
#if DEBUG
                if (maskCheckArmed) BakeUnmasked(bake, prog, wakeTexture, rows);
#endif
                bake.Run(prog, wakeTexture, factors, rows, hulls);
#if DEBUG
                if (maskCheckArmed) CompareMask(bake, rows);
#endif
                region.Copy(bake.Target, rows);
                copied = true;
            }
            finally
            {
                if (!copied) bake.Restore();
                if (scissor) capi.Render.GlScissorFlag(true);
            }
            bakes++;
            lastRows = rows;
            mesh!.Write(nodes, region, capi.World.BlockAccessor, camera, force);
        }
        finally
        {
            nodes.Clear();
            long ticks = Stopwatch.GetTimestamp() - start;
            steps++;
            stepTicks += ticks;
            stepMaxTicks = Math.Max(stepMaxTicks, ticks);
            stepMaxWindow = Math.Max(stepMaxWindow, ticks);
            stepBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    // The factor of each node's slot in the region, as wakessurface.vsh gives a vertex: wakeOpacity times the far
    // alpha times the fade with distance, by the horizontal distance of the block's centre from the camera; then the
    // bits of the hulls over the node and the node's place from the hulls' origin. Returns one past the highest slot
    // set, 0 for none.
    int Factors(Vec3d camera, TraceHulls over)
    {
        WakeLook look = session.Overlay?.Look ?? new WakeLook();
        float opacity = session.Config?.Current.WakeOpacity ?? WakesConfig.DefaultWakeOpacity;
        (float from, float to) = look.Fade((float)Math.Sqrt(capi.Render.DefaultFrustumCuller.ViewDistanceSq));
        float far = look.FarAlpha;
        int slots = region!.Slots, top = 0;
        foreach (WakeNode node in nodes)
        {
            int slot = node.Slot;
            if ((uint)slot >= (uint)slots) continue;
            double dx = node.X + 0.5 - camera.X, dz = node.Z + 0.5 - camera.Z;
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            int f = slot * TraceBake.SlotFloats;
            factors[f] = opacity * (1 + (far - 1) * SmoothStep(WakeLook.FarFrom, WakeLook.FarTo, d)) * (1 - SmoothStep(from, to, d));
            factors[f + 1] = over.Count > 0 ? over.Bits(node.X, node.Y, node.Z) : 0;
            factors[f + 2] = node.X - over.OriginX;
            factors[f + 3] = node.Z - over.OriginZ;
            if (slot >= top) top = slot + 1;
        }
        return top;
    }

    static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    // From the renderer, once a frame of the OIT stage: whether to draw, with what. A run of vspt's frames that starts
    // anew finds in the region and the mesh what the last run left, unless the step of this very frame on Before 1.1
    // wrote them; otherwise nothing is drawn until a step has.
    public bool Frame([NotNullWhen(true)] out MeshRef? meshRef, [NotNullWhen(true)] out IShaderProgram? prog)
    {
        meshRef = null;
        prog = null;
        bool silent = vspt.Silent;
        if (silent && !wasSilent && writtenFrame != (session.Driver?.Frames ?? -2)) stale = true;
        wasSilent = silent;
        if (!silent) return false;
        silentFrames++;
        if (stale || mesh is not { QuadCount: > 0, Ref: { } r } || session.Programs?.Trace is not { } p
            || session.Config?.Current.TraceFoamOn == false || session.Driver is not { Off: false, UploadOff: false })
        {
            return false;
        }
        meshRef = r;
        prog = p;
        return true;
    }

    public void NoteDraw() => draws++;

    // The translation in [12..14] (column-major), as the Recorder and vspt's EntityLink read it: the anchor relative to
    // the player's CameraPos, lifted by WakeLook's lift as the raster's quads.
    public float[] ModelMatrix()
    {
        Vec3d camera = capi.World.Player.Entity.CameraPos;
        float lift = session.Overlay?.Look.Lift ?? WakeLook.DefaultLift;
        model[12] = (float)((mesh?.AnchorX ?? 0) - camera.X);
        model[13] = (float)((mesh?.AnchorY ?? 0) - camera.Y + lift);
        model[14] = (float)((mesh?.AnchorZ ?? 0) - camera.Z);
        return model;
    }

#if DEBUG
    static void DrainGlErrors()
    {
        for (int i = 0; i < 16 && GL.GetError() != ErrorCode.NoError; i++)
        {
        }
    }

    void CheckGlError()
    {
        checkedSteps++;
        ErrorCode error = GL.GetError();
        if (error == ErrorCode.NoError || glErrors++ > 0) return;
        firstGlError = error;
        log.Warning("{0}", $"Traced foam: GL error {error} after the bake and the copy into the entity atlas");
    }

    // '.wakes vspt mask'; the answer comes with the next '.wakes vspt' after a bake.
    public string ArmMaskCheck()
    {
        if (hulls == null) return "no traced foam";
        maskCheckArmed = true;
        maskCheck = "armed, runs at the next bake";
        return $"mask check armed; {hulls.StatsLine()}";
    }

    // The bake of this step with every slot's hull bits cleared, read back; the bake with them follows.
    void BakeUnmasked(TraceBake into, IShaderProgram prog, int wakeTexture, int rows)
    {
        unmasked ??= new float[factors.Length];
        Array.Copy(factors, unmasked, factors.Length);
        for (int i = 1; i < unmasked.Length; i += TraceBake.SlotFloats) unmasked[i] = 0;
        into.Run(prog, wakeTexture, unmasked, rows, hulls!);
        before = into.Read(rows, before);
    }

    // Every texel of the nodes' slots after the bake against the one before the mask: under a plate (the bake's test
    // in double) alpha 0, elsewhere the same texel.
    void CompareMask(TraceBake from, int rows)
    {
        maskCheckArmed = false;
        int[] after = from.Read(rows, null);
        const int t = TraceRegion.Texels;
        long under = 0, hadFoam = 0, left = 0, offChanged = 0, freeChanged = 0;
        int slotsUnder = 0;
        foreach (WakeNode node in nodes)
        {
            int slot = node.Slot;
            if ((uint)slot >= (uint)region!.Slots || slot / TraceRegion.Columns >= rows) continue;
            int bits = (int)factors[slot * TraceBake.SlotFloats + 1];
            if (bits != 0) slotsUnder++;
            int x0 = slot % TraceRegion.Columns * t, y0 = slot / TraceRegion.Columns * t;
            for (int j = 0; j < t; j++)
            {
                for (int i = 0; i < t; i++)
                {
                    int k = (y0 + j) * TraceRegion.Width + x0 + i;
                    if (bits == 0)
                    {
                        if (after[k] != before![k]) freeChanged++;
                        continue;
                    }
                    double x = node.X + (i + 0.5) / t, z = node.Z + (j + 0.5) / t;
                    bool covered = false;
                    for (int h = 0; h < TraceHulls.Max && !covered; h++) covered = (bits & 1 << h) != 0 && hulls!.Covers(h, x, z);
                    if (!covered)
                    {
                        if (after[k] != before![k]) offChanged++;
                        continue;
                    }
                    under++;
                    if (before![k] >>> 24 != 0) hadFoam++;
                    if (after[k] >>> 24 != 0) left++;
                }
            }
        }
        maskCheck = string.Create(CultureInfo.InvariantCulture,
            $"{nodes.Count} nodes, {slotsUnder} slots under {hulls!.Count} hulls; {under} texels under plates, {hadFoam} of them with foam before the mask, " +
            $"{left} with alpha after it; texels changed off the plates {offChanged}, in slots under no hull {freeChanged}");
    }
#endif

    static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture);

    // The line of .wakes stats over the window ResetWindow opened, which WakesStats.Reset calls.
    public string StatsLine()
    {
        var sb = new StringBuilder($"trace: {State}");
        if (mesh != null && region != null)
        {
            long n = steps - stepsAt, writes = mesh.Writes - writesAt;
            sb.Append(CultureInfo.InvariantCulture,
                $"; frames under Silent {silentFrames - silentFramesAt}, drawn {draws - drawsAt}; steps {n}, {bakes - bakesAt} of them with a bake, " +
                $"cpu ms per step (nodes, factors, bake, copy, mesh) mean/max {Ms(n > 0 ? (stepTicks - stepTicksAt) / n : 0)}/{Ms(stepMaxWindow)}, " +
                $"bytes per step {(n > 0 ? (stepBytes - stepBytesAt) / n : 0)}; nodes {lastNodes}, quads {mesh.QuadCount} of {mesh.Capacity}, " +
                $"copy rows {lastRows} of {region.Rows}; mesh writes {writes}, same set kept {mesh.Unchanged - unchangedAt}, " +
                $"UpdateMesh ms mean/max {Ms(writes > 0 ? (mesh.WriteTicks - writeTicksAt) / writes : 0)}/{Ms(mesh.WindowMaxTicks)}, " +
                $"KB per write {(writes > 0 ? (mesh.WriteBytes - writeBytesAt) / writes / 1024.0 : 0):0.0}");
            sb.Append("; ").Append(clouds?.StatsLine() ?? $"clouds {cloudState}");
            if (hulls != null) sb.Append("; ").Append(hulls.StatsLine());
        }
        if (patch != null) sb.Append("; ").Append(BlendedLayerPatch.Describe());
        return sb.ToString();
    }

    public void ResetWindow()
    {
        stepsAt = steps;
        bakesAt = bakes;
        stepTicksAt = stepTicks;
        stepBytesAt = stepBytes;
        silentFramesAt = silentFrames;
        drawsAt = draws;
        stepMaxWindow = 0;
        clouds?.ResetWindow();
        hulls?.ResetWindow();
        if (mesh == null) return;
        writesAt = mesh.Writes;
        writeTicksAt = mesh.WriteTicks;
        writeBytesAt = mesh.WriteBytes;
        unchangedAt = mesh.Unchanged;
        mesh.ResetWindowMax();
    }

#if DEBUG
    // '.wakes vspt'.
    public string Status()
    {
        var sb = new StringBuilder(vspt.Describe());
        // Silent is read outside a renderer here, so it says no; the frames under Silent below tell.
        sb.Append($"; Skip {Word(vspt.Skip)}, Silent {Word(vspt.Silent)}; traced foam {State}");
        if (patch != null) sb.Append($"; blended-layer patch {PatchState}, ").Append(BlendedLayerPatch.Describe());
        if (region != null) sb.Append('\n').Append(region.Describe());
        if (mesh != null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nmesh {mesh.QuadCount} of {mesh.Capacity} quads, anchor {mesh.AnchorX} {mesh.AnchorY} {mesh.AnchorZ}, stale {Word(stale)}, " +
                $"level ready {Word(levelReady)}; since the world was entered: steps {steps}, cpu ms mean/max {Ms(steps > 0 ? stepTicks / steps : 0)}/{Ms(stepMaxTicks)}, " +
                $"mesh writes {mesh.Writes}, same set kept {mesh.Unchanged}, UpdateMesh ms mean/max {Ms(mesh.Writes > 0 ? mesh.WriteTicks / mesh.Writes : 0)}/{Ms(mesh.WriteMaxTicks)}, " +
                $"KB per write {(mesh.Writes > 0 ? mesh.WriteBytes / mesh.Writes / 1024.0 : 0):0.0}, nodes with corners read {mesh.CornerReads}; " +
                $"frames under Silent {silentFrames}, drawn {draws}; GL errors {glErrors} of {checkedSteps} checked{(glErrors > 0 ? $" (first {firstGlError})" : "")}");
            string traced = renderer != null ? vspt.Traces(renderer) switch { true => "yes", false => "no", null => "unknown" } : "-";
            // QueryPerformanceCounter, the clock of start_qpc in vspt's vspt-frames-*.csv: marks a measuring window.
            sb.Append($"; Recorder traced the foam's renderer {traced}; qpc {Stopwatch.GetTimestamp()}");
            sb.Append('\n').Append(clouds?.Status() ?? $"traced clouds {cloudState}");
            if (hulls != null) sb.Append('\n').Append(hulls.Status()).Append("\nmask check: ").Append(maskCheck);
        }
        if (session.Programs is { } programs && On && (programs.Trace == null || programs.TraceBake == null)) sb.Append("\nprograms wakestrace/wakestracebake not built, see client-main.log");
        return sb.ToString();
    }

    static string Word(bool on) => on ? "yes" : "no";
#endif

    public void Dispose()
    {
        capi.Event.LevelFinalize -= OnLevelFinalize;
        capi.Event.ReloadTextures -= OnReloadTextures;
        if (session.Config is { } config) config.Changed -= OnConfigChanged;
        if (waitingForAtlas) capi.Event.BlockTexturesLoaded -= OnBlockTexturesLoaded;
        waitingForAtlas = false;
        TurnOff("off, the world was left", notify: false);
    }
}
