using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// A mesh of quads for vspt's Recorder, textured from a page of the entity atlas: the foam's (TraceMesh) and the splash
// clouds' (TraceCloudMesh). xyz, uv and flags only (the tracer takes no colour, and the Recorder copies less without
// it), flags 0, quads of 4 vertices and 6 indices only (five exceptions while the Recorder collects geometry turn it
// off for the session, with everything vspt traces through it; refute-mechanism, item 7). One UploadMesh at full
// capacity, then UpdateMesh, as QuadMesh. Every UpdateMesh hands the Recorder a new copy, which goes to the tracer
// whole (vsrtx_live_set_entity_mesh).
//
// The vertices are relative to an anchor near the camera, a multiple of AnchorStep moved once the camera is MoveAfter
// away, so the pose vspt gets from modelMatrix stays near the quads (refute-mechanism, item 5).
abstract class TraceQuads : IDisposable
{
    const int AnchorStep = 32, MoveAfter = 96;

    readonly ICoreClientAPI capi;
    protected readonly MeshData Data;
    MeshRef? meshRef;
    // Whether an anchor has been set since the mesh was made.
    bool anchored;

    protected TraceQuads(ICoreClientAPI capi, int capacity)
    {
        this.capi = capi;
        Capacity = Math.Max(1, capacity);
        Data = new MeshData(Capacity * 4, Capacity * 6, withNormals: false, withUv: true, withRgba: false, withFlags: true);
        Data.XyzStatic = Data.UvStatic = Data.FlagsStatic = Data.IndicesStatic = false;
        QuadMesh.FillIndices(Data.Indices, Capacity);
        // The flags stay 0: bits 0-7 are glow in the tracer, and a glowing vertex casts no shadow.
        Data.VerticesCount = Capacity * 4;
        Data.IndicesCount = Capacity * 6;
        meshRef = capi.Render.UploadMesh(Data);
        Commit(0);
    }

    public int Capacity { get; }
    public MeshRef? Ref => meshRef;
    public int QuadCount { get; private set; }
    public int AnchorX { get; private set; }
    public int AnchorY { get; private set; }
    public int AnchorZ { get; private set; }

    // UpdateMesh calls with quads, the CPU ticks they took and the bytes they allocated (the Recorder's copy under the
    // patched engine).
    public long Writes { get; private set; }
    public long WriteTicks { get; private set; }
    public long WriteMaxTicks { get; private set; }
    // The longest UpdateMesh since ResetWindowMax, for the window of .wakes stats.
    public long WindowMaxTicks { get; private set; }
    public long WriteBytes { get; private set; }
    // Stopwatch timestamp of the last UpdateMesh.
    protected long LastCommit { get; private set; }

    // An anchor at the first write and once the camera is MoveAfter blocks from it along any axis; true when it moved.
    protected bool MoveAnchor(Vec3d camera)
    {
        int cx = (int)Math.Floor(camera.X), cy = (int)Math.Floor(camera.Y), cz = (int)Math.Floor(camera.Z);
        if (anchored && Math.Abs(cx - AnchorX) < MoveAfter && Math.Abs(cy - AnchorY) < MoveAfter && Math.Abs(cz - AnchorZ) < MoveAfter) return false;
        AnchorX = FloorTo(cx);
        AnchorY = FloorTo(cy);
        AnchorZ = FloorTo(cz);
        anchored = true;
        return true;
    }

    static int FloorTo(int v) => (int)Math.Floor(v / (double)AnchorStep) * AnchorStep;

    // Empty until the next write; an empty mesh has no Recorder copy and the renderer does not draw it.
    public void Clear()
    {
        if (QuadCount > 0) Commit(0);
    }

    protected void Commit(int quads)
    {
        if (meshRef == null) return;
        QuadCount = Math.Clamp(quads, 0, Capacity);
        Data.VerticesCount = QuadCount * 4;
        // UpdateMesh takes the new count only while Indices is not null.
        Data.IndicesCount = QuadCount * 6;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        // Under the patched engine the Recorder copies xyz, uv, flags and indices here, Silent or not.
        capi.Render.UpdateMesh(meshRef, Data);
        long ticks = Stopwatch.GetTimestamp() - start;
        LastCommit = Stopwatch.GetTimestamp();
        if (QuadCount == 0) return;
        Writes++;
        WriteTicks += ticks;
        WriteMaxTicks = Math.Max(WriteMaxTicks, ticks);
        WindowMaxTicks = Math.Max(WindowMaxTicks, ticks);
        WriteBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
    }

    public void ResetWindowMax() => WindowMaxTicks = 0;

    public void Dispose()
    {
        meshRef?.Dispose();
        meshRef = null;
        QuadCount = 0;
    }
}
