using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using ImmersiveWakes.Effects;
using ImmersiveWakes.Render;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Debug;

// GL state leaks of the overlay (condition 3 of S1) and of the splash clouds. No engine tool sees one: a blend left on
// or a depth write left open is legal GL state (memory vs-gl-debug-render-profiler-tools). Renderers of the probe
// stand on both sides of each overlay pass and after the clouds, and on ".wakes probe" each reads the state once, in
// the next frame. ".wakes probe ref" keeps that reading as the reference, taken with no tiles, no live wake and no
// clouds so the passes return before their first GL call; a later reading is compared with it point by point. A point before a pass shows what earlier passes left for the
// engine's draws in between, a point after it what the pass itself leaves.
//
// The reads stall a threaded driver, so the probe stays idle between readings and a timing window should not hold
// a probed frame.
sealed class GlStateProbe : IDisposable
{
    static readonly (EnumRenderStage Stage, double Order, string Name)[] Layout =
    [
        (EnumRenderStage.OIT, OverlayRenderer.OitOrder - 0.005, "before OIT colour"),
        (EnumRenderStage.OIT, OverlayRenderer.OitOrder + 0.005, "after OIT colour"),
        (EnumRenderStage.OIT, SplashCloudRenderer.Order + 0.005, "after splash clouds"),
    ];

    enum Mode { Idle, Reference, Compare }

    readonly ICoreClientAPI capi;
    readonly WakesSession session;
    readonly RenderGuard guard;
    readonly Point[] points;
    readonly string[] markers = Layout.Select(p => "immersivewakes probe " + Format(p.Order)).ToArray();
    readonly GlSnapshot[] reading = Layout.Select(_ => new GlSnapshot()).ToArray();
    readonly bool[] taken = new bool[Layout.Length];
    readonly List<string> sequence = new();
    GlSnapshot[]? reference;
    Mode mode;

    public GlStateProbe(ICoreClientAPI capi, ILogger log, WakesSession session)
    {
        this.capi = capi;
        this.session = session;
        guard = new RenderGuard(log, "GL state probe");
        points = Layout.Select((p, i) => new Point(this, i, capi, guard, p.Stage, p.Order, "probe")).ToArray();
    }

    // The differences found by the last comparison, for ".wakes stats"; null before the first one.
    public string? LastResult { get; private set; }
    // Frames left in which the probe points and the overlay passes put markers into the GL command stream, so a
    // RenderDoc capture shows where they ran among the engine's draws. KHR_debug is core in GL 4.3, the context the
    // engine asks for. Kept short: with ".debug gl true" the engine logs every marker.
    int markFrames;

    public void MarkFrames(int frames) => markFrames = frames;

    public void Register()
    {
        foreach (Point p in points) p.Register();
    }

    // The answer comes as a chat line once the frame has run.
    public string Arm(bool asReference)
    {
        if (guard.Tripped) return "probe: off after an error, see client-main.log";
        if (!asReference && reference == null) return "probe: no reference yet; take it with '.wakes tiles clear' and '.wakes probe ref' with no live wake and no clouds";
        mode = asReference ? Mode.Reference : Mode.Compare;
        Array.Clear(taken);
        sequence.Clear();
        return asReference ? "probe: reading the reference in the next frame" : "probe: reading the next frame";
    }

    // Called by the overlay's passes, so the order of the mod's renderers in the probed frame goes into the report.
    public void Note(string pass, double order, string state)
    {
        // A probe that failed in its armed frame stays armed; the list must not grow every frame after it.
        if (mode != Mode.Idle && !guard.Tripped) sequence.Add($"{pass} {Format(order)} {state}");
    }

    // A pass that pushed a group pops it after its draw.
    public bool PushGroup(string pass)
    {
        if (markFrames <= 0) return false;
        GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 0, -1, "immersivewakes " + pass);
        return true;
    }

    public void PopGroup(bool pushed)
    {
        if (pushed) GL.PopDebugGroup();
    }

    void Read(int index)
    {
        if (markFrames > 0)
        {
            GL.DebugMessageInsert(DebugSourceExternal.DebugSourceApplication, DebugType.DebugTypeMarker, 0,
                DebugSeverity.DebugSeverityNotification, -1, markers[index]);
            if (index == points.Length - 1) markFrames--;
        }
        if (mode == Mode.Idle) return;
        reading[index].Capture();
        taken[index] = true;
        sequence.Add("probe " + Format(Layout[index].Order));
        if (index < points.Length - 1) return;
        // The last point closes the frame. All points stand in the OIT stage, so without transparentRenderPass none
        // runs and the probe stays armed without a word; a point skipped inside a stage that ran shows as untaken.
        // The report waits for the frame to end: it reads the engine's renderer lists.
        Mode done = mode;
        mode = Mode.Idle;
        GlSnapshot[] copy = reading.Select(r => r.Clone()).ToArray();
        bool[] took = (bool[])taken.Clone();
        string order = string.Join(", ", sequence);
        capi.Event.EnqueueMainThreadTask(() => Report(done, copy, took, order), "immersivewakes-probe");
    }

    void Report(Mode done, GlSnapshot[] read, bool[] took, string order)
    {
        var sb = new StringBuilder();
        string missing = string.Join(", ", Enumerable.Range(0, Layout.Length).Where(i => !took[i]).Select(i => Format(Layout[i].Order)));
        int tiles = session.Tiles?.Count ?? 0, wakes = session.Wakes?.QuadCount ?? 0;
        if (done == Mode.Reference)
        {
            if (missing.Length > 0)
            {
                Say($"probe: no reference, points {missing} did not run in the frame");
                return;
            }
            reference = read;
            sb.Append($"probe ref: taken at {Layout.Length} points with {tiles} tiles and {wakes} wake quads");
            if (order.Contains("drew")) sb.Append(" (a pass drew; a reference wants '.wakes tiles clear', no live wake and no clouds)");
            sb.Append("\nprobe order: ").Append(order);
            sb.Append("\nprobe engine OIT: ").Append(EngineOrder(EnumRenderStage.OIT));
            Say(sb.ToString());
            return;
        }

        var found = new List<string>();
        var perPoint = new StringBuilder();
        int count = 0;
        for (int i = 0; i < Layout.Length; i++)
        {
            if (!took[i]) continue;
            found.Clear();
            read[i].Diff(reference![i], found);
            if (found.Count == 0) continue;
            count += found.Count;
            perPoint.Append(perPoint.Length > 0 ? "; " : "").Append(Format(Layout[i].Order)).Append(' ').Append(Layout[i].Name)
                .Append(": ").Append(string.Join(", ", found));
        }
        LastResult = count == 0 ? "0 differences" : $"{count} differences: {perPoint}";
        if (missing.Length > 0) LastResult += $" (points {missing} did not run)";
        sb.Append($"probe: {tiles} tiles, {wakes} wake quads; {LastResult}\nprobe order: {order}");
        Say(sb.ToString());
    }

    void Say(string text) => session.Commands?.Say(text);

    // The engine calls the renderers of a stage in the order of its list (ClientEventManager.TriggerRenderStage): the
    // list itself shows the overlay's colour after the water's 0.37. Read by reflection, the fields are VintagestoryLib's,
    // which the mod does not reference.
    string EngineOrder(EnumRenderStage stage)
    {
        try
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object world = capi.World;
            object? events = world.GetType().GetField("eventManager", any)?.GetValue(world);
            if (events?.GetType().GetField("renderersByStage", any)?.GetValue(events) is not Array stages) return "not readable";
            if (stages.GetValue((int)stage) is not IEnumerable list) return "not readable";
            var names = new List<string>();
            foreach (object handler in list)
            {
                if (handler.GetType().GetField("Renderer", any)?.GetValue(handler) is not IRenderer r) continue;
                string name = r is SafeRenderer own
                    ? "immersivewakes " + own.PassName
                    : (handler.GetType().GetField("ProfilingName", any)?.GetValue(handler) as string ?? "")
                        .Replace(stage + "-", "");
                if (name.Length == 0) name = r.GetType().Name;
                names.Add($"{name} {Format(r.RenderOrder)}");
            }
            return string.Join(", ", names);
        }
        catch (Exception e)
        {
            return "not readable: " + e.Message;
        }
    }

    static string Format(double order) => order.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        foreach (Point p in points) p.Dispose();
    }

    sealed class Point : SafeRenderer
    {
        readonly GlStateProbe probe;
        readonly int index;

        public Point(GlStateProbe probe, int index, ICoreClientAPI capi, RenderGuard guard, EnumRenderStage stage, double order,
            string name) : base(capi, guard, stage, order, name)
        {
            this.probe = probe;
            this.index = index;
        }

        protected override void Render(float deltaTime) => probe.Read(index);
    }
}

// One reading of the state a draw of the overlay could leave behind: the switches a pass could leave changed (blend,
// depth, colour masks, culling), what Use() and the draw bind (program, texture units, vertex array) and what the OIT stage
// sets per frame buffer (draw buffers, blend per buffer). Kept as a flat row of ints with a table of field kinds, so
// a comparison is one loop.
sealed class GlSnapshot
{
    const int Buffers = 8;
    const int Units = 16;

    enum Kind { Id, Bool, Enum, Mask, Unit }

    const int PerBuffer = 9;
    const int FirstBuffer = 11;
    const int FirstUnit = FirstBuffer + Buffers * PerBuffer;
    static readonly (string Name, Kind Kind)[] Fields = BuildFields();

    // Indexed BLEND_* queries are GL 4.0; OpenTK 4.9.4 has no GetIndexedPName members for them (audit S1K-12).
    const GetIndexedPName BlendSrcRgb = (GetIndexedPName)0x80C9;
    const GetIndexedPName BlendDstRgb = (GetIndexedPName)0x80C8;
    const GetIndexedPName BlendSrcAlpha = (GetIndexedPName)0x80CB;
    const GetIndexedPName BlendDstAlpha = (GetIndexedPName)0x80CA;
    const GetIndexedPName BlendEquationRgb = (GetIndexedPName)0x8009;
    const GetIndexedPName BlendEquationAlpha = (GetIndexedPName)0x883D;

    readonly int[] values = new int[Fields.Length];
    readonly bool[] mask = new bool[4];
    int buffers;

    static (string, Kind)[] BuildFields()
    {
        var f = new List<(string, Kind)>
        {
            ("program", Kind.Id), ("active unit", Kind.Unit), ("draw framebuffer", Kind.Id), ("read framebuffer", Kind.Id),
            ("vertex array", Kind.Id), ("depth test", Kind.Bool), ("depth write", Kind.Bool), ("depth func", Kind.Enum),
            ("cull", Kind.Bool), ("cull face", Kind.Enum), ("front face", Kind.Enum),
        };
        for (int b = 0; b < Buffers; b++)
        {
            f.Add(("blend", Kind.Bool));
            f.Add(("blend src rgb", Kind.Enum));
            f.Add(("blend dst rgb", Kind.Enum));
            f.Add(("blend src alpha", Kind.Enum));
            f.Add(("blend dst alpha", Kind.Enum));
            f.Add(("blend eq rgb", Kind.Enum));
            f.Add(("blend eq alpha", Kind.Enum));
            f.Add(("colour mask", Kind.Mask));
            f.Add(("draw buffer", Kind.Enum));
        }
        for (int u = 0; u < Units; u++) f.Add(("texture 2D", Kind.Id));
        return f.ToArray();
    }

    public GlSnapshot Clone()
    {
        var c = new GlSnapshot { buffers = buffers };
        values.CopyTo(c.values, 0);
        return c;
    }

    // Main thread, inside a render stage. Leaves the active unit as it found it.
    public void Capture()
    {
        if (buffers == 0) buffers = Math.Min(Buffers, GL.GetInteger(GetPName.MaxDrawBuffers));
        Array.Clear(values);
        int i = 0;
        values[i++] = GL.GetInteger(GetPName.CurrentProgram);
        values[i++] = GL.GetInteger(GetPName.ActiveTexture);
        values[i++] = GL.GetInteger(GetPName.DrawFramebufferBinding);
        values[i++] = GL.GetInteger(GetPName.ReadFramebufferBinding);
        values[i++] = GL.GetInteger(GetPName.VertexArrayBinding);
        values[i++] = GL.IsEnabled(EnableCap.DepthTest) ? 1 : 0;
        values[i++] = GL.GetBoolean(GetPName.DepthWritemask) ? 1 : 0;
        values[i++] = GL.GetInteger(GetPName.DepthFunc);
        values[i++] = GL.IsEnabled(EnableCap.CullFace) ? 1 : 0;
        values[i++] = GL.GetInteger(GetPName.CullFaceMode);
        values[i] = GL.GetInteger(GetPName.FrontFace);
        for (int b = 0; b < buffers; b++)
        {
            int at = FirstBuffer + b * PerBuffer;
            values[at] = GL.IsEnabled(IndexedEnableCap.Blend, b) ? 1 : 0;
            GL.GetInteger(BlendSrcRgb, b, out values[at + 1]);
            GL.GetInteger(BlendDstRgb, b, out values[at + 2]);
            GL.GetInteger(BlendSrcAlpha, b, out values[at + 3]);
            GL.GetInteger(BlendDstAlpha, b, out values[at + 4]);
            GL.GetInteger(BlendEquationRgb, b, out values[at + 5]);
            GL.GetInteger(BlendEquationAlpha, b, out values[at + 6]);
            GL.GetBoolean(GetIndexedPName.ColorWritemask, b, mask);
            values[at + 7] = (mask[0] ? 8 : 0) | (mask[1] ? 4 : 0) | (mask[2] ? 2 : 0) | (mask[3] ? 1 : 0);
            values[at + 8] = GL.GetInteger(GetPName.DrawBuffer0 + b);
        }
        int active = values[1];
        for (int u = 0; u < Units; u++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + u);
            values[FirstUnit + u] = GL.GetInteger(GetPName.TextureBinding2D);
        }
        GL.ActiveTexture((TextureUnit)active);
    }

    // Adds "field from old to new" for every difference from the reference; a per-buffer field that changed the same
    // way in several buffers is one entry with the buffer list. No '>' in the text: the chat escapes it.
    public void Diff(GlSnapshot reference, List<string> into)
    {
        for (int i = 0; i < FirstBuffer; i++)
        {
            if (values[i] != reference.values[i]) into.Add($"{Fields[i].Name} from {Show(i, reference.values[i])} to {Show(i, values[i])}");
        }
        for (int k = 0; k < PerBuffer; k++)
        {
            var groups = new List<(int Old, int New, List<int> Buffers)>();
            for (int b = 0; b < Math.Max(buffers, reference.buffers); b++)
            {
                int i = FirstBuffer + b * PerBuffer + k;
                if (values[i] == reference.values[i]) continue;
                int g = groups.FindIndex(x => x.Old == reference.values[i] && x.New == values[i]);
                if (g < 0) groups.Add((reference.values[i], values[i], new List<int> { b }));
                else groups[g].Buffers.Add(b);
            }
            int field = FirstBuffer + k;
            foreach ((int o, int n, List<int> bs) in groups)
            {
                into.Add($"{Fields[field].Name} [{string.Join(" ", bs)}] from {Show(field, o)} to {Show(field, n)}");
            }
        }
        for (int u = 0; u < Units; u++)
        {
            int i = FirstUnit + u;
            if (values[i] != reference.values[i]) into.Add($"unit {u} texture from {reference.values[i]} to {values[i]}");
        }
    }

    static string Show(int field, int v) => Fields[field].Kind switch
    {
        Kind.Bool => v != 0 ? "on" : "off",
        Kind.Mask => $"{((v & 8) != 0 ? 'r' : '-')}{((v & 4) != 0 ? 'g' : '-')}{((v & 2) != 0 ? 'b' : '-')}{((v & 1) != 0 ? 'a' : '-')}",
        Kind.Unit => (v - (int)TextureUnit.Texture0).ToString(CultureInfo.InvariantCulture),
        Kind.Enum => EnumName(v),
        _ => v.ToString(CultureInfo.InvariantCulture),
    };

    // The constants the passes and the engine put into these fields; anything else prints in hex.
    static string EnumName(int v) => v switch
    {
        0 => "ZERO/NONE",
        1 => "ONE",
        0x0300 => "SRC_COLOR",
        0x0301 => "ONE_MINUS_SRC_COLOR",
        0x0302 => "SRC_ALPHA",
        0x0303 => "ONE_MINUS_SRC_ALPHA",
        0x0304 => "DST_ALPHA",
        0x0305 => "ONE_MINUS_DST_ALPHA",
        0x0306 => "DST_COLOR",
        0x0307 => "ONE_MINUS_DST_COLOR",
        0x8006 => "FUNC_ADD",
        0x800A => "FUNC_SUBTRACT",
        0x8007 => "MIN",
        0x8008 => "MAX",
        0x0201 => "LESS",
        0x0202 => "EQUAL",
        0x0203 => "LEQUAL",
        0x0207 => "ALWAYS",
        0x0404 => "FRONT",
        0x0405 => "BACK",
        0x0408 => "FRONT_AND_BACK",
        0x0900 => "CW",
        0x0901 => "CCW",
        >= 0x8CE0 and <= 0x8CEF => "COLOR_ATTACHMENT" + (v - 0x8CE0).ToString(CultureInfo.InvariantCulture),
        _ => "0x" + v.ToString("X", CultureInfo.InvariantCulture),
    };
}
