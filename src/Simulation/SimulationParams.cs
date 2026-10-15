using System;
using System.Numerics;

namespace ImmersiveWakes.Simulation;

// Parameters of the core. A set is immutable; the handler takes a new one between ticks as long as the resolution
// stays (WakeHandler.Params), since Wakes reads these from its config on every tick, and a new resolution builds a
// new core. Wakes reads wavePropagationFactor and waveDecayFactor as free text with no bounds, and outside the bounds
// below its step blows up or grows, so every source of a value (JSON config, ConfigLib, commands) is clamped here
// (FINDINGS "Шаг волнового уравнения").
sealed class SimulationParams
{
    // Ticks a node lives after its last touch. Static in Wakes, not in its config.
    public const int MaxAge = 30;

    // The longest life without a touch in the amplitude fade (CoreModes.AmplitudeFade).
    public const int AmplitudeMaxAge = 4 * MaxAge;

    // Without decay the synchronous scheme holds up to 1.25 (alpha 1). The halo of Wakes lags on two sides and makes
    // a different scheme that nobody analysed, hence the margin.
    public const float MaxPropagation = 1.2f;

    // At factor 0 the wave stands still, and with decay 1 every impulse then grows u by itself each step for as long
    // as the node lives. The bound itself is arbitrary: at 0.1 with decay 1 the slowest mode of a node turns once in
    // about 300 ticks, and the field still grows almost linearly through a node's life.
    public const float MinPropagation = 0.1f;

    public const float DefaultPropagation = 0.95f, DefaultDecay = 0.5f;

    // Flood fill bounds are the sliders of Wakes (WakesConfig floodFillDistance, floodFillTickDelay).
    public const int MinFloodDistance = 1, MaxFloodDistance = 6, DefaultFloodDistance = 2;
    public const int MinFloodDelay = 1, MaxFloodDelay = 20, DefaultFloodDelay = 2;

    public SimulationParams(int resolution = 16, float wavePropagationFactor = DefaultPropagation, float waveDecayFactor = DefaultDecay,
        int floodFillDistance = DefaultFloodDistance, int floodFillTickDelay = DefaultFloodDelay, bool cacheWaterTint = true)
    {
        if (resolution is not (8 or 16 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "the core runs at 8, 16 or 32 cells per block");
        }
        Resolution = resolution;
        Power = BitOperations.Log2((uint)resolution);
        WavePropagationFactor = Clamp(wavePropagationFactor, MinPropagation, MaxPropagation, DefaultPropagation);
        WaveDecayFactor = Clamp(waveDecayFactor, 0f, 1f, DefaultDecay);
        FloodFillDistance = Math.Clamp(floodFillDistance, MinFloodDistance, MaxFloodDistance);
        FloodFillTickDelay = Math.Clamp(floodFillTickDelay, MinFloodDelay, MaxFloodDelay);
        CacheWaterTint = cacheWaterTint;

        // SimulationNode.WakeSimulation.tick with its float steps: the factor product in float, pow and log in double,
        // then the casts. At the defaults the bits match Java 22 (FINDINGS "Шаг волнового уравнения"). One tick of
        // 1/20 s and a cell of 1/16 block are baked in at any resolution, so the wave runs at factor*16/res
        // blocks per second.
        float time = 20f;
        Alpha = (float)Math.Pow(WavePropagationFactor * 16f / time, 2);
        Beta = (float)(Math.Log(10 * WaveDecayFactor + 10) / Math.Log(20));
    }

    public int Resolution { get; }

    // log2(Resolution): a world pixel p lies in block p >> Power.
    public int Power { get; }

    public float WavePropagationFactor { get; }
    public float WaveDecayFactor { get; }
    public int FloodFillDistance { get; }
    public int FloodFillTickDelay { get; }

    // cacheNodeWaterColor of Wakes: a node takes its water tint once when it is made; off, it asks again on every
    // tick it lives.
    public bool CacheWaterTint { get; }

    public float Alpha { get; }
    public float Beta { get; }

    static float Clamp(float value, float min, float max, float fallback) => float.IsNaN(value) ? fallback : Math.Clamp(value, min, max);
}
