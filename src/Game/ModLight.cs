using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Game;

// The light of the foam and the splash clouds under a mod that changes the water's light (T08.04).
//
// Dark Vision lifts the block light of the dark by a uniform minUnlitBright in applyLightWithoutPointLight of
// fogandlight.vsh. Its patch goes into every vertex shader that holds the function, the mod's two among them, but its
// hook on ShaderProgramBase.Use writes the value only to programs on its list of pass names; the mod's programs kept 0,
// and the wake vanished at night on water that Dark Vision lifted. The value comes from its DarkVisionAPI and is
// written after Use().
//
// SheyderMod's Blocklight also lights the chunk meshes with light of its own and patches GetLightRGBSVec4f, which
// GetLightRGBsAsInt bypasses. In T08.04 its patch changed no light over the water and the foam at a torch looked the
// same either way, so the wake keeps reading GetLightRGBsAsInt.
static class ModLight
{
    public const string DarkVisionModId = "darkvision";
    public const string DarkVisionUniform = "minUnlitBright";

    // '.wakes modlight off' leaves the uniform at 0, as before T08.04.
    public static bool Enabled = true;
    static Func<float>? darkVision;

    public static float? DarkVisionValue => darkVision?.Invoke();

    public static void Start(ICoreClientAPI capi, ILogger log)
    {
        Enabled = true;
        darkVision = null;
        if (!capi.ModLoader.IsModEnabled(DarkVisionModId)) return;
        // A soft dependency: Dark Vision's types are reached by name, and the mod loads without it.
        ModSystem? api = capi.ModLoader.GetModSystem("DarkVision.DarkVisionAPI");
        MethodInfo? method = api?.GetType().GetMethod("GetSmoothedMinUnlitBright", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (method?.ReturnType == typeof(float)) darkVision = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), api, method);
        else log.Warning("{0}", "Dark Vision is loaded, but DarkVisionAPI.GetSmoothedMinUnlitBright was not found: in the dark the foam stays darker than the water Dark Vision lifts");
    }

    public static void Stop()
    {
        darkVision = null;
        Enabled = true;
    }

    // After prog.Use(), which runs Dark Vision's hook. Off writes 0, what the mod's programs held before.
    public static void SetUniforms(IShaderProgram prog)
    {
        if (darkVision == null || !prog.HasUniform(DarkVisionUniform)) return;
        prog.Uniform(DarkVisionUniform, Enabled ? darkVision() : 0f);
    }
}
