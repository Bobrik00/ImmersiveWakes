using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ImmersiveWakes.Config;
using ImmersiveWakes.Effects;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// The splash clouds in the frame of VS Path Tracing (T11.04, variant B2b of reference/vspt-audit/wf2-B-clouds.md, the
// author's choice of 2026-10-15), on the foam's path (TraceFoam): quads drawn from the OIT stage over a region of the
// entity atlas that BlendedLayerPatch flags as a blended layer, so the tracer stops a ray at a texel with probability
// alpha. TraceFoam takes the region with its own at the mod's start and owns this object.
//
// The tracer takes the texel and nothing else, so the cloud's fade goes into the texels: the region holds an image for
// each pair of sprite and age in steps, the sprite's frame for that age with its alpha times the fade, an age a column
// and a sprite a row of images. The raster draws a cloud between its step's Now and Next with the fade and the frame of
// the frame's fraction (SplashCloudPool.Fill); a traced quad stands for a whole step, so it takes the middle of the
// step, Fraction, for its place, its fade and its frame. The images are copied into the region on every entry into
// vspt's Skip and after .reload textures, which redraws the page from the CPU's arrays.
//
// The mesh (TraceCloudMesh) is written under Skip once a frame that stepped the clouds, from WakeDriver after the
// frame's steps, with the camera of that frame; on pause it stands, unless clouds were born (.wakes clouds) or the
// config changed. A mesh written before Skip went down is not drawn once vspt shows its frame again, until a walk has
// written it, which the first frame under Skip does. The clouds are what the pool holds, so the boats' cloud rules and
// spawnParticles hold here too; firstPersonEffects and the reach go as in the raster. A cloud over a hull's water mask
// is cut at the highest plate over its centre while the camera is above that plate (TraceHulls.Deck, T11.05): in the
// raster the plates hide by depth what lies under them, and the tracer would show the cloud inside the hull. Errors
// here turn the traced clouds off for the world and leave the traced foam alone.
sealed class TraceClouds : ITracedDraw, IDisposable
{
    // The middle of a step.
    const float Fraction = 0.5f;
    const int Ages = SplashCloudPool.Lifetime;

    readonly ICoreClientAPI capi;
    readonly WakesSession session;
    readonly VsptState vspt;
    readonly RenderGuard guard;
    readonly AtlasRegion region;
    readonly int size;
    readonly SplashCloudPool.TracedCloud[] clouds = new SplashCloudPool.TracedCloud[SplashCloudPool.Capacity];
    readonly float[] model = Mat4f.Create();
    LoadedTexture? images;
    TraceCloudMesh? mesh;
    TraceRenderer<TraceClouds>? renderer;
    // The mesh, or the images in the region, may hold what the tracer should no longer see: written again at the next
    // frame under Skip, drawn only after it.
    bool stale = true, imagesStale = true, wasSilent;
    // The driver's Before frame of the last walk, and the pool's births it had seen.
    long writtenFrame = -1, walkedSpawned = -1;
    int lastClouds;

    // Since the world was entered: walks of the pool, which write or clear the mesh, with their CPU ticks and bytes,
    // copies of the images, frames under Silent and draws; the window of .wakes stats.
    long walks, walkTicks, walkMaxTicks, walkBytes, copies, silentFrames, draws;
    long walksAt, walkTicksAt, walkBytesAt, copiesAt, silentFramesAt, drawsAt, writesAt, writeTicksAt, writeBytesAt;
    long walkMaxWindow;

    TraceClouds(ICoreClientAPI capi, WakesSession session, VsptState vspt, RenderGuard guard, AtlasRegion region, int size)
    {
        this.capi = capi;
        this.session = session;
        this.vspt = vspt;
        this.guard = guard;
        this.region = region;
        this.size = size;
    }

    // Null with the reason when the sprites did not load or the region does not fit; nothing is held then. The
    // composition of the atlas has ended (TraceFoam.TakeRegion).
    public static TraceClouds? TryCreate(ICoreClientAPI capi, WakesSession session, VsptState vspt, RenderGuard guard, out string why)
    {
        if (session.CloudTexture is not { } sprites)
        {
            why = "the cloud sprites did not load";
            return null;
        }
        AtlasRegion? region = AtlasRegion.Take(capi, Ages * sprites.FrameSize, sprites.Strips * sprites.FrameSize, out why);
        if (region == null) return null;
        var clouds = new TraceClouds(capi, session, vspt, guard, region, sprites.FrameSize);
        try
        {
            clouds.images = Bake(capi, sprites, region.Width, region.Height);
            clouds.mesh = new TraceCloudMesh(capi, SplashCloudPool.Capacity);
            clouds.renderer = new TraceRenderer<TraceClouds>(capi, guard, clouds, TraceRenderer.CloudOrder, "traced splash clouds");
            clouds.renderer.Register();
        }
        catch
        {
            clouds.Dispose();
            throw;
        }
        return clouds;
    }

    public int SubId => region.SubId;
    public int PageTextureId => region.PageTextureId;
    public string State => guard.Tripped ? "off after an error, see client-main.log"
        : session.Clouds is not { Off: false } ? "off: the splash clouds' pool is off after an error"
        : session.Driver is not { Off: false } ? "off: the wake simulation is off after an error"
        : session.Config?.Current.TraceFoamOn == false ? $"paused: {WakesConfig.Key(nameof(WakesConfig.PathTracingFoam))} is off in the config"
        : "on";
    public string Describe() => region.Describe();

    // The images in a texture of the region's size, nearest and clamped like the sprites': for sprite s and age a,
    // the frame the raster shows at the middle of that step, alpha times 1 - life.
    static LoadedTexture Bake(ICoreClientAPI capi, FlipbookTexture sprites, int width, int height)
    {
        int frameSize = sprites.FrameSize, stride = sprites.Strips * frameSize;
        var pixels = new int[width * height];
        for (int s = 0; s < sprites.Strips; s++)
        {
            for (int a = 0; a < Ages; a++)
            {
                float life = (a + Fraction) / SplashCloudPool.Lifetime;
                int frame = Math.Clamp((int)(life * sprites.Frames), 0, sprites.Frames - 1);
                for (int y = 0; y < frameSize; y++)
                {
                    for (int x = 0; x < frameSize; x++)
                    {
                        int p = sprites.Pixels[(frame * frameSize + y) * stride + s * frameSize + x];
                        int alpha = (int)MathF.Round((p >>> 24) * (1 - life));
                        pixels[(s * frameSize + y) * width + a * frameSize + x] = (p & 0xFFFFFF) | alpha << 24;
                    }
                }
            }
        }
        int id = GL.GenTexture();
        var texture = new LoadedTexture(capi, id, width, height);
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    // Out of Skip or with the path paused by the config: the region and the mesh stand, and the next frame under Skip
    // writes them both.
    public void Stand()
    {
        stale = true;
        imagesStale = true;
    }

    public void OnReloadTextures() => imagesStale = true;

    // Walked again at the next frame under Skip, paused or not.
    public void Refresh() => stale = true;

    // From TraceFoam under Skip with the path on, once a Before frame of the driver; stepped when the frame made steps
    // of the clouds; hulls refreshed for the frame, null when the foam has none.
    public void Step(bool stepped, TraceHulls? hulls)
    {
        if (guard.Tripped || mesh == null || images == null) return;
        try
        {
            if (imagesStale)
            {
                CopyImages(images);
                imagesStale = false;
            }
            if (!stepped && !stale && session.Clouds?.Spawned == walkedSpawned) return;
            Walk(mesh, hulls);
            stale = false;
            writtenFrame = session.Driver?.Frames ?? -1;
        }
        catch (Exception e)
        {
            guard.Trip("step", e);
        }
    }

    // The scissor flag is not read back (glIsEnabled waits for a threaded driver): it is on while the GUI's scissor stack
    // holds a rectangle, and it would clip the copy.
    void CopyImages(LoadedTexture source)
    {
        IRenderAPI render = capi.Render;
        bool scissor = render.ScissorStack.Count > 0, copied = false;
        if (scissor) render.GlScissorFlag(false);
        try
        {
            region.Copy(source, region.Width, region.Height);
            copied = true;
            copies++;
        }
        finally
        {
            if (!copied) render.CurrentFrameBuffer = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
            if (scissor) render.GlScissorFlag(true);
        }
    }

    void Walk(TraceCloudMesh into, TraceHulls? hulls)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        try
        {
            if (session.Clouds is not { Off: false } pool)
            {
                lastClouds = 0;
                into.Clear();
                return;
            }
            walkedSpawned = pool.Spawned;
            IClientPlayer player = capi.World.Player;
            Vec3d camera = player.Entity.CameraPos;
            bool hideLocal = session.Config?.Current.FirstPersonEffects != true && player.CameraMode == EnumCameraMode.FirstPerson;
            double reach = Math.Sqrt(capi.Render.DefaultFrustumCuller.ViewDistanceSq);
            int n = pool.Collect(clouds, camera, Fraction, hideLocal, reach);
            lastClouds = n;
            if (n == 0)
            {
                into.Clear();
                return;
            }
            // Under the water the raster's murk test leaves the cloud whole (underwatereffects.fsh), at the threshold
            // the overlay takes too.
            bool cut = capi.Render.ShaderUniforms.CameraUnderwater <= 0.7f;
            float[] view = capi.Render.CameraMatrixOriginf;
            // The eye from the view matrix: CameraPos less the view's turned translation, which a third person camera has.
            double eyeY = camera.Y - (view[4] * view[12] + view[5] * view[13] + view[6] * view[14]);
            TraceHulls? decks = hulls is { Off: false, Count: > 0 } ? hulls : null;
            into.Write(clouds, n, region, size, Ages, camera, view, cut, decks, eyeY);
        }
        finally
        {
            long ticks = Stopwatch.GetTimestamp() - start;
            walks++;
            walkTicks += ticks;
            walkMaxTicks = Math.Max(walkMaxTicks, ticks);
            walkMaxWindow = Math.Max(walkMaxWindow, ticks);
            walkBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    // From the renderer, once a frame of the OIT stage. A run of vspt's frames that starts anew finds in the mesh what
    // the last run left, unless the walk of this very frame on Before 1.1 wrote it; otherwise nothing is drawn until a
    // walk has.
    public bool Frame([NotNullWhen(true)] out MeshRef? meshRef, [NotNullWhen(true)] out IShaderProgram? prog)
    {
        meshRef = null;
        prog = null;
        bool silent = vspt.Silent;
        if (silent && !wasSilent && writtenFrame != (session.Driver?.Frames ?? -2)) stale = true;
        wasSilent = silent;
        if (!silent) return false;
        silentFrames++;
        if (stale || imagesStale || mesh is not { QuadCount: > 0, Ref: { } r } || session.Programs?.Trace is not { } p
            || session.Config?.Current.TraceFoamOn == false || session.Driver is not { Off: false } || session.Clouds is not { Off: false })
        {
            return false;
        }
        meshRef = r;
        prog = p;
        return true;
    }

    public void NoteDraw() => draws++;

    public float[] ModelMatrix()
    {
        Vec3d camera = capi.World.Player.Entity.CameraPos;
        model[12] = (float)((mesh?.AnchorX ?? 0) - camera.X);
        model[13] = (float)((mesh?.AnchorY ?? 0) - camera.Y);
        model[14] = (float)((mesh?.AnchorZ ?? 0) - camera.Z);
        return model;
    }

    static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture);

    // The clouds' part of the trace line of .wakes stats, over the window ResetWindow opened.
    public string StatsLine()
    {
        var sb = new StringBuilder($"clouds {State}");
        if (mesh == null) return sb.ToString();
        long n = walks - walksAt, writes = mesh.Writes - writesAt;
        sb.Append(CultureInfo.InvariantCulture,
            $"; frames under Silent {silentFrames - silentFramesAt}, drawn {draws - drawsAt}; walks {n}, cpu ms per walk (collect, quads, mesh) mean/max " +
            $"{Ms(n > 0 ? (walkTicks - walkTicksAt) / n : 0)}/{Ms(walkMaxWindow)}, bytes per walk {(n > 0 ? (walkBytes - walkBytesAt) / n : 0)}; " +
            $"clouds {lastClouds}, quads {mesh.QuadCount} of {mesh.Capacity}; mesh writes {writes}, " +
            $"UpdateMesh ms mean/max {Ms(writes > 0 ? (mesh.WriteTicks - writeTicksAt) / writes : 0)}/{Ms(mesh.WindowMaxTicks)}, " +
            $"KB per write {(writes > 0 ? (mesh.WriteBytes - writeBytesAt) / writes / 1024.0 : 0):0.0}; images copied {copies - copiesAt}");
        return sb.ToString();
    }

    public void ResetWindow()
    {
        walksAt = walks;
        walkTicksAt = walkTicks;
        walkBytesAt = walkBytes;
        copiesAt = copies;
        silentFramesAt = silentFrames;
        drawsAt = draws;
        walkMaxWindow = 0;
        if (mesh == null) return;
        writesAt = mesh.Writes;
        writeTicksAt = mesh.WriteTicks;
        writeBytesAt = mesh.WriteBytes;
        mesh.ResetWindowMax();
    }

#if DEBUG
    // The clouds' part of '.wakes vspt'.
    public string Status()
    {
        var sb = new StringBuilder($"traced clouds {State}; {region.Describe()}");
        if (mesh != null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nclouds mesh {mesh.QuadCount} of {mesh.Capacity} quads for {lastClouds} clouds, anchor {mesh.AnchorX} {mesh.AnchorY} {mesh.AnchorZ}, " +
                $"stale {(stale ? "yes" : "no")}, images stale {(imagesStale ? "yes" : "no")}; since the world was entered: walks {walks}, " +
                $"cpu ms mean/max {Ms(walks > 0 ? walkTicks / walks : 0)}/{Ms(walkMaxTicks)}, mesh writes {mesh.Writes}, " +
                $"UpdateMesh ms mean/max {Ms(mesh.Writes > 0 ? mesh.WriteTicks / mesh.Writes : 0)}/{Ms(mesh.WriteMaxTicks)}, " +
                $"KB per write {(mesh.Writes > 0 ? mesh.WriteBytes / mesh.Writes / 1024.0 : 0):0.0}, images copied {copies}; " +
                $"frames under Silent {silentFrames}, drawn {draws}");
            string traced = renderer != null ? vspt.Traces(renderer) switch { true => "yes", false => "no", null => "unknown" } : "-";
            sb.Append($"; Recorder traced the clouds' renderer {traced}");
        }
        return sb.ToString();
    }
#endif

    // The renderer first, the region last; a throw on the way leaves nothing else behind. TraceFoam takes the patch off
    // before this.
    public void Dispose()
    {
        try
        {
            renderer?.Dispose();
            renderer = null;
        }
        finally
        {
            try
            {
                mesh?.Dispose();
                mesh = null;
                images?.Dispose();
                images = null;
            }
            finally
            {
                region.Dispose();
            }
        }
    }
}
