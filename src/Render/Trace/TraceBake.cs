using System;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;

namespace ImmersiveWakes.Render.Trace;

// The pass that makes the texels the tracer takes: rows of the wake texture times a factor per slot into a texture of
// the region's size, which RenderTextureIntoAtlas then copies into the region. The tracer reads the texel and nothing
// else (EntityNative takes no vertex colour), so the factors the raster adds in its shader go into the texel here:
// wakeOpacity, the fade with distance and the far alpha of WakeLook, per node from the camera of the step
// (wakessurface.vsh does it per vertex). The palette's alpha, the node's opacity and the soft edge are in the texel
// already. In pass 3 with the blended-layer flag the tracer takes a texel with probability alpha, so the product is the
// share of rays the foam stops (vs-vspt-alpha-by-render-pass). A texel under a hull's water mask gets alpha 0
// (TraceHulls, T11.05).
//
// The program is wakestracebake (OverlayPrograms): texelFetch of the wake texture, which holds slots 1:1 with the
// region, of the slots, one texel of RGBA32F per slot (the factor, the bits of the hulls over the node, the node's x
// and z from the hulls' origin), and of the hulls' masks. Its quad covers the viewport of the rows drawn. In the Before
// stage, where nothing is silenced, the primary framebuffer is bound; the copy that follows binds it again, so the bake
// leaves it to the copy and rebinds it only when it throws. Blend is off for the bake; the copy turns it back on.
sealed class TraceBake : IDisposable
{
    // Floats a slot: factor, hull bits, x and z.
    public const int SlotFloats = 4;

    readonly ICoreClientAPI capi;
    LoadedTexture? target;
    FrameBufferRef? framebuffer;
    int factorTexture;
    MeshRef? quad;

    public TraceBake(ICoreClientAPI capi, int width, int height)
    {
        this.capi = capi;
        try
        {
            int id = GL.GenTexture();
            target = new LoadedTexture(capi, id, width, height);
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
            Nearest();
            factorTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, factorTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, TraceRegion.Columns, TraceRegion.Columns, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            Nearest();
            GL.BindTexture(TextureTarget.Texture2D, 0);
            // Leaves no framebuffer bound; the bake right after binds its own, the copy the primary one.
            framebuffer = capi.Render.CreateFrameBuffer(target);
            quad = capi.Render.UploadMesh(Quad());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public LoadedTexture Target => target!;

    static void Nearest()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
    }

    // The viewport in clip space, counter-clockwise, so face culling leaves it alone. No uv: under the patched engine
    // UploadMesh hands vspt's Recorder a copy only of a mesh with uv.
    static MeshData Quad()
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false);
        float[] corners = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0];
        Array.Copy(corners, mesh.xyz, corners.Length);
        mesh.Indices = [0, 1, 2, 0, 2, 3];
        mesh.VerticesCount = 4;
        mesh.IndicesCount = 6;
        return mesh;
    }

    // Rows [0, rows * Texels) of the wake texture into the target, alpha times the slot's factor and 0 under a hull;
    // slots holds 64 slots of SlotFloats a slot row, the first rows of them are uploaded.
    public void Run(IShaderProgram prog, int wakeTexture, float[] slots, int rows, TraceHulls hulls)
    {
        if (target == null || framebuffer == null || quad == null) return;
        IRenderAPI render = capi.Render;
        GL.BindTexture(TextureTarget.Texture2D, factorTexture);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, TraceRegion.Columns, rows, PixelFormat.Rgba, PixelType.Float, slots);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        render.CurrentFrameBuffer = framebuffer;
        GL.Viewport(0, 0, target.Width, rows * TraceRegion.Texels);
        render.GlToggleBlend(false);
        // Use() throws while another program is active (vs-custom-mesh-in-engine-stage).
        IShaderProgram? prev = render.CurrentActiveShader;
        prev?.Stop();
        try
        {
            try
            {
                prog.Use();
                prog.BindTexture2D("wakeTex", wakeTexture, 0);
                prog.BindTexture2D("factorTex", factorTexture, 1);
                prog.BindTexture2D("hullTex", hulls.MaskTexture, 2);
                prog.Uniforms4("hullPose", TraceHulls.Max, hulls.Pose);
                prog.Uniforms4("hullFrame", TraceHulls.Max, hulls.Frame);
                prog.Uniforms4("hullCell", TraceHulls.Max, hulls.Cell);
                render.RenderMesh(quad);
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

#if DEBUG
    // The target's rows [0, rows * Texels) as BGRA bytes in ints, alpha in the top byte; binds the bake's framebuffer,
    // which the copy that follows binds over.
    public int[] Read(int rows, int[]? into)
    {
        int count = target!.Width * rows * TraceRegion.Texels;
        if (into == null || into.Length < count) into = new int[count];
        capi.Render.CurrentFrameBuffer = framebuffer;
        GL.ReadPixels(0, 0, target.Width, rows * TraceRegion.Texels, PixelFormat.Bgra, PixelType.UnsignedByte, into);
        return into;
    }
#endif

    // After a throw between the bake and the end of the copy: the primary framebuffer with its viewport, and blend on.
    public void Restore()
    {
        IRenderAPI render = capi.Render;
        // The bake left unit 2 active; the copy that did not come would have bound unit 0.
        GL.ActiveTexture(TextureUnit.Texture0);
        render.CurrentFrameBuffer = render.FrameBuffers[(int)EnumFrameBuffer.Primary];
        render.GlToggleBlend(true, EnumBlendMode.Standard);
    }

    public void Dispose()
    {
        if (framebuffer != null) capi.Render.DestroyFrameBuffer(framebuffer);
        framebuffer = null;
        target?.Dispose();
        target = null;
        if (factorTexture != 0) GL.DeleteTexture(factorTexture);
        factorTexture = 0;
        quad?.Dispose();
        quad = null;
    }
}
