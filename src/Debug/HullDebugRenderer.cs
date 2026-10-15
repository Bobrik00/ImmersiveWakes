using ImmersiveWakes.Config;
using ImmersiveWakes.Render;
using ImmersiveWakes.Sources;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Debug;

// ".wakes debug hull": the hull of every boat in the player's dimension as a box of lines, turned with the boat, a line
// along its bow edge, a post at each emission point and a white post at each point its bow clouds come from going bow
// first, to hold against the frames of ".debug wireframe entity". One cube of lines is uploaded once and drawn with a
// matrix per box: RenderLine would make and drop a mesh on every call (FINDINGS "Harmony, хоткеи, команды, отладка").
// The boxes go through the engine's wireframe program in Opaque, as the multibox behaviour draws its frames, so both
// sit in one depth and light. The engine's boxes keep the world axes (BehaviorPassivePhysicsMultiBox.adjustBoxesToYaw)
// and match these only on a course along an axis, and there the bow box of the sailed boat, 2.8 by 3.0, still stands
// 0.1 off on an east or west course.
sealed class HullDebugRenderer : SafeRenderer
{
    const double Order = 0.51;
    const float LineWidth = 2f, EngineLineWidth = 1.6f, Thin = 0.02f, PostAbove = 1f;
    static readonly Vec4f HullColor = new(0, 1, 1, 1), BowColor = new(1, 1, 0, 1), EmissionColor = new(0, 1, 0, 1), CloudColor = new(1, 1, 1, 1);

    readonly WakesSession session;
    readonly RenderGuard guard;
    readonly Matrixf matrix = new();
    MeshRef? cube;

    public HullDebugRenderer(ICoreClientAPI capi, ILogger log, WakesSession session)
        : this(capi, new RenderGuard(log, "hull frame"), session)
    {
    }

    HullDebugRenderer(ICoreClientAPI capi, RenderGuard guard, WakesSession session)
        : base(capi, guard, EnumRenderStage.Opaque, Order, "hull")
    {
        this.guard = guard;
        this.session = session;
    }

    public bool On { get; private set; }

    // After an exception the frames are not drawn for the rest of the world.
    public bool Off => guard.Tripped;

    // Main thread: the first call uploads the cube.
    public void Show(bool on)
    {
        if (on && cube == null)
        {
            MeshData data = LineMeshUtil.GetCube(ColorUtil.WhiteArgb);
            // The wireframe program pulls a vertex with z offset 1 toward the camera, as the engine's frames do.
            data.Flags = new int[data.VerticesCount];
            for (int i = 0; i < data.Flags.Length; i++) data.Flags[i] = 1 << 8;
            cube = Capi.Render.UploadMesh(data);
        }
        On = on;
    }

    protected override void Render(float deltaTime)
    {
        if (!On || cube == null || session.Driver is not { } driver || session.Config is not { } config) return;
        if (Capi.World.Player?.Entity is not { } me) return;
        WakesConfig current = config.Current;
        IShaderProgram prog = Capi.Shader.GetProgram((int)EnumShaderProgram.Wireframe);
        prog.Use();
        try
        {
            // On by default in Opaque, but WireframeCube turns it off after every box on macOS, the engine's own
            // frames of .debug wireframe entity among them.
            Capi.Render.GLEnableDepthTest();
            Capi.Render.LineWidth = LineWidth;
            Capi.Render.GLDepthMask(false);
            prog.Uniform("origin", 0f, 0f, 0f);
            prog.UniformMatrix("projectionMatrix", Capi.Render.CurrentProjectionMatrix);
            Vec3d camera = me.CameraPos;
            foreach (SourceState s in driver.Sources.States)
            {
                if (s.Hull is not { } h || s.Entity.Pos.Dimension != me.Pos.Dimension) continue;
                EntityPos p = s.Entity.Pos;
                // The boats of the report, so a harbour of them costs no more than it lists.
                if (p.DistanceTo(me.Pos) > SourceTracker.DebugRadius) continue;
                double x = p.X - camera.X, y = p.InternalY - camera.Y, z = p.Z - camera.Z;
                float turn = p.Yaw + GameMath.PI;
                float midX = (h.MinX + h.MaxX) / 2, midY = (h.MinY + h.MaxY) / 2, midZ = (h.MinZ + h.MaxZ) / 2;
                float halfX = (h.MaxX - h.MinX) / 2, halfY = (h.MaxY - h.MinY) / 2;
                Draw(prog, x, y, z, turn, midX, midY, midZ, halfX, halfY, h.Length / 2, HullColor);
                Draw(prog, x, y, z, turn, midX, h.MaxY, h.MinZ, halfX, Thin, Thin, BowColor);
                // The bow lies toward local -z, so an emission point ahead of Pos is at local z = -offset * length.
                BoatEmissionRule rule = SourceTracker.EmissionOf(s, current);
                for (int i = 0, n = rule.Points(h.Length); i < n; i++)
                {
                    float emissionZ = -rule.OffsetAt(i, n) * h.Length;
                    Draw(prog, x, y, z, turn, 0, midY + PostAbove / 2, emissionZ, Thin, halfY + PostAbove / 2, Thin, EmissionColor);
                }
                // A cloud point `along` ahead of the bow edge and `across` off the axis lies at local x = across,
                // z = MinZ - along.
                for (int i = 0, n = rule.CloudPointCount; i < n; i++)
                {
                    (float along, float across) = rule.CloudPoint(i);
                    Draw(prog, x, y, z, turn, across, midY + PostAbove / 2, h.MinZ - along, Thin, halfY + PostAbove / 2, Thin, CloudColor);
                }
            }
        }
        finally
        {
            prog.Stop();
            Capi.Render.LineWidth = EngineLineWidth;
            Capi.Render.GLDepthMask(true);
        }
    }

    // The cube spans -1..1: scaled to the half sizes, moved to the box's middle in the boat's axes, turned as the engine
    // turns the boxes and put at Pos.
    void Draw(IShaderProgram prog, double x, double y, double z, float turn, float midX, float midY, float midZ,
        float halfX, float halfY, float halfZ, Vec4f color)
    {
        matrix.Set(Capi.Render.CameraMatrixOrigin).Translate(x, y, z).RotateY(turn)
            .Translate(midX, midY, midZ).Scale(halfX, halfY, halfZ);
        prog.UniformMatrix("modelViewMatrix", matrix.Values);
        prog.Uniform("colorIn", color);
        Capi.Render.RenderMesh(cube!);
    }

    public override void Dispose()
    {
        base.Dispose();
        cube?.Dispose();
        cube = null;
    }
}
