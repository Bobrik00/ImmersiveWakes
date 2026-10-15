using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ImmersiveWakes.Simulation;

// The colour buckets of Wakes 0.7.1 (wakeColors, wakeColorIntervals, blendStrength of WakesConfig). A texel whose
// averaged wave height h gives s = 1/(1+exp(-0.1 h)) takes the first bucket with s below Thresholds[i], the last one
// otherwise. A palette is immutable: the config builds a new one and the handler repaints its nodes with it
// (WakeHandler.Palette). Wakes holds the values of Wakes, which a new handler, the S1 tiles and the bench use
// (FINDINGS "Цвет (WakeColor)"); DefaultColors are the mod's default, the colours of the config file.
sealed class WakePalette
{
    public const int ColorCount = 9, ThresholdCount = 8;

    // Weight of a bucket's own colour against the water tint: (a/255)^(BlendStrength*10).
    public const float DefaultBlendStrength = 0.5f;

    // How much of the grey of the water's texture goes into the tint the buckets blend with, 0..1. Wakes blends with
    // the bare tint (0), and the see-through buckets come out about 1.86 times as bright as fresh water under the
    // same light; at 1 they take the brightness of the water itself (FINDINGS "Цвет воды и декали", С14). Not a
    // config key (fork 15): .wakes look shade tries values until the world is left.
    public const float DefaultWaterShade = 0f;

    // ARGB as Wakes keeps them; buckets 0 and 4 are transparent: deep troughs and flat water.
    public static ReadOnlySpan<uint> WakesColors => new uint[]
    {
        0x00000000, // transparent
        0x289399a6, // dark grey
        0x649ea5b0, // grey
        0xb4c4cad1, // light grey
        0x00000000, // transparent
        0xb4c4cad1, // light grey
        0xffffffff, // white, the crest
        0xb4c4cad1, // light grey
        0x649ea5b0, // grey
    };

    // The mod's default, the author's pick of 2026-10-11 (T07.03, DESIGN C14): Wakes' alphas with blue-grey colours
    // and a pale cyan crest. Buckets 0 and 4 stay transparent; bucket 0 keeps the colour the author left in it.
    public static ReadOnlySpan<uint> DefaultColors => new uint[]
    {
        0x00161414, // transparent
        0x28687aa4, // dark blue-grey
        0x64728cb7, // blue-grey
        0xb4a4b8cf, // light blue-grey
        0x00000000, // transparent
        0xb482aed1, // light blue
        0xffc9fafc, // pale cyan, the crest
        0xb481a6d0, // light blue
        0x648397b1, // blue-grey
    };

    // In units of the sigmoid; in units of h they are -29.44, -17.35, -13.86, -6.19, +0.80, +4.05, +8.47, +21.97.
    public static ReadOnlySpan<float> DefaultThresholds => new[] { 0.05f, 0.15f, 0.2f, 0.35f, 0.52f, 0.6f, 0.7f, 0.9f };

    // Immutable, so one instance serves every world of the process.
    public static readonly WakePalette Wakes = new(WakesColors, DefaultThresholds, DefaultBlendStrength);

    readonly uint[] colors;
    readonly float[] thresholds;

    // s < thr is h < -10 ln(1/thr - 1), and Wakes compares the double sigmoid with the float threshold widened to
    // double, so the bound is taken in double from (double)thr and rounded up to the first float at or above it:
    // h < bound in float is then (double)h < -10 ln(1/(double)thr - 1) exactly. A float compare, or bounds from the
    // double literals 0.05..0.9, part from Java on single values of h (FINDINGS "Баги и причуды оригинала"). A texel
    // of the 32 atlas pays no exp this way.
    readonly float[] heightBounds;
    readonly Vector128<float> lowBounds, highBounds;
    readonly Vector<float>[] boundLanes;

    // Depend on each colour's alpha and BlendStrength only, so they are worked out once, as Wakes' pow would give them.
    readonly double[] weights;

    public WakePalette(ReadOnlySpan<uint> colors, ReadOnlySpan<float> thresholds, float blendStrength, float waterShade = DefaultWaterShade)
    {
        if (!(waterShade >= 0 && waterShade <= 1)) throw new ArgumentOutOfRangeException(nameof(waterShade), waterShade, "the water shade lies in 0..1");
        if (colors.Length != ColorCount) throw new ArgumentException($"a palette holds {ColorCount} colours, not {colors.Length}", nameof(colors));
        if (!AreValidThresholds(thresholds))
        {
            throw new ArgumentException($"bucket thresholds must be {ThresholdCount}, none falling, inside (0, 1): {string.Join(", ", thresholds.ToArray())}",
                nameof(thresholds));
        }
        this.colors = colors.ToArray();
        this.thresholds = thresholds.ToArray();
        BlendStrength = blendStrength;
        WaterShade = waterShade;
        heightBounds = BuildHeightBounds(thresholds);
        lowBounds = Vector128.Create(heightBounds.AsSpan(0, 4));
        highBounds = Vector128.Create(heightBounds.AsSpan(4, 4));
        boundLanes = Array.ConvertAll(heightBounds, b => new Vector<float>(b));
        weights = new double[ColorCount];
        for (int i = 0; i < weights.Length; i++) weights[i] = Math.Pow((colors[i] >> 24) / 255f, blendStrength * 10);
    }

    public ReadOnlySpan<uint> Colors => colors;
    public ReadOnlySpan<float> Thresholds => thresholds;
    public float BlendStrength { get; }
    public float WaterShade { get; }

    public WakePalette WithWaterShade(float waterShade) => new(colors, thresholds, BlendStrength, waterShade);

    public bool SameAs(WakePalette other) =>
        other == this || BlendStrength.Equals(other.BlendStrength) && WaterShade.Equals(other.WaterShade)
            && Colors.SequenceEqual(other.Colors) && Thresholds.SequenceEqual(other.Thresholds);

    // WakeColor.resolveBucket, the first bucket whose bound h is below. The bounds rise, so that is the count of bounds
    // h is not below: two compares of four lanes and a bit count, where a branch per bound mispredicts on a field of
    // mixed heights. NaN is below no bound and lands in the last bucket, +-infinity at the ends, as in Wakes.
    public int ResolveBucket(float h)
    {
        var v = Vector128.Create(h);
        uint below = Vector128.LessThan(v, lowBounds).ExtractMostSignificantBits()
            | Vector128.LessThan(v, highBounds).ExtractMostSignificantBits() << 4;
        return ThresholdCount - BitOperations.PopCount(below);
    }

    // into[i] = colors[ResolveBucket(heights[i])], a vector of heights at a time: every lane starts at the last
    // bucket and steps one down for each bound it is below. On AVX2 the lanes take their colours by a permute of the
    // first eight and a select of the ninth; a lane by lane lookup cost about as much as the stretch of the heights.
    public void Paint(ReadOnlySpan<float> heights, ReadOnlySpan<int> colors, Span<int> into)
    {
        // The AVX2 lanes load all nine colours up front; the other paths would fail only on a height in the last bucket.
        if (colors.Length < ColorCount) throw new ArgumentException($"a palette paints with {ColorCount} colours, not {colors.Length}", nameof(colors));
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<float>[] bounds = boundLanes;
            var last = new Vector<int>(bounds.Length);
            if (Avx2.IsSupported && Vector<int>.Count == Vector256<int>.Count)
            {
                Vector256<int> low = Vector256.Create(colors[..8]), ninth = Vector256.Create(colors[8]), eight = Vector256.Create(8);
                for (; i <= heights.Length - Vector<float>.Count; i += Vector<float>.Count)
                {
                    var h = new Vector<float>(heights[i..]);
                    Vector<int> bucket = last;
                    foreach (Vector<float> bound in bounds) bucket += Vector.LessThan(h, bound);
                    Vector256<int> b = bucket.AsVector256();
                    Vector256.ConditionalSelect(Vector256.Equals(b, eight), ninth, Avx2.PermuteVar8x32(low, b)).CopyTo(into[i..]);
                }
            }
            else
            {
                Span<int> lanes = stackalloc int[Vector<int>.Count];
                for (; i <= heights.Length - Vector<float>.Count; i += Vector<float>.Count)
                {
                    var h = new Vector<float>(heights[i..]);
                    Vector<int> bucket = last;
                    foreach (Vector<float> bound in bounds) bucket += Vector.LessThan(h, bound);
                    bucket.CopyTo(lanes);
                    for (int k = 0; k < lanes.Length; k++) into[i + k] = colors[lanes[k]];
                }
            }
        }
        for (; i < heights.Length; i++) into[i] = colors[ResolveBucket(heights[i])];
    }

    // WakeColor.buildBucketColors without allocations: each bucket colour blended with the water tint (ARGB) and its
    // alpha scaled by the node's opacity, with the widenings of the Java code (a/255f, pow and blend in double,
    // truncation by the casts). The water's alpha byte is the grey of its texture (Game.WaterTint), which WaterShade
    // above 0 darkens the tint by; at 0 the tint is taken as Wakes takes it.
    public void BlendBuckets(int water, float opacity, Span<int> into)
    {
        int wr = (water >> 16) & 0xFF, wg = (water >> 8) & 0xFF, wb = water & 0xFF;
        if (WaterShade > 0)
        {
            float shade = 1 - WaterShade * (1 - ((uint)water >> 24) / 255f);
            wr = (int)(wr * shade);
            wg = (int)(wg * shade);
            wb = (int)(wb * shade);
        }
        uint[] palette = colors;
        for (int i = 0; i < palette.Length; i++)
        {
            uint c = palette[i];
            int a = (int)(c >> 24);
            double w = weights[i];
            int r = (int)(((c >> 16) & 0xFF) * w + wr * (1 - w));
            int g = (int)(((c >> 8) & 0xFF) * w + wg * (1 - w));
            int b = (int)((c & 0xFF) * w + wb * (1 - w));
            into[i] = (int)(a * opacity) << 24 | r << 16 | g << 8 | b;
        }
    }

    // debugColors of Wakes (SimulationNode.getPixelColor): troughs red, crests green, opaque whatever the node's age.
    public static int DebugColor(float h)
    {
        int c = (int)(255 * (2 / (1 + Math.Exp(-0.1 * h)) - 1));
        return unchecked((int)0xFF000000) | Math.Max(-c, 0) << 16 | Math.Max(c, 0) << 8;
    }

    // Eight thresholds inside (0, 1), none below the one before, as the colour screen of Wakes keeps them: the count of
    // bounds h is not below is the first bucket only when they do not fall. A threshold of 1 gives a bound of
    // +infinity, while Wakes' sigmoid reaches 1.0 near h = 368 and leaves the bucket there. Inside (0, 1) every bound
    // is finite, and rising thresholds give rising bounds. The config checks its values by this rule (T05.01).
    public static bool AreValidThresholds(ReadOnlySpan<float> thresholds)
    {
        if (thresholds.Length != ThresholdCount) return false;
        for (int i = 0; i < thresholds.Length; i++)
        {
            if (!(thresholds[i] > 0 && thresholds[i] < 1) || i > 0 && thresholds[i] < thresholds[i - 1]) return false;
        }
        return true;
    }

    static float[] BuildHeightBounds(ReadOnlySpan<float> thresholds)
    {
        var bounds = new float[thresholds.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            double v = -10 * Math.Log(1 / (double)thresholds[i] - 1);
            float b = (float)v;
            bounds[i] = b < v ? MathF.BitIncrement(b) : b;
        }
        return bounds;
    }
}
