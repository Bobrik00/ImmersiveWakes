using System;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Client;
// The namespace itself would make Func ambiguous: the API has a Func of its own.
using EnumAppSide = Vintagestory.API.Common.EnumAppSide;
using ModSystem = Vintagestory.API.Common.ModSystem;

namespace ImmersiveWakes.Render.Trace;

// What the mod knows of VS Path Tracing (vspt), read only (reference/vspt-audit/wf2-A-recorder-foam.md, "Включение").
// The mod is enabled and SceneModSystem loads on the client, which it does only on an engine patched for this version
// of vspt; ShouldLoad is asked itself, since the system's object exists in StartClientSide either way. The static flags
// of RtxWorldDraws that the patcher sews into VintagestoryLib: Skip is raised from Before 0 on while vspt shows its
// frame (and under its loading cover), Silent only inside OnRenderFrame of a renderer whose draws go to vspt's Recorder
// instead of GL. The hook version is not compared: it was 4 in 0.2.2 and 6 in 0.2.3. The mod acts on the flags only
// with a version it was tested with (BlendedLayerPatch.TestedVersions): a later vspt may raise Skip where the raster
// frame shows, and with an unknown version the mod runs as 0.9.1, drawing its raster under Silent for nothing.
sealed class VsptState
{
    const string ModId = "vspt";
    const string SceneSystem = "VsRtx.Scene.SceneModSystem";
    const string WorldDraws = "VsRtx.Engine.RtxWorldDraws";
    const string RecorderType = "VsRtx.Scene.Recorder";

    readonly Func<bool>? skip, silent;
#if DEBUG
    readonly Func<object, bool>? traces;
#endif

    VsptState(bool patched, string? version, Type? sceneSystem, Func<bool>? skip, Func<bool>? silent, Func<object, bool>? traces, string? fault)
    {
        Patched = patched;
        Version = version;
        SceneSystemType = sceneSystem;
        Trusted = patched && version != null && Array.IndexOf(BlendedLayerPatch.TestedVersions(), version) >= 0;
        this.skip = skip;
        this.silent = silent;
#if DEBUG
        this.traces = traces;
#endif
        Fault = fault;
    }

    public bool Patched { get; }
    public string? Version { get; }
    // VsRtx.Scene.SceneModSystem, the way into vspt's assembly; null when it does not load.
    public Type? SceneSystemType { get; }
    // The first reflection that threw, for the status line.
    public string? Fault { get; }
    public bool FlagsFound => skip != null && silent != null;
    // A version the mod was tested with, on an engine patched for it.
    public bool Trusted { get; }

    // False without the patched engine, with a version the mod was not tested with, or after the field went away.
    public bool Skip => Trusted && skip?.Invoke() == true;
    public bool Silent => Trusted && silent?.Invoke() == true;

#if DEBUG
    // Whether vspt's Recorder has handed a pose of this renderer's type to the tracer; null when it cannot be asked.
    public bool? Traces(object renderer)
    {
        if (traces == null) return null;
        try
        {
            return traces(renderer);
        }
        catch (Exception)
        {
            return null;
        }
    }
#endif

    // Null when vspt is not enabled in this world.
    public static VsptState? Detect(ICoreClientAPI capi)
    {
        string? fault = null;
        bool patched = false;
        string? version = null;
        ModSystem? scene = null;
        try
        {
            if (!capi.ModLoader.IsModEnabled(ModId)) return null;
            version = capi.ModLoader.GetMod(ModId)?.Info?.Version;
            scene = capi.ModLoader.GetModSystem(SceneSystem);
            patched = scene?.ShouldLoad(EnumAppSide.Client) == true;
        }
        catch (Exception e)
        {
            fault = $"detect: {e.GetType().Name}: {e.Message}";
        }

        Func<bool>? skip = null, silent = null;
        try
        {
            Type? draws = capi.World.GetType().Assembly.GetType(WorldDraws);
            skip = StaticBool(draws, "Skip");
            silent = StaticBool(draws, "Silent");
        }
        catch (Exception e)
        {
            fault ??= $"{WorldDraws}: {e.GetType().Name}: {e.Message}";
        }

        Func<object, bool>? traces = null;
#if DEBUG
        try
        {
            MethodInfo? method = scene?.GetType().Assembly.GetType(RecorderType)
                ?.GetMethod("Traces", BindingFlags.Public | BindingFlags.Static, [typeof(object)]);
            if (method?.ReturnType == typeof(bool)) traces = method.CreateDelegate<Func<object, bool>>();
        }
        catch (Exception e)
        {
            fault ??= $"{RecorderType}.Traces: {e.GetType().Name}: {e.Message}";
        }
#endif
        return new VsptState(patched, version, patched ? scene?.GetType() : null, skip, silent, traces, fault);
    }

    // A compiled read: FieldInfo.GetValue would box a bool on every frame.
    static Func<bool>? StaticBool(Type? type, string name)
    {
        FieldInfo? field = type?.GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (field == null || field.FieldType != typeof(bool)) return null;
        return Expression.Lambda<Func<bool>>(Expression.Field(null, field)).Compile();
    }

    public string Describe()
    {
        string flags = FlagsFound ? "RtxWorldDraws.Skip and Silent found" : "RtxWorldDraws.Skip/Silent not found";
        return $"vspt {Version ?? "?"}, engine {(Patched ? "patched for it" : "not patched for it")}, {flags}, {(Trusted ? "a tested version" : "not a tested version, the mod ignores the flags")}" +
               (Fault != null ? $", fault {Fault}" : "");
    }
}
