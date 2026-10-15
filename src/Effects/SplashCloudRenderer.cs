using System;
using System.Diagnostics;
using ImmersiveWakes.Debug;
using ImmersiveWakes.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Effects;

// The splash clouds drawn by instancing (fork 4): one quad facing the camera per cloud with a sprite of Wakes, in the
// OIT stage after the wake on the contract of the overlay (vs-oit-overlay-over-water): no blend and no depth state is
// touched, the stage sets them per buffer. The Opaque stage would not do: a half-clear pixel of a cloud over the water
// would write depth before the water on 0.37, and there would be no water behind it (FINDINGS "Частица `splash_cloud`").
// Vanilla particles take no texture (vs-particles-untexturable), and OptiTime culls the particle calls of mods
// (vs-client-optimizers-particle-cull).
//
// The quad is the mesh, uploaded once; the clouds are instance data in custom floats, written by the pool each frame
// into one array and sent with UpdateMesh, as EntityParticleSystem does. The array is sized for the whole pool, so a
// frame allocates nothing. Clouds stand still while the driver is off: they are not drawn then. Left as the draw made
// them, as after the overlay: the active texture unit and the textures Use() and cloudTex bound.
sealed class SplashCloudRenderer : ProbedRenderer
{
    public const double Order = 0.39;

    // xyz is the quad's corner -1..1 in x and y, uv 0..1 over one frame, v down from the frame's top row.
    static readonly float[] Corners = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0];
    static readonly float[] Uvs = [0, 1, 1, 1, 1, 0, 0, 0];

    readonly WakesSession session;
    readonly float[] values = new float[SplashCloudPool.Capacity * SplashCloudPool.InstanceFloats];
    // The update carries only the instance floats: without xyz and indices UpdateMesh leaves the quad and its index
    // count alone.
    readonly MeshData update;
    MeshRef? meshRef;

    public SplashCloudRenderer(RenderGuard guard, WakesSession session)
        : base(session, guard, EnumRenderStage.OIT, Order, "splash clouds")
    {
        this.session = session;
        var quad = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: false, withFlags: false);
        Array.Copy(Corners, quad.xyz, Corners.Length);
        Array.Copy(Uvs, quad.Uv, Uvs.Length);
        quad.Indices = [0, 1, 2, 0, 2, 3];
        quad.VerticesCount = 4;
        quad.IndicesCount = 6;
        // UploadMesh numbers attributes over the streams present: xyz 0, uv 1, then one per interleaved part.
        quad.CustomFloats = Instances(values);
        quad.CustomFloats.Count = values.Length;
        meshRef = Capi.Render.UploadMesh(quad);
        update = new MeshData(initialiseArrays: false) { CustomFloats = Instances(values) };
    }

    static CustomMeshDataPartFloat Instances(float[] values) => new()
    {
        Values = values,
        Instanced = true,
        StaticDraw = false,
        InterleaveSizes = [4, 4, 4],
        InterleaveStride = SplashCloudPool.InstanceFloats * sizeof(float),
        InterleaveOffsets = [0, 16, 32],
    };

    // Clouds in the last frame drawn and frames that drew.
    public int LastDrawn { get; private set; }
    public long Draws { get; private set; }

    // Under vspt's Silent the draw would go to its Recorder, which drops one with a texture of its own and a program
    // other than the vanilla instanced one (vs-vspt-foreign-renderers-silent): nothing is filled or sent then.
    protected override bool Draw()
    {
        LastDrawn = 0;
        if (session.Vspt?.Silent == true) return false;
        if (meshRef == null || session.Programs is not { Cloud: { } prog } programs || session.CloudTexture is not { TextureId: not 0 } texture
            || session.Driver is not { Off: false } driver || session.Clouds is not { Count: > 0 } pool)
        {
            return false;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        Vec3d cam = Capi.World.Player.Entity.CameraPos;
        double reach = Math.Sqrt(Capi.Render.DefaultFrustumCuller.ViewDistanceSq);
        bool hideLocal = session.Config?.Current.FirstPersonEffects != true && Capi.World.Player.CameraMode == EnumCameraMode.FirstPerson;
        int n = pool.Fill(values, cam, driver.StepFraction, texture.Frames, hideLocal, reach);
        WakesStats? stats = session.Stats;
        if (n == 0)
        {
            stats?.AddCloudFill(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocated);
            return false;
        }
        // The GPU time takes in the upload of the instances, as the wake's Mesh channel takes in its UpdateMesh.
        bool timed = stats?.Gpu.Begin(GpuChannel.Cloud) == true;
        try
        {
            update.CustomFloats.Count = n * SplashCloudPool.InstanceFloats;
            Capi.Render.UpdateMesh(meshRef, update);
            stats?.AddCloudFill(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocated);
            // Use() throws while another program is active (vs-custom-mesh-in-engine-stage).
            IShaderProgram? prev = Capi.Render.CurrentActiveShader;
            prev?.Stop();
            try
            {
                try
                {
                    prog.Use();
                    programs.SetScene(prog);
                    prog.Uniform("cloudGrid", (float)texture.Strips, (float)texture.Frames);
                    prog.BindTexture2D("cloudTex", texture.TextureId, OverlayPrograms.FirstTextureUnit);
                    Capi.Render.RenderMeshInstanced(meshRef, n);
                    Draws++;
                    LastDrawn = n;
                }
                finally
                {
                    prog.Stop();
                }
            }
            finally
            {
                prev?.Use();
            }
        }
        finally
        {
            if (timed) stats?.Gpu.End();
        }
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        meshRef?.Dispose();
        meshRef = null;
    }
}
