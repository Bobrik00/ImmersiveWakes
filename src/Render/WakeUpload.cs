using System;
using System.Diagnostics;
using ImmersiveWakes.Debug;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Render;

// Puts the core's work on the GPU in the Before stage of every frame that finds the simulation thread idle, before the
// overlay draws in OIT and before the frame's steps go to the thread (T05.11): the strip of atlas rows the core drew
// since the last call, through TexSubImage2D with the mip chain rebuilt, and, when a job of the thread ticked the core
// or a new core replaced it, the quads of the live nodes. A repaint outside the step (.wakes reload, on pause too)
// leaves a strip without a tick, so the strip is taken on every call. The driver hands the core in on every call: a
// reload may have replaced it, and the new one has a TakeDirty of its own (T05.02).
//
// Under vspt's Skip the vanilla frame is not shown (vs-vspt-foreign-renderers-silent), so the raster's own work stops
// there: the overlay's quads are cleared once and not written, the mip chain is not rebuilt. Each costs on every step
// at ten sailed boats (0.21 ms for the quads with a copy for vspt's Recorder of 137 bytes a quad, a GenerateMipmap of
// 2048 x 2048), and neither shows. The strip goes up only for the traced foam, which bakes its texels from it
// (1.3 ms of CPU a step at ten sailed boats); without the traced foam the rows add up in WakeAtlasCpu until the first
// upload after Skip takes them. That upload also writes the quads and rebuilds the chain; a frame whose thread is busy
// shows no foam rather than old quads. Then the traced foam takes its step, with a switch of its own.
//
// An exception here stops the uploads for the rest of the world and clears the quads, so the overlay does not go on
// drawing a wake that no longer moves; the core keeps stepping.
sealed class WakeUpload
{
    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly WakesSession session;
    int writtenBuild = -1;
    // The overlay's quads or the mip chain were left out under Skip and wait for the first upload out of it.
    bool quadsBehind, mipsBehind;

    public WakeUpload(ICoreClientAPI capi, ILogger log, WakesSession session)
    {
        this.capi = capi;
        this.log = log;
        this.session = session;
    }

    public bool Off { get; private set; }

    public void Publish(WakeHandler core, int coreBuild, bool ticked)
    {
        if (Off) return;
        bool skip = session.Vspt?.Skip == true;
        bool uploaded;
        try
        {
            uploaded = PublishParts(core, coreBuild, ticked, skip);
        }
        catch (Exception e)
        {
            Off = true;
            log.Warning("{0}", $"The wake upload failed, the wake is not drawn in this world any more: {e}");
            try
            {
                session.Wakes?.Clear();
            }
            catch (Exception clear)
            {
                log.Warning("{0}", $"Clearing the wake mesh failed too: {clear.Message}");
            }
            return;
        }
        session.Trace?.Publish(core, coreBuild, ticked, uploaded, skip);
    }

    // Each part runs inside a GPU timer query of its own channel and is timed by Stopwatch; -1 for a part with nothing
    // to do. Returns whether rows of the strip went up.
    bool PublishParts(WakeHandler core, int coreBuild, bool ticked, bool skip)
    {
        if (session.Atlas is not { } atlas || session.Wakes is not { } mesh) return false;
        WakesStats? stats = session.Stats;
        long upload = -1, write = -1, uploadBytes = 0, writeBytes = 0;

        (int first, int rows) = skip && session.Trace?.Bakes != true ? (0, 0) : core.Atlas.TakeDirty();
        if (rows > 0)
        {
            bool timed = stats?.Gpu.Begin(GpuChannel.Upload) == true;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            try
            {
                atlas.Upload(core.Atlas.Pixels, first, rows, rebuildMips: !skip);
                mipsBehind = skip;
            }
            finally
            {
                upload = Stopwatch.GetTimestamp() - start;
                uploadBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (timed) stats!.Gpu.End();
            }
        }
        if (!skip && mipsBehind)
        {
            atlas.RebuildMips();
            mipsBehind = false;
        }

        // A new core starts without nodes, and its node limit may need another mesh: the old quads go at once, even on
        // pause, or they would show the old wake over slots the new core paints anew.
        bool rebuilt = coreBuild != writtenBuild;
        if (skip)
        {
            if (rebuilt) mesh.Resize(core.MaxNodes);
            if (mesh.QuadCount > 0) mesh.Clear();
            writtenBuild = coreBuild;
            quadsBehind = true;
        }
        else if (ticked || rebuilt || quadsBehind)
        {
            bool timed = stats?.Gpu.Begin(GpuChannel.Mesh) == true;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (rebuilt) mesh.Resize(core.MaxNodes);
                mesh.Write(core, capi.World.BlockAccessor, capi.World.Player.Entity.CameraPos);
                writtenBuild = coreBuild;
                quadsBehind = false;
            }
            finally
            {
                write = Stopwatch.GetTimestamp() - start;
                writeBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (timed) stats!.Gpu.End();
            }
        }

        if (upload >= 0 || write >= 0) stats?.AddUpload(upload, rows, uploadBytes, write, mesh.QuadCount, writeBytes);
        return rows > 0;
    }
}
