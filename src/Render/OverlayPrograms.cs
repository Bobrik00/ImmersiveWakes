using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ImmersiveWakes.Game;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render;

// The shader programs of the mod: wakesoverlay draws the wake in the OIT stage, wakescloud the splash clouds there.
// With VS Path Tracing (TraceFoam) also wakestrace, whose draws go only to vspt's Recorder, and wakestracebake, the
// pass that makes the traced foam's texels; both without OIT.
//
// ReloadShaders disposes every program, the mod's included, and only then raises ReloadShader, so every reload builds
// new objects here. The first build comes from the reload that follows StartClientSide on world entry: a Compile()
// before it would not find the wakes* includes yet. A failed compile throws nothing and leaves ProgramId 0
// (vs-shader-compile-fail-silent), so a program is handed out only after its Compile() returned true.
sealed class OverlayPrograms : IDisposable
{
    public const string OitName = "wakesoverlay";
    public const string CloudName = "wakescloud";
    public const string TraceName = "wakestrace";
    public const string TraceBakeName = "wakestracebake";

    // The mod's sampler is the first of every program (wakeTex, cloudTex), and the engine binds samplers to units equal
    // to their declaration index (vs-sampler-unit-equals-declaration-index).
    public const int FirstTextureUnit = 0;

    readonly ICoreClientAPI capi;
    readonly ILogger log;
    IShaderProgram? oit;
    IShaderProgram? cloud;
    IShaderProgram? trace, traceBake;

    public OverlayPrograms(ICoreClientAPI capi, ILogger log)
    {
        this.capi = capi;
        this.log = log;
        capi.Event.ReloadShader += Load;
    }

    // Null while the program is missing, failed to compile or was disposed by a reload.
    public IShaderProgram? Oit => Live(oit);
    public IShaderProgram? Cloud => Live(cloud);
    public IShaderProgram? Trace => Live(trace);
    public IShaderProgram? TraceBake => Live(traceBake);

    // Set by TraceFoam at the mod's start when vspt takes the foam, before the reload that builds the programs.
    public bool TraceWanted { get; set; }

    static IShaderProgram? Live(IShaderProgram? prog) => prog is { Disposed: false } ? prog : null;

    // The result reaches ".reload shaders", which then reports "errors occured".
    bool Load()
    {
        // The engine has already disposed the old programs; the fields must not keep them if a build below throws.
        oit = cloud = trace = traceBake = null;
        oit = Build(OitName, useOit: true);
        cloud = Build(CloudName, useOit: true);
        if (!TraceWanted) return oit != null && cloud != null;
        trace = Build(TraceName, useOit: false);
        traceBake = Build(TraceBakeName, useOit: false);
        return oit != null && cloud != null && trace != null && traceBake != null;
    }

    IShaderProgram? Build(string name, bool useOit)
    {
        IShaderProgram? prog = null;
        bool compiled = false;
        try
        {
            prog = capi.Shader.NewShaderProgram();
            // Before registration: a null domain makes the engine look for the files in "game".
            prog.AssetDomain = "immersivewakes";
            prog.Oit = useOit;
            // The registration writes the prefix into these objects; without them a missing file ends in a
            // NullReferenceException instead of LoadError.
            prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
            prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
            capi.Shader.RegisterFileShaderProgram(name, prog);
            // A missing file leaves an empty shader, and compiling it would only add a second error to the log.
            if (!prog.LoadError)
            {
                bool ok = prog.Compile();
                compiled = true;
                if (ok) return prog;
            }
            // The cause is already in the log: an Error line of the engine with the driver's message or the missing file.
            log.Warning("{0}", $"Shader program {name} did not build, nothing draws with it until the next shader reload");
        }
        catch (Exception e)
        {
            log.Warning("{0}", $"Shader program {name} failed to load, nothing draws with it until the next shader reload: {e}");
        }
        if (prog != null)
        {
            // Before Compile() the shader ids are 0, and Dispose would detach shader 0 from program 0: a GL error of the
            // mod's own on a path where the engine made no GL call. Without the shaders Dispose only deletes program 0,
            // which GL ignores, and the engine's own Dispose on the next reload does nothing.
            if (!compiled)
            {
                prog.VertexShader = null;
                prog.FragmentShader = null;
            }
            prog.Dispose();
        }
        return null;
    }

    // The wake's programs, after prog.Use(). opacity is wakeOpacity of the config, the alpha Wakes gives the wake's
    // vertices; reach is the renderer's own, past which the wake is not drawn.
    public void SetUniforms(IShaderProgram prog, float opacity, float reach, WakeLook look)
    {
        IRenderAPI render = capi.Render;
        prog.Uniform("wakeOpacity", opacity);
        (float fadeFrom, float fadeTo) = look.Fade(reach);
        prog.Uniform("wakesFadeFrom", fadeFrom);
        prog.Uniform("wakesFadeEnd", fadeTo);
        prog.Uniform("wakesLift", look.Lift);
        prog.Uniform("wakesFarAlpha", look.FarAlpha);
        // Wakes lowers its quads under the surface while the camera is under water. The engine's own switch for
        // "under water" is this value above 0.7, where getUnderwaterMurkiness stops testing the overlay against the
        // water's depth (underwatereffects.fsh), so a lowered quad is never thrown away as murk.
        prog.Uniform("wakesLiftSign", render.ShaderUniforms.CameraUnderwater > 0.7f ? -1f : 1f);
        SetScene(prog);
    }

    // The camera, and the fog and ambient light of the mod's programs. Call after prog.Use(). Use()
    // sets the uniforms of the engine includes: wave counters, shadows, fog spheres, point lights. The rest comes from
    // the sources ChunkRenderer.RenderOIT gives chunkliquid: capi.Render.FogColor and its neighbours return
    // AmbientManager.Blended*, and CameraMatrixOriginf is the float copy of the camera matrix the chunk pools render
    // with. Dark Vision's lift of the dark goes last (ModLight).
    public void SetScene(IShaderProgram prog)
    {
        IRenderAPI render = capi.Render;
        prog.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
        prog.UniformMatrix("modelViewMatrix", render.CameraMatrixOriginf);
        prog.Uniform("rgbaFogIn", render.FogColor);
        prog.Uniform("rgbaAmbientIn", render.AmbientColor);
        prog.Uniform("fogMinIn", render.FogMin);
        prog.Uniform("fogDensityIn", render.FogDensity);
        ModLight.SetUniforms(prog);
    }

    // One mesh and its texture. BindTexture2D, unlike Uniform(), does not check that the program is the active one and
    // would set the sampler of whatever program is. origin is the absolute position the mesh vertices are relative to.
    public void SetLayer(IShaderProgram prog, Vec3d meshOrigin, int textureId)
    {
        prog.BindTexture2D("wakeTex", textureId, FirstTextureUnit);
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        prog.Uniform("origin", (float)(meshOrigin.X - cam.X), (float)(meshOrigin.Y - cam.Y), (float)(meshOrigin.Z - cam.Z));
    }

    // What CoriaenderShaders' hook in Use() left in cloudshadowsEnabled (wakescloudshadow.fsh) at the program's last
    // Use(): 1 while the program takes Coriaender's cloud shadow, 0 without Coriaender; null for a missing program or
    // with the uniform compiled out, as with shadows off. '.wakes shadows off' writes 0 into the wake's program after
    // Coriaender's hook, and right after a shader reload both read 0 until the program draws. glGetUniform waits for
    // the driver, so only for a command.
    public static int? CloudShadowsSet(IShaderProgram? prog)
    {
        if (prog is not { Disposed: false }) return null;
        int location = GL.GetUniformLocation(prog.ProgramId, "cloudshadowsEnabled");
        if (location < 0) return null;
        GL.GetUniform(prog.ProgramId, location, out int value);
        return value;
    }

    // What the program's last Use() left in Dark Vision's minUnlitBright (ModLight); null for a missing program or
    // without Dark Vision, whose patch declares the uniform. glGetUniform waits for the driver, so only for a command.
    public static float? DarkVisionSet(IShaderProgram? prog)
    {
        if (prog is not { Disposed: false }) return null;
        int location = GL.GetUniformLocation(prog.ProgramId, ModLight.DarkVisionUniform);
        if (location < 0) return null;
        GL.GetUniform(prog.ProgramId, location, out float value);
        return value;
    }

    // The active samplers of a program with the units their uniforms hold, "!" after a unit that samplers of two types
    // share. A shader pack that adds a sampler to an engine include ahead of the mod's moves the declaration indices the
    // engine binds by (vs-sampler-unit-equals-declaration-index), while wakeTex and cloudTex stay on FirstTextureUnit;
    // samplers of two types on one unit fail the draw with GL_INVALID_OPERATION and nothing in the log (T08.03), two of
    // one type are legal. The units are what the program's last draw left: only BindTexture2D sets them, and before the
    // first draw every one reads 0, so drawn says whether the reading means anything. glGetUniform waits for the driver,
    // so only for a command.
    public static string Samplers(IShaderProgram? prog, bool drawn)
    {
        if (prog is not { Disposed: false }) return "-";
        if (!drawn) return "not drawn yet";
        int id = prog.ProgramId;
        GL.GetProgram(id, GetProgramParameterName.ActiveUniforms, out int count);
        var units = new List<(string Name, int Unit, ActiveUniformType Type)>();
        for (int i = 0; i < count; i++)
        {
            string name = GL.GetActiveUniform(id, i, out int size, out ActiveUniformType type);
            if (!type.ToString().Contains("Sampler", StringComparison.Ordinal)) continue;
            // An array comes as one entry named after its first element, "x[0]".
            string stem = size > 1 && name.EndsWith("[0]", StringComparison.Ordinal) ? name[..^3] : name;
            for (int k = 0; k < size; k++)
            {
                string element = size > 1 ? string.Create(CultureInfo.InvariantCulture, $"{stem}[{k}]") : name;
                GL.GetUniform(id, GL.GetUniformLocation(id, element), out int unit);
                units.Add((element, unit, type));
            }
        }
        return string.Join(" ", units.Select(u => string.Create(CultureInfo.InvariantCulture,
            $"{u.Name}@{u.Unit}{(units.Any(o => o.Unit == u.Unit && o.Type != u.Type) ? "!" : "")}")));
    }

    public void Dispose()
    {
        capi.Event.ReloadShader -= Load;
        oit?.Dispose();
        cloud?.Dispose();
        trace?.Dispose();
        traceBake?.Dispose();
        oit = cloud = trace = traceBake = null;
    }
}
