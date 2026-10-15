using System;
using ImmersiveWakes.Config;
using ImmersiveWakes.Debug;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render;

// The wake's pass, OIT 0.38: right after the water on 0.37, through the weighted OIT buffers (DESIGN "Итоги S1"). The
// depth masks under the crest that S1 and T08.03 tried against the screen space reflections of shader packs are gone
// (fork 12): one on OIT 0.36 before the water wrote nothing at the default opacity, one at the end of OIT looked
// worse to the author than no mask.
//
// The pass draws two layers with the same program: the wake's quads over the core's atlas, when one of its chunks is
// in view (WakeMesh.InView), and the S1 tiles over their own texture. The renderer gates by itself, as the engine
// ignores RenderRange (vs-renderrange-not-honored): the frustum and the view distance, measured horizontally as chunks
// are culled.
//
// The pass touches no blend at all: the OIT stage sets it per buffer, and a single GlToggleBlend there breaks every
// later OIT draw of the frame (vs-oit-overlay-over-water). Left as the draw made them: the active texture unit and the
// textures Use() and wakeTex bound, all of which the engine sets anew before its own draws. A pass that draws runs
// inside a GPU timer query and tells the GL state probe it ran.
sealed class OverlayRenderer : IDisposable
{
    public const double OitOrder = 0.38;

    readonly ICoreClientAPI capi;
    readonly WakesSession session;
    readonly RenderGuard guard;
    readonly Pass pass;
    // What the pass in progress draws, from Prepare.
    bool wakesInView, tilesInView;
    double reach;

    public OverlayRenderer(ICoreClientAPI capi, ILogger log, WakesSession session)
    {
        this.capi = capi;
        this.session = session;
        guard = new RenderGuard(log, "wake overlay");
        pass = new Pass(session, guard, EnumRenderStage.OIT, OitOrder, "OIT colour", DrawOit);
    }

    public bool Shadows { get; set; } = true;
    public WakeLook Look { get; } = new();
    public bool Off => guard.Tripped;
    // Frames in which the pass issued its draw; a count that stands still means a gate or a missing program held it.
    public long OitDraws { get; private set; }
    // The wake's quads and chunks in view at the last colour pass that ran, 0 when it drew nothing.
    public int LastQuads { get; private set; }
    public int LastChunks { get; private set; }

    public void Register() => pass.Register();

    // Under vspt's Silent every call would go to its Recorder, which drops a draw with a texture of its own
    // (vs-vspt-foreign-renderers-silent): the pass leaves before Use() and the uniforms.
    bool DrawOit()
    {
        LastQuads = LastChunks = 0;
        if (session.Vspt?.Silent == true) return false;
        if (session.Programs is not { Oit: { } prog } programs || !Prepare(out var inView)) return false;
        bool timed = BeginTimer();
        try
        {
            Draw(programs, prog);
            OitDraws++;
            (LastQuads, LastChunks) = inView;
        }
        finally
        {
            EndTimer(timed);
        }
        return true;
    }

    // The query spans the whole pass, the program's switch included.
    bool BeginTimer() => session.Stats?.Gpu.Begin(GpuChannel.Oit) == true;

    void EndTimer(bool timed)
    {
        if (timed) session.Stats?.Gpu.End();
    }

    // The wake's runs in view and the S1 tiles in view. A wake the driver no longer steps or uploads would stand still
    // on the water, so it is not drawn. False when there is nothing to draw: the pass then makes no GL call at all.
    bool Prepare(out (int Quads, int Chunks) inView)
    {
        inView = default;
        FrustumCulling culler = capi.Render.DefaultFrustumCuller;
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        reach = Math.Sqrt(culler.ViewDistanceSq);
        // Past the end of the fade the wake is transparent, so the runs out there are left out of the draw.
        double drawn = Look.Fade((float)reach).To;
        if (session.Wakes is { QuadCount: > 0, Ref: not null } wakes && session.Atlas is { TextureId: not 0 }
            && session.Driver is { Off: false, UploadOff: false })
        {
            inView = wakes.InView(culler, cam, drawn);
        }
        wakesInView = inView.Quads > 0;
        tilesInView = session.Tiles?.InView(culler, cam, drawn) == true;
        return wakesInView || tilesInView;
    }

    // Use() throws while another program is active (vs-custom-mesh-in-engine-stage): one the engine left bound is
    // stopped for the draw and bound again after it.
    void Draw(OverlayPrograms programs, IShaderProgram prog)
    {
        IShaderProgram? prev = capi.Render.CurrentActiveShader;
        prev?.Stop();
        try
        {
            try
            {
                // Inside the try: Use() makes the program the active one before it sets the include uniforms, and a
                // throw there would leave it active for the engine's next Use(), which throws outside any try.
                prog.Use();
                programs.SetUniforms(prog, session.Config?.Current.WakeOpacity ?? WakesConfig.DefaultWakeOpacity, (float)reach, Look);
                // Use() has just set the engine's drop shadow intensity; at zero getBrightnessFromShadowMap gives 1
                // (fogandlight.fsh). CoriaenderShaders' hook in Use() has just turned its cloud shadow on.
                if (!Shadows)
                {
                    prog.Uniform("shadowIntensity", 0f);
                    prog.Uniform("cloudshadowsEnabled", 0);
                }
                if (wakesInView && session.Wakes is { Ref: { } wakeRef } wakes && session.Atlas is { } atlas)
                {
                    programs.SetLayer(prog, wakes.Origin, atlas.TextureId);
                    capi.Render.RenderMesh(wakeRef);
                }
                if (tilesInView && session.Tiles is { Mesh: { Ref: { } tileRef } tileMesh, Texture: { } tex })
                {
                    programs.SetLayer(prog, tileMesh.Origin, tex.TextureId);
                    capi.Render.RenderMesh(tileRef);
                }
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

    public void Dispose()
    {
        pass.Dispose();
    }

    // One stage and order of the overlay; draw returns whether it issued its draw.
    sealed class Pass : ProbedRenderer
    {
        readonly Func<bool> draw;

        public Pass(WakesSession session, RenderGuard guard, EnumRenderStage stage, double order, string name, Func<bool> draw)
            : base(session, guard, stage, order, name)
        {
            this.draw = draw;
        }

        protected override bool Draw() => draw();
    }
}
