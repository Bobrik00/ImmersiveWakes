using System;
using System.Globalization;

namespace ImmersiveWakes.Render;

// The overlay's look that the shaders take as uniforms (wakessurface.vsh): the lift over the water, the fade with
// distance and the opacity far away. Fork 15 kept them out of the config; they were GLSL constants until T07.03 made
// them uniforms, so that .wakes look tries values on the fly while the author compares. The values hold until the
// world is left.
sealed class WakeLook
{
    // Wakes lifts its quads by 0.01; an overlay just under the wavy surface fails the murkiness test of
    // underwatereffects.fsh in patches.
    public const float DefaultLift = 0.01f;
    public const float MaxLift = 0.5f;
    // The fade in blocks from the camera, the author's pick of 2026-10-11 (T07.03; 7..13 at first, back to 48..96 the
    // same day): past 96 blocks no wake shows, a boat's or anything's, and the run of the mesh there is not drawn.
    public const float DefaultFadeFrom = 48f, DefaultFadeTo = 96f;
    // .wakes look fade auto: the fade starts at this fraction of the renderer's reach and ends at the reach, as before
    // T07.03.
    public const float AutoFadeFrom = 0.8f;
    // Opacity factor reached at 64 blocks and beyond, ramped from 16 (wakessurface.vsh); 1 leaves it as it is.
    public const float DefaultFarAlpha = 1f;
    // The ramp of the far alpha in blocks, WAKES_FAR_FROM and WAKES_FAR_TO of wakessurface.vsh, for the traced foam's
    // texels (TraceFoam).
    public const float FarFrom = 16f, FarTo = 64f;
    public const float MaxFarAlpha = 4f;

    public float Lift { get; set; } = DefaultLift;
    // Blocks, both set or both null (auto).
    public float? FadeFrom { get; private set; } = DefaultFadeFrom;
    public float? FadeTo { get; private set; } = DefaultFadeTo;
    public float FarAlpha { get; set; } = DefaultFarAlpha;

    public void SetFade(float? from, float? to)
    {
        if (from.HasValue != to.HasValue) throw new ArgumentException("the fade takes both ends or neither");
        if (from is { } f && to is { } t && !(f >= 0 && t > f)) throw new ArgumentOutOfRangeException(nameof(to), $"the fade ends past its start: {f}..{t}");
        FadeFrom = from;
        FadeTo = to;
    }

    // The ends the shader gets for the renderer's reach: the set ones never past the reach, where the wake stops
    // drawing anyway, and always one before the other, as smoothstep needs.
    public (float From, float To) Fade(float reach)
    {
        if (FadeFrom is not { } from || FadeTo is not { } to) return (AutoFadeFrom * reach, reach);
        return to <= reach ? (from, to) : (Math.Min(from, AutoFadeFrom * reach), reach);
    }

    public string Describe(float reach)
    {
        (float from, float to) = Fade(reach);
        string fade = FadeFrom == null ? $"auto {Format(from)}..{Format(to)}" : $"{Format(from)}..{Format(to)}";
        return $"lift {Format(Lift)}, fade {fade} blocks, far alpha {Format(FarAlpha)}";
    }

    static string Format(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
