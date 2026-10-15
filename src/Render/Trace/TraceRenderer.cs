using System.Diagnostics.CodeAnalysis;
using Vintagestory.API.Client;

namespace ImmersiveWakes.Render.Trace;

// What a TraceRenderer draws: the foam (TraceFoam) or the splash clouds (TraceClouds).
interface ITracedDraw
{
    // Once a frame of the OIT stage: whether to draw, with what.
    bool Frame([NotNullWhen(true)] out MeshRef? mesh, [NotNullWhen(true)] out IShaderProgram? prog);
    // The translation in [12..14] (column-major), as the Recorder and vspt's EntityLink read it: the mesh's anchor
    // relative to the player's CameraPos.
    float[] ModelMatrix();
    int PageTextureId { get; }
    void NoteDraw();
}

// Draws quads for vspt's Recorder from the OIT stage, which the Recorder marks pass 3: with the blended-layer flag of
// BlendedLayerPatch the tracer takes a texel there with probability alpha (vs-vspt-alpha-by-render-pass). Only under
// Silent, where every call below goes to the Recorder instead of GL; the program wakestrace never runs in GL. The
// Recorder takes the draw with a page of a vanilla atlas in unit 0 and modelMatrix of 16 floats, whose translation it
// reads in [12..14] relative to the player's CameraPos (vs-vspt-foreign-renderers-silent).
//
// The patched Use() throws while another program is current, which under Silent the Recorder keeps without GL: one the
// engine left current is stopped for the draw and made current again after it, as in OverlayRenderer. The mesh is never
// culled: a record that is not repeated in a frame is dropped from the tracer and sent anew when it comes back, and the
// tracer needs the mesh off screen too, for reflections and shadows. One mesh a renderer and a frame: the Recorder keys a
// record by the renderer, the mesh, the texture and the draw's number among the renderer's draws of the frame
// (Recorder.Collect), so the foam and the clouds have a renderer each, and the clouds' record stays the same while the
// foam comes and goes. Generic so that each has a type of its own: the Recorder notes the types it traced
// (Recorder.Traces, read by .wakes vspt).
sealed class TraceRenderer<T> : SafeRenderer where T : ITracedDraw
{
    readonly T draw;

    public TraceRenderer(ICoreClientAPI capi, RenderGuard guard, T draw, double order, string pass)
        : base(capi, guard, EnumRenderStage.OIT, order, pass)
    {
        this.draw = draw;
    }

    protected override void Render(float deltaTime)
    {
        if (!draw.Frame(out MeshRef? mesh, out IShaderProgram? prog)) return;
        IShaderProgram? prev = Capi.Render.CurrentActiveShader;
        prev?.Stop();
        try
        {
            try
            {
                prog.Use();
                prog.UniformMatrix("modelMatrix", draw.ModelMatrix());
                prog.BindTexture2D("tex", draw.PageTextureId, 0);
                Capi.Render.RenderMesh(mesh);
                draw.NoteDraw();
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
}

// The render orders of the two TraceRenderers.
static class TraceRenderer
{
    // After the overlay on 0.38 and the splash clouds on 0.39; the order means nothing to the Recorder.
    public const double FoamOrder = 0.4, CloudOrder = 0.41;
}
