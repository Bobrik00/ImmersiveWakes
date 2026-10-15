using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
// The namespace itself would make Func ambiguous: the API has a Func of its own.
using ILogger = Vintagestory.API.Common.ILogger;

namespace ImmersiveWakes.Render.Trace;

// The Harmony postfix of T11.01 on vspt's internal SceneModSystem.AddAtlas(ICoreClientAPI capi, SceneCapture capture,
// ITextureAtlasAPI? which). vspt builds the capture of the entity atlas there (AuxAtlasMaterials.Send, at the first and
// every full send of the atlas), and its MaterialBaker sets BlendedLayer, bit 0 of the material's Flags, on every entry
// whose SubId is in capture.BlendedLayerSubIds (MaterialBaker.cs:430 in 0.2.3). vspt fills that set for the block
// atlas only, so for the entity atlas it stays empty; the postfix adds the regions of the foam and of the splash clouds.
// A texel of a flagged material in pass 3, a Recorder draw from the OIT stage, is taken with probability alpha instead
// of turning into thin glass (vs-vspt-alpha-by-render-pass). The same method captures the block atlas with which null;
// the postfix leaves that capture alone.
//
// vspt ships often (0.2.0 to 0.2.3 in three days) and the method is internal, so the patch goes on only for a version
// in Tested, finds the type, the method and the property by name and checks their signatures. Another version gets
// one Notification: every Warning of the start the engine repeats in its "Captured N issues during startup", and a new
// vspt every day or two would put one there each time (the author's choice of 2026-10-14). A miss on a tested version
// is a fault and gets one Warning. Harmony calls a static postfix, so what it reads is static too; statics outlive the
// world (vs-mod-assembly-statics-live-whole-process), and Dispose takes the patch off and clears them.
sealed class BlendedLayerPatch : IDisposable
{
    const string HarmonyId = "immersivewakes.vspt-blended-layer";
    const string CaptureTypeName = "VsRtx.Capture.SceneCapture";
    const string TargetName = "AddAtlas";
    const string SetName = "BlendedLayerSubIds";
    static readonly string[] Tested = ["0.2.3"];
#if DEBUG
    // Read once at the start, so a version not checked yet can be tried and a failed patch provoked without a rebuild:
    // a comma separated list of versions in place of Tested, and a method name in place of AddAtlas.
    public const string TestedVariable = "IWK_VSPT_TESTED", TargetVariable = "IWK_VSPT_TARGET";
#endif

    static ITextureAtlasAPI? entityAtlas;
    static Func<object, HashSet<int>>? blendedSet;
    static ILogger? log;
    static int[] subIds = [];
    static long captures, flagged, faults;

    readonly Harmony harmony;
    readonly MethodInfo target;

    BlendedLayerPatch(Harmony harmony, MethodInfo target)
    {
        this.harmony = harmony;
        this.target = target;
    }

    // The regions the postfix flags, none when empty. Set whole: the postfix may read it on a thread of vspt's.
    public static int[] SubIds
    {
        get => Volatile.Read(ref subIds);
        set => Volatile.Write(ref subIds, value);
    }

    // Captures of the entity atlas the postfix saw, and those it added the regions to.
    static long Captures => Interlocked.Read(ref captures);
    static long Flagged => Interlocked.Read(ref flagged);

    // Null when the patch stays off, with the reason in why; the caller logs nothing more. vspt is installed and its
    // engine patched.
    // The versions this build trusts; a Debug build takes another list from TestedVariable.
    public static string[] TestedVersions()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(TestedVariable) is { Length: > 0 } list)
        {
            return list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
#endif
        return Tested;
    }

    public static BlendedLayerPatch? TryApply(ICoreClientAPI capi, ILogger logger, VsptState vspt, out string why)
    {
        string[] tested = TestedVersions();
        string name = TargetName;
#if DEBUG
        if (Environment.GetEnvironmentVariable(TargetVariable) is { Length: > 0 } n) name = n;
#endif
        if (vspt.Version is not { } version || !tested.Contains(version))
        {
            why = $"off, vspt {vspt.Version ?? "?"} is not among the versions this build was tested with ({string.Join(", ", tested)})";
            logger.Notification("{0}", $"VS Path Tracing {vspt.Version ?? "?"} is not among the versions ImmersiveWakes was tested with ({string.Join(", ", tested)}): the foam is not drawn in the traced frame, the raster foam is as before");
            return null;
        }
        try
        {
            if (vspt.SceneSystemType is not { } system) throw new MissingMemberException($"{nameof(VsptState.SceneSystemType)} not found");
            if (!vspt.FlagsFound) throw new MissingMemberException("RtxWorldDraws.Skip/Silent not found in the patched engine");
            Type captureType = system.Assembly.GetType(CaptureTypeName) ?? throw new MissingMemberException($"type {CaptureTypeName} not found");
            MethodInfo method = system.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(ICoreClientAPI), captureType, typeof(ITextureAtlasAPI)])
                ?? throw new MissingMemberException($"static {system.Name}.{name}(ICoreClientAPI, SceneCapture, ITextureAtlasAPI) not found");
            // Harmony hands the postfix the original's arguments by name.
            string[] names = method.GetParameters().Select(p => p.Name ?? "").ToArray();
            if (method.ReturnType != typeof(void) || names[1] != "capture" || names[2] != "which")
            {
                throw new MissingMemberException($"{name} is {method.ReturnType.Name} ({string.Join(", ", names)}), not void (capi, capture, which)");
            }
            PropertyInfo set = captureType.GetProperty(SetName, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException($"{CaptureTypeName}.{SetName} not found");
            if (set.PropertyType != typeof(HashSet<int>) || set.GetMethod == null) throw new MissingMemberException($"{SetName} is {set.PropertyType.Name}, not a readable HashSet<int>");

            ParameterExpression capture = Expression.Parameter(typeof(object));
            Clear();
            blendedSet = Expression.Lambda<Func<object, HashSet<int>>>(Expression.Property(Expression.Convert(capture, captureType), set), capture).Compile();
            entityAtlas = capi.EntityTextureAtlas;
            log = logger;

            var harmony = new Harmony(HarmonyId);
            // A session whose Dispose never ran leaves its postfix on for the process; it goes before the new one. An
            // Unpatch without one would still rebuild the method.
            if (Harmony.GetPatchInfo(method)?.Postfixes.Any(p => p.owner == HarmonyId) == true) harmony.Unpatch(method, HarmonyPatchType.Postfix, HarmonyId);
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(BlendedLayerPatch).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
            why = $"on {system.Name}.{name} of vspt {version}";
            return new BlendedLayerPatch(harmony, method);
        }
        catch (Exception e)
        {
            Clear();
            why = $"off, {e.GetType().Name}: {e.Message}";
            logger.Warning("{0}", $"The Harmony patch for VS Path Tracing {version} failed, the foam is not drawn in the traced frame and the raster foam is as before: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // Never throws: an exception here would break off vspt's send of the atlas halfway.
    static void Postfix(object capture, ITextureAtlasAPI? which)
    {
        try
        {
            if (which == null || !ReferenceEquals(which, entityAtlas) || blendedSet is not { } read) return;
            Interlocked.Increment(ref captures);
            int[] ids = SubIds;
            if (ids.Length == 0) return;
            HashSet<int> set = read(capture);
            foreach (int id in ids) set.Add(id);
            Interlocked.Increment(ref flagged);
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref faults) == 1) log?.Warning("{0}", $"The blended-layer postfix on VS Path Tracing failed, the regions stay unflagged in this capture: {e}");
        }
    }

    public static string Describe() => $"entity atlas captures {Captures}, regions flagged in {Flagged}{(faults > 0 ? $", postfix faults {faults}" : "")}";

    // The counters too: a later world in the same process must not read the last one's.
    static void Clear()
    {
        entityAtlas = null;
        blendedSet = null;
        subIds = [];
        Interlocked.Exchange(ref captures, 0);
        Interlocked.Exchange(ref flagged, 0);
        Interlocked.Exchange(ref faults, 0);
    }

    public void Dispose()
    {
        try
        {
            harmony.Unpatch(target, HarmonyPatchType.Postfix, HarmonyId);
        }
        catch (Exception e)
        {
            log?.Warning("{0}", $"Taking the blended-layer patch off VS Path Tracing failed: {e}");
        }
        Clear();
        log = null;
    }
}
