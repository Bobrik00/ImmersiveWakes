using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ImmersiveWakes.Simulation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace ImmersiveWakes.Config;

// The options of WakesConfig 0.7.1 under its keys, without the debug switches (debug overlays belong to the "full
// parity" level of DESIGN, debugColors is WakeHandler.DebugColors for the bench) and shaderWaterHeightOffset, an
// offset for Iris, and without the shape of the splash plane and paddleStrength: the plane and the raft's paddles are
// not ported (DESIGN "Сокращение уровня", 2026-10-10). Plus what the port adds: maxNodes (fork 20), boatEmission
// (fork 19), swimmerOffset and projectileSpawning (fork 21), wakeColorAlphas, the alpha of each colour apart from
// it (T06.08), and pathTracingFoam (T11.02).
// Render constants stay out of the file (fork 15), and so do the core modes (DESIGN "Переключатели режимов ядра").
// LoadModConfig is plain Newtonsoft: it keeps the initialiser of a key the file lacks, matches keys in any case, takes
// NaN and Infinity for a number and drops a key it does not know; Sanitize puts every value back in range.
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
sealed class WakesConfig
{
    // Strengths enter the impulse as (int)(strength * velocity) at up to about one block a tick, so 1000 already puts
    // h far past the bound of the last bucket near 22. Wakes takes any int; the bound catches a typo of extra digits.
    public const int MaxStrength = 1000;

    // More than twice the head's lead: further out the wake starts in open water ahead of the swimmer. The default is
    // the author's pick on the lake of S1 by .wakes swimmer (2026-10-10).
    public const float MaxSwimmerOffset = 2f, DefaultSwimmerOffset = 0.8f;

    // Wakes draws at 1; the author's pick on the lake of S1 together with the default palette (T07.03, 2026-10-11).
    public const float DefaultWakeOpacity = 0.5f;

    public bool DisableMod { get; set; }

    // The names of EffectSpawningRule. A boat that carries a remote player and not ours masks boatSpawning by
    // otherPlayersSpawning (WakesUtils.getEffectRuleFromSource); a ridden animal keeps mobSpawning. itemSpawning holds
    // the fishing bobber too, which Wakes leaves without a wake (fork 21).
    public string BoatSpawning { get; set; } = EffectSpawningRule.SimulationAndPlanes.Name;
    public string PlayerSpawning { get; set; } = EffectSpawningRule.OnlySimulation.Name;
    public string OtherPlayersSpawning { get; set; } = EffectSpawningRule.OnlySimulation.Name;
    public string MobSpawning { get; set; } = EffectSpawningRule.OnlySimulation.Name;
    public string ItemSpawning { get; set; } = EffectSpawningRule.OnlySimulation.Name;

    // The port's: arrows, spears, thrown stones, snowballs and thrown items, whatever implements IProjectile. Wakes
    // 0.7.1 gives them DISABLED (fork 21 adds their rings). They make rings alone, so only the wake flag counts.
    public string ProjectileSpawning { get; set; } = EffectSpawningRule.OnlySimulation.Name;

    public float WavePropagationFactor { get; set; } = SimulationParams.DefaultPropagation;
    public float WaveDecayFactor { get; set; } = SimulationParams.DefaultDecay;
    public int InitialStrength { get; set; } = 20;
    public int SplashStrength { get; set; } = 100;

    // Cells of the simulation per block: 8, 16 or 32. 32 is expensive: the step of the wave bench takes about 3.4
    // times the CPU of 16 (20.6 ms against 6 ms a tick at 4095 nodes, FINDINGS "Стоимость на CPU"); the colouring
    // of the atlas costs less at 32, the step of the port at 32 is not measured. The atlas keeps 32 texels a slot.
    public int WakeResolution { get; set; } = 16;

    // Live nodes at most; at the limit new nodes are refused and .wakes stats counts them (fork 20).
    public int MaxNodes { get; set; } = WakeHandler.DefaultMaxNodes;

    public int FloodFillDistance { get; set; } = SimulationParams.DefaultFloodDistance;
    public int FloodFillTickDelay { get; set; } = SimulationParams.DefaultFloodDelay;

    public float WakeOpacity { get; set; } = DefaultWakeOpacity;
    public float BlendStrength { get; set; } = WakePalette.DefaultBlendStrength;
    public bool FirstPersonEffects { get; set; }
    public bool SpawnParticles { get; set; } = true;
    // Off, every live node asks the water tint on every step, and the engine allocates 32 bytes a call and takes its
    // climate map lock (ApplyColorMapOnRgba); the tint of a block does not change in VS, so only the cost differs.
    public bool CacheNodeWaterColor { get; set; } = true;

    // Eight thresholds of the sigmoid and the nine colours of the buckets between them (WakePalette). The port's
    // buckets are eight compares in two vectors, so the count is fixed where Wakes' colour screen could add handles.
    // A colour is "#RRGGBB" and its alpha 0..255 sits in WakeColorAlphas: ConfigLib's colour picker has no alpha
    // (T06.08). A colour written "#AARRGGBB", as Wakes and the files before T06.08 keep it, is taken with its own
    // alpha, which replaces the number in WakeColorAlphas; the file is then written back in the split form.
    public float[] WakeColorIntervals { get; set; } = WakePalette.DefaultThresholds.ToArray();
    public string[] WakeColors { get; set; } = DefaultColors();
    public int[] WakeColorAlphas { get; set; } = DefaultAlphas();

    // Blocks per 50 ms step. Wakes' splash plane spawns the bow clouds above 0.3 of it, 3 b/s at the default; the plane
    // is not ported, and the clouds keep its key and the rule's planes flag (T06.03). Wakes' bound is -5..5; the speed
    // divides, so it starts above 0 here.
    public float MaxSplashPlaneVelocity { get; set; } = 0.5f;

    // Blocks ahead of Pos along the course where a swimming player's wake starts. Wakes starts it at Pos, under an
    // upright MC player; the swim animation of VS lays the body flat along the course around Pos, the head about 0.8
    // ahead (seraph LowerTorso turned 80 degrees about its origin 15/16 up), and a wake from Pos starts at the hips.
    public float SwimmerOffset { get; set; } = DefaultSwimmerOffset;

    // Where a boat's wake starts and how strong it is, by entity code; the first rule that matches wins, a boat no
    // rule matches starts at Pos with strength x1.
    public BoatEmissionRule[] BoatEmission { get; set; } = DefaultBoats();

    // The foam in the frame of VS Path Tracing (E11): "auto" draws it with vspt of a version the mod was tested with,
    // "off" patches nothing of vspt and takes no region of the entity atlas. Both come at the start of a world, so
    // "auto" after a start with "off" waits for the next world; "off" on a reload stops the drawing at once.
    public string PathTracingFoam { get; set; } = TraceFoamAuto;

    public const string TraceFoamAuto = "auto", TraceFoamOff = "off";

    // A key the file has and the config does not, typos among them; Sanitize names each and drops it.
    [JsonExtensionData] IDictionary<string, JToken>? unknown;

    // Sanitize parses the names and colours into these. Until then they hold the defaults, so a fresh config reads
    // the same either way.
    [JsonIgnore] public EffectSpawningRule BoatRule { get; private set; } = EffectSpawningRule.SimulationAndPlanes;
    [JsonIgnore] public EffectSpawningRule PlayerRule { get; private set; } = EffectSpawningRule.OnlySimulation;
    [JsonIgnore] public EffectSpawningRule OtherPlayersRule { get; private set; } = EffectSpawningRule.OnlySimulation;
    [JsonIgnore] public EffectSpawningRule MobRule { get; private set; } = EffectSpawningRule.OnlySimulation;
    [JsonIgnore] public EffectSpawningRule ItemRule { get; private set; } = EffectSpawningRule.OnlySimulation;
    [JsonIgnore] public EffectSpawningRule ProjectileRule { get; private set; } = EffectSpawningRule.OnlySimulation;
    [JsonIgnore] public uint[] Colors { get; private set; } = WakePalette.DefaultColors.ToArray();
    [JsonIgnore] public bool TraceFoamOn { get; private set; } = true;

    static readonly NamingStrategy Naming = new CamelCaseNamingStrategy();

    // The key of a member in the file.
    public static string Key(string member) => Naming.GetPropertyName(member, false);

    public BoatEmissionRule EmissionFor(AssetLocation code)
    {
        foreach (BoatEmissionRule rule in BoatEmission)
        {
            if (rule.Pattern != null && WildcardUtil.Match(rule.Pattern, code)) return rule;
        }
        return BoatEmissionRule.None;
    }

    // Brings every value into its range and adds a line to warnings for each one it changes; spelling and case of a
    // valid value are fixed silently. The bounds of the core are those SimulationParams clamps to on its own.
    public void Sanitize(List<string> warnings)
    {
        foreach (string key in unknown?.Keys ?? Enumerable.Empty<string>())
        {
            warnings.Add($"unknown key '{key}' is ignored and left out of the file");
        }
        unknown = null;

        (BoatRule, BoatSpawning) = Rule(warnings, nameof(BoatSpawning), BoatSpawning, EffectSpawningRule.SimulationAndPlanes);
        (PlayerRule, PlayerSpawning) = Rule(warnings, nameof(PlayerSpawning), PlayerSpawning, EffectSpawningRule.OnlySimulation);
        (OtherPlayersRule, OtherPlayersSpawning) = Rule(warnings, nameof(OtherPlayersSpawning), OtherPlayersSpawning, EffectSpawningRule.OnlySimulation);
        (MobRule, MobSpawning) = Rule(warnings, nameof(MobSpawning), MobSpawning, EffectSpawningRule.OnlySimulation);
        (ItemRule, ItemSpawning) = Rule(warnings, nameof(ItemSpawning), ItemSpawning, EffectSpawningRule.OnlySimulation);
        (ProjectileRule, ProjectileSpawning) = Rule(warnings, nameof(ProjectileSpawning), ProjectileSpawning, EffectSpawningRule.OnlySimulation);

        WavePropagationFactor = Clamp(warnings, nameof(WavePropagationFactor), WavePropagationFactor,
            SimulationParams.MinPropagation, SimulationParams.MaxPropagation, SimulationParams.DefaultPropagation);
        WaveDecayFactor = Clamp(warnings, nameof(WaveDecayFactor), WaveDecayFactor, 0f, 1f, SimulationParams.DefaultDecay);
        InitialStrength = Clamp(warnings, nameof(InitialStrength), InitialStrength, 0, MaxStrength);
        SplashStrength = Clamp(warnings, nameof(SplashStrength), SplashStrength, 0, MaxStrength);
        if (WakeResolution is not (8 or 16 or 32))
        {
            warnings.Add($"{Key(nameof(WakeResolution))} {WakeResolution} is not 8, 16 or 32, using 16");
            WakeResolution = 16;
        }
        MaxNodes = Clamp(warnings, nameof(MaxNodes), MaxNodes, 0, WakeAtlasCpu.Capacity);
        FloodFillDistance = Clamp(warnings, nameof(FloodFillDistance), FloodFillDistance,
            SimulationParams.MinFloodDistance, SimulationParams.MaxFloodDistance);
        FloodFillTickDelay = Clamp(warnings, nameof(FloodFillTickDelay), FloodFillTickDelay,
            SimulationParams.MinFloodDelay, SimulationParams.MaxFloodDelay);

        WakeOpacity = Clamp(warnings, nameof(WakeOpacity), WakeOpacity, 0f, 1f, DefaultWakeOpacity);
        BlendStrength = Clamp(warnings, nameof(BlendStrength), BlendStrength, 0f, 1f, WakePalette.DefaultBlendStrength);
        SanitizePalette(warnings);

        MaxSplashPlaneVelocity = Clamp(warnings, nameof(MaxSplashPlaneVelocity), MaxSplashPlaneVelocity, 0.05f, 5f, 0.5f);

        SwimmerOffset = Clamp(warnings, nameof(SwimmerOffset), SwimmerOffset, 0f, MaxSwimmerOffset, DefaultSwimmerOffset);
        SanitizeBoats(warnings);

        string? trace = PathTracingFoam?.Trim().ToLowerInvariant();
        if (trace is not (TraceFoamAuto or TraceFoamOff))
        {
            string got = PathTracingFoam == null ? "null" : $"'{PathTracingFoam}'";
            warnings.Add($"{Key(nameof(PathTracingFoam))} {got} is not {TraceFoamAuto} or {TraceFoamOff}; using {TraceFoamAuto}");
            trace = TraceFoamAuto;
        }
        PathTracingFoam = trace;
        TraceFoamOn = trace == TraceFoamAuto;
    }

    void SanitizePalette(List<string> warnings)
    {
        ReadOnlySpan<uint> defaults = WakePalette.DefaultColors;
        if (WakeColorIntervals == null || !WakePalette.AreValidThresholds(WakeColorIntervals))
        {
            string got = WakeColorIntervals == null ? "null" : "[" + string.Join(", ", WakeColorIntervals.Select(Format)) + "]";
            warnings.Add($"{Key(nameof(WakeColorIntervals))} {got} must be 8 numbers inside (0, 1), none below the one before; using the defaults");
            WakeColorIntervals = WakePalette.DefaultThresholds.ToArray();
        }
        if (WakeColors == null || WakeColors.Length != defaults.Length)
        {
            warnings.Add($"{Key(nameof(WakeColors))} must hold {defaults.Length} colours, it has {WakeColors?.Length.ToString(CultureInfo.InvariantCulture) ?? "none"}; using the defaults");
            WakeColors = DefaultColors();
        }
        if (WakeColorAlphas == null || WakeColorAlphas.Length != defaults.Length)
        {
            warnings.Add($"{Key(nameof(WakeColorAlphas))} must hold {defaults.Length} numbers 0..255, it has {WakeColorAlphas?.Length.ToString(CultureInfo.InvariantCulture) ?? "none"}; using the defaults");
            WakeColorAlphas = DefaultAlphas();
        }
        var colors = new uint[defaults.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            if (!ColorParse.TryParse(WakeColors[i], out uint rgb, out int? own))
            {
                rgb = defaults[i] & 0xffffff;
                string got = WakeColors[i] == null ? "null" : $"'{WakeColors[i]}'";
                warnings.Add($"{Key(nameof(WakeColors))}[{i}] {got} is not #RRGGBB, using {ColorParse.FormatRgb(rgb)}");
            }
            int alpha = own ?? Clamp(warnings, $"{nameof(WakeColorAlphas)}[{i}]", WakeColorAlphas[i], 0, 255);
            WakeColors[i] = ColorParse.FormatRgb(rgb);
            WakeColorAlphas[i] = alpha;
            colors[i] = (uint)alpha << 24 | rgb;
        }
        Colors = colors;
    }

    void SanitizeBoats(List<string> warnings)
    {
        string key = Key(nameof(BoatEmission));
        if (BoatEmission == null)
        {
            warnings.Add($"{key} is null, using the defaults");
            BoatEmission = DefaultBoats();
        }
        var kept = new List<BoatEmissionRule>(BoatEmission.Length);
        for (int i = 0; i < BoatEmission.Length; i++)
        {
            BoatEmissionRule? rule = BoatEmission[i];
            string at = $"{key}[{i}]";
            if (rule == null)
            {
                warnings.Add($"{at} is dropped: it is empty");
                continue;
            }
            string? reason = rule.Sanitize(at, warnings);
            if (reason != null)
            {
                warnings.Add($"{at} is dropped: {reason}");
                continue;
            }
            kept.Add(rule);
        }
        BoatEmission = kept.ToArray();
    }

    static (EffectSpawningRule, string) Rule(List<string> warnings, string member, string? text, EffectSpawningRule fallback)
    {
        if (EffectSpawningRule.TryParse(text, out EffectSpawningRule rule)) return (rule, rule.Name);
        string got = text == null ? "null" : $"'{text}'";
        warnings.Add($"{Key(member)} {got} is not one of {string.Join(", ", EffectSpawningRule.Names)}; using {fallback.Name}");
        return (fallback, fallback.Name);
    }

    internal static float Clamp(List<string> warnings, string member, float value, float min, float max, float fallback)
    {
        if (float.IsNaN(value))
        {
            warnings.Add($"{Key(member)} is NaN, using {Format(fallback)}");
            return fallback;
        }
        if (value >= min && value <= max) return value;
        float clamped = Math.Clamp(value, min, max);
        warnings.Add($"{Key(member)} {Format(value)} is outside {Format(min)}..{Format(max)}, using {Format(clamped)}");
        return clamped;
    }

    internal static int Clamp(List<string> warnings, string member, int value, int min, int max)
    {
        if (value >= min && value <= max) return value;
        int clamped = Math.Clamp(value, min, max);
        warnings.Add($"{Key(member)} {value} is outside {min}..{max}, using {clamped}");
        return clamped;
    }

    static string Format(float v) => v.ToString(CultureInfo.InvariantCulture);

    static string[] DefaultColors() => WakePalette.DefaultColors.ToArray().Select(c => ColorParse.FormatRgb(c)).ToArray();

    static int[] DefaultAlphas() => WakePalette.DefaultColors.ToArray().Select(c => (int)(c >> 24)).ToArray();

    // The two vanilla boats, raft and sailed boat of any wood (FINDINGS "Ванильные лодки"), as the author picked them
    // on the water (T07.02, DESIGN fork 19): the raft from a row of 4 points from its bow edge to its stern edge, 1.5
    // blocks either side of Pos; the sailed boat from a row of 9 points, 4.4 blocks ahead of Pos (1.5 behind the bow)
    // down to its stern edge 2.9 behind. The bow clouds as the author placed them by the posts of .wakes debug hull
    // (T07.04): the raft's pair 0.3 ahead of its flat leading edge, spread half again as wide as Wakes'; the sailed boat's
    // pointed bow from its tip 0.6 inside the bow edge back along both flanks to 1.5 inside it and 0.8 out, 7 points.
    // Shipwright 1.4.1's eight boats (T08.06; its largejunker is disabled) read the same way off the waterline of each
    // shape, which gives the sailed boat's picks back within 0.15 block: the row from where the hull first reaches 0.6 of
    // its widest at the waterline down to the stern edge, the bow point 0.1 inside the waterline tip, the side points
    // where the row starts at 0.7 of the hull's half-width there; the square-ended punter takes the raft's pair. The reed
    // longboat, the papyrus sailer and the punter ride with their raised ends out of the water, so their tips lie 0.8-0.9
    // inside the bow edge of their boxes; the drakkar's stem runs 0.5 past its boxes. The author moved the clouds of five
    // of them on the water (2026-10-11): the kayak, the totora, the reed longboat and the papyrus sailer spray from three
    // points across the bow near its tip, the coracle from 7 points along its round bow.
    internal static BoatEmissionRule[] DefaultBoats() =>
    [
        new() { Code = "game:boat-raft-*", Offset = 0.5f, OffsetTo = -0.5f, StrengthMultiplier = 2f, CloudBow = 0.3f, CloudSpread = 1.5f },
        new()
        {
            Code = "game:boat-sailed-*", Offset = 0.5f, OffsetTo = -0.329f, StrengthMultiplier = 2f,
            CloudBow = -0.6f, CloudSides = [-1.5f, 0.8f], CloudBetween = 2,
        },
        new()
        {
            Code = "shipwright:boat-catboat-*", Offset = 0.3f, OffsetTo = -0.5f, StrengthMultiplier = 2f,
            CloudBow = -0.2f, CloudSides = [-1.85f, 0.5f], CloudBetween = 2,
        },
        new()
        {
            Code = "shipwright:boat-coracle-*", Offset = 0.5f, OffsetTo = -0.5f, StrengthMultiplier = 2f,
            CloudBow = 0f, CloudSides = [-0.5f, 1f], CloudBetween = 2, CloudSpread = 1.5f,
        },
        new()
        {
            Code = "shipwright:boat-drakkar-*", Offset = 0.293f, OffsetTo = -0.526f, StrengthMultiplier = 2f,
            CloudBow = 0.4f, CloudSides = [-3.4f, 0.75f], CloudBetween = 4,
        },
        new()
        {
            Code = "shipwright:boat-kayak-*", Offset = 0.25f, OffsetTo = -0.5f, StrengthMultiplier = 2f,
            CloudBow = 0f, CloudSides = [-0.25f, 0.2f],
        },
        new() { Code = "shipwright:boat-punter-*", Offset = 0.38f, OffsetTo = -0.5f, StrengthMultiplier = 2f, CloudBow = -0.8f, CloudSpread = 1.5f },
        new()
        {
            Code = "shipwright:boat-reedlongboat-*", Offset = 0.326f, OffsetTo = -0.5f, StrengthMultiplier = 2f,
            CloudBow = -1f, CloudSides = [-1f, 0.25f],
        },
        new()
        {
            Code = "shipwright:boat-sailedreedboat-*", Offset = 0.326f, OffsetTo = -0.5f, StrengthMultiplier = 2f,
            CloudBow = -1.2f, CloudSides = [-1.2f, 0.25f],
        },
        new()
        {
            Code = "shipwright:boat-totora-*", Offset = 0.31f, OffsetTo = -0.5f, StrengthMultiplier = 2f,
            CloudBow = -0.75f, CloudSides = [-0.75f, 0.1f],
        },
    ];
}

// The keys of a boatEmission rule a command sets: the wake's (.wakes emission, T07.02) or the clouds' (.wakes bow).
[Flags]
enum BoatRuleKeys
{
    Emission = 1,
    Clouds = 2,
    All = Emission | Clouds,
}

// Fork 19: the point a boat's wake starts from and its strength, set by boat type in the first game build (T07.02);
// where its bow clouds come from (T07.04).
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
sealed class BoatEmissionRule
{
    public static readonly BoatEmissionRule None = new();

    // Wakes' bow point, bbWidth - 0.3 ahead of the entity, taken as 0.3 inside the leading edge of the hull.
    public const float WakesCloudBow = -0.3f;
    public const float MinCloudAlong = -20f, MaxCloudAlong = 5f, MaxCloudAcross = 10f, MaxCloudSpread = 4f;
    public const int MaxCloudBetween = 8;

    // An entity code with * wildcards (WildcardUtil.Match); without a domain it is game:, and *: takes any domain.
    public string? Code { get; set; }

    // A point every PointSpacing blocks of the hull, wider only past MaxPoints, which a span over 63 blocks would need:
    // one point on a straight course passes a block every 1/speed seconds, and a node lives MaxAge ticks without a touch.
    public const float PointSpacing = 1f;
    public const int MaxPoints = 64;

    // Along the course from Pos, in hull lengths: positive toward the bow, negative toward the stern.
    public float Offset { get; set; }

    // The other end of a row of points from Offset, in the same units; null for the one point at Offset. A node lives
    // 1.5 s after its last touch, so a single point leaves a trail as long as the boat goes in 1.5 s: a slow sailed
    // boat drawing near its bow had nothing behind its stern (T07.02). Points down the hull touch it all the way.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? OffsetTo { get; set; }

    // Times the strength of the trail at each point; swimmers do not take it. Each point adds its impulse to the blocks
    // it passes in a tick of its own, so a row of n points puts up to n times the impulse of one into the water, spread
    // over n ticks; segments that overlap within a tick, past about 18 b/s on the sailed boat or in a sharp turn, keep
    // the larger value.
    public float StrengthMultiplier { get; set; } = 1f;

    // The bow clouds, in blocks from the leading edge of the hull along the course, positive ahead of it: the bow point
    // on the boat's axis, and when CloudSides is given, a pair of side points [along, across], mirrored about the axis,
    // with CloudBetween points evenly on the line from the bow point to each of them. Without the pair each step spawns
    // Wakes' two clouds at the bow point, spread across a quarter of the hull's width at random times CloudSpread; with
    // it, one cloud at every point (CloudPoint), so a pointed bow sprays along its flanks.
    public float CloudBow { get; set; } = WakesCloudBow;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float[]? CloudSides { get; set; }

    public int CloudBetween { get; set; }

    public float CloudSpread { get; set; } = 1f;

    // 1 without side points, where the pair of Wakes goes at the bow point; otherwise the bow point and each side's line.
    [JsonIgnore] public int CloudPointCount => CloudSides == null ? 1 : 3 + 2 * CloudBetween;

    // Point i of CloudPointCount as (along, across): the bow point first, then each side from the bow point out to its
    // side point.
    public (float Along, float Across) CloudPoint(int i)
    {
        if (i == 0 || CloudSides is not { } sides) return (CloudBow, 0);
        int perSide = CloudBetween + 1;
        float t = (float)((i - 1) % perSide + 1) / perSide;
        float sign = i <= perSide ? -1 : 1;
        return (CloudBow + (sides[0] - CloudBow) * t, sign * sides[1] * t);
    }

    [JsonExtensionData] IDictionary<string, JToken>? unknown;

    [JsonIgnore] public AssetLocation? Pattern { get; private set; }

    // Null when the rule is kept; otherwise why it is not.
    public string? Sanitize(string at, List<string> warnings)
    {
        foreach (string key in unknown?.Keys ?? Enumerable.Empty<string>())
        {
            warnings.Add($"{at}: unknown key '{key}' is ignored and left out of the file");
        }
        unknown = null;
        string code = Code?.Trim() ?? "";
        if (code.Length == 0) return "it has no code";
        // WildcardUtil takes a code that starts with @ for a regex and throws on a broken one at the first boat.
        if (code.Contains('@')) return $"its code '{code}' is a regex, only * wildcards are taken";
        // AssetLocation keeps spaces around the colon in the domain and path, and an empty domain or path matches no
        // entity; nor does a second colon, which stays in the path, or a * inside a domain, which WildcardUtil.Match
        // compares as text unless the domain is * alone. Such a rule would sit in the file and never apply.
        var pattern = new AssetLocation(code);
        if (code.Any(char.IsWhiteSpace) || pattern.Domain.Length == 0 || pattern.Path.Length == 0 || pattern.Path.Contains(':')
            || pattern.Domain != "*" && pattern.Domain.Contains('*'))
        {
            return $"its code '{code}' matches no entity: spaces, a second colon, a * inside the domain, or an empty domain or path";
        }
        Code = code;
        Offset = WakesConfig.Clamp(warnings, $"{at}.offset", Offset, -1f, 1f, 0f);
        if (OffsetTo is { } to) OffsetTo = WakesConfig.Clamp(warnings, $"{at}.offsetTo", to, -1f, 1f, Offset);
        StrengthMultiplier = WakesConfig.Clamp(warnings, $"{at}.strengthMultiplier", StrengthMultiplier, 0f, 10f, 1f);
        CloudBow = WakesConfig.Clamp(warnings, $"{at}.cloudBow", CloudBow, MinCloudAlong, MaxCloudAlong, WakesCloudBow);
        if (CloudSides is { } sides)
        {
            if (sides.Length != 2)
            {
                warnings.Add($"{WakesConfig.Key($"{at}.cloudSides")} needs two numbers, along and across; left out, the bow point sprays alone");
                CloudSides = null;
            }
            else
            {
                CloudSides =
                [
                    WakesConfig.Clamp(warnings, $"{at}.cloudSides[0]", sides[0], MinCloudAlong, MaxCloudAlong, CloudBow),
                    // The pair is mirrored, so a negative across means the same points.
                    WakesConfig.Clamp(warnings, $"{at}.cloudSides[1]", Math.Abs(sides[1]), 0f, MaxCloudAcross, 0f),
                ];
            }
        }
        CloudBetween = WakesConfig.Clamp(warnings, $"{at}.cloudBetween", CloudBetween, 0, MaxCloudBetween);
        CloudSpread = WakesConfig.Clamp(warnings, $"{at}.cloudSpread", CloudSpread, 0f, MaxCloudSpread, 1f);
        Pattern = pattern;
        return null;
    }

    // For a command that changes some keys of a rule of the config in use and writes it back.
    public BoatEmissionRule Copy()
    {
        var copy = (BoatEmissionRule)MemberwiseClone();
        copy.CloudSides = (float[]?)CloudSides?.Clone();
        return copy;
    }

    // How many points a hull of this length gets, both ends counted.
    public int Points(float hullLength)
    {
        if (OffsetTo is not { } to) return 1;
        // The epsilon keeps a span of whole blocks from rounding up to one more point.
        double gaps = Math.Ceiling(Math.Abs(to - Offset) * hullLength / PointSpacing - 1e-4);
        // A NaN length would cast to 0 points; both ends are always counted.
        if (!(gaps >= 0)) return 1;
        return (int)Math.Clamp(gaps + 1, 1, MaxPoints);
    }

    // Point i of n, evenly from Offset to OffsetTo.
    public float OffsetAt(int i, int n) => n <= 1 ? Offset : Offset + (OffsetTo!.Value - Offset) * i / (n - 1);
}

// ModConfig/immersivewakes.json, one file for every world of the profile, read at the start of a world, on
// .wakes reload and when it changes on disk (Poll). A file that reads cleanly is sanitised, written back when its text
// changed and taken; a missing one gets the defaults written; an unreadable one is left as it is.
sealed class WakesConfigFile
{
    public const string FileName = "immersivewakes.json";
    const int PollIntervalMs = 500, MissingRecheckMs = 100;
    static readonly (long Ticks, long Length) Missing = (0, -1);

    readonly ICoreClientAPI capi;
    readonly ILogger logger;

    public WakesConfigFile(ICoreClientAPI capi, ILogger logger)
    {
        this.capi = capi;
        this.logger = logger;
    }

    public WakesConfig Current { get; private set; } = Defaults();

    string origin = "the defaults, nothing read yet";
    bool stale;

    // The write time and length of the file as the mod last read or wrote it; null before the first read and when
    // the file could not be looked at.
    (long Ticks, long Length)? seen;
    long nextPollMs;
    // The file was gone at the last poll, which looks again shortly before the defaults go in.
    bool goneOnce;

    // Where Current came from, for .wakes config; a reload that failed since then is named too.
    public string Origin => stale ? origin + " (the last reload could not read the file, these values stayed)" : origin;

    // After a reload (.wakes reload, a command, Poll) took a new config, with that config. T05.02 rebuilds the core and
    // recolours the nodes here. A subscriber that throws is logged, and the others still run.
    public event Action<WakesConfig>? Changed;

    public static string FilePath => Path.Combine(GamePaths.ModConfig, FileName);

    // The start of the world: the file's config, or the defaults when the file cannot be read.
    public void Load() => Read(keepCurrent: false);

    // An unreadable file keeps the values in use: a typo in an edit changes nothing until it is fixed.
    public ConfigReport Reload()
    {
        ConfigReport report = Read(keepCurrent: true);
        if (report.Taken) Notify();
        return report;
    }

    // ConfigLib publishes configlib:<domain>:config-saved on the client only once a server running ConfigLib has sent
    // its registry (ConfigRegistry.FromBytes); a server without ConfigLib sends none (T06.07). So the window's Save is
    // seen here: twice a second the write time and length of the file are compared with the mod's last read or
    // write, and a change reloads it, a hand edit too.
    // The caller is a render stage, which runs on pause, where tick listeners stand still: a save made in the window
    // on pause shows before the window closes. The report of the reload, or null when the file did not change.
    // An editor that saves through a temporary file and a rename leaves no file under the name for a moment; a reload
    // then would write the defaults and rebuild the core. So a file found gone is looked at again 0.1 s later, and only
    // one gone both times gets the defaults.
    public ConfigReport? Poll()
    {
        long now = Environment.TickCount64;
        if (now < nextPollMs) return null;
        nextPollMs = now + PollIntervalMs;
        if (Stamp() is not { } stamp) return null;
        if (stamp == Missing && stamp != seen && !goneOnce)
        {
            goneOnce = true;
            nextPollMs = now + MissingRecheckMs;
            return null;
        }
        goneOnce = false;
        return stamp == seen ? null : Reload();
    }

    // Null when the file cannot be looked at; the next poll tries again.
    static (long Ticks, long Length)? Stamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : Missing;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // .wakes emission (T07.02) and .wakes bow (T07.04): the command's own keys of each rule go into the first rule of
    // the file with the same code; then the file is reloaded once. Its other keys keep their text, edits not reloaded
    // yet among them, until that reload writes the file back in its own form. A rule the file lacks is added in front of
    // the others, so a broader rule cannot shadow it, with every key of the rule passed, which the command copied from
    // the one the boat goes by now. A file the game cannot load is not touched; a missing one starts from the defaults.
    public ConfigReport SetBoatEmission(IReadOnlyList<BoatEmissionRule> rules, BoatRuleKeys own)
    {
        if (Open(out JObject root) is { } failed) return failed;

        // Newtonsoft takes a key in any case, so the file's own spelling is the one to change.
        string listKey = WakesConfig.Key(nameof(WakesConfig.BoatEmission));
        JProperty? listProp = root.Property(listKey, StringComparison.OrdinalIgnoreCase);
        if (listProp?.Value is not JArray list)
        {
            // A missing key or null loads as the defaults, and the boat the command leaves alone keeps its rule.
            list = JArray.FromObject(WakesConfig.DefaultBoats());
            if (listProp != null) listProp.Value = list;
            else root.Add(listKey, list);
        }
        string codeKey = WakesConfig.Key(nameof(BoatEmissionRule.Code));
        for (int i = 0; i < rules.Count; i++)
        {
            BoatEmissionRule rule = rules[i];
            JObject? entry = list.OfType<JObject>().FirstOrDefault(o =>
                o.GetValue(codeKey, StringComparison.OrdinalIgnoreCase) is JValue { Value: string code } && code.Trim() == rule.Code);
            BoatRuleKeys keys = entry == null ? BoatRuleKeys.All : own;
            entry?.Remove();
            entry ??= new JObject { [codeKey] = rule.Code };
            list.Insert(i, entry);
            if ((keys & BoatRuleKeys.Emission) != 0)
            {
                Set(entry, WakesConfig.Key(nameof(BoatEmissionRule.Offset)), rule.Offset);
                SetOrRemove(entry, WakesConfig.Key(nameof(BoatEmissionRule.OffsetTo)), rule.OffsetTo is { } to ? (JToken)to : null);
                Set(entry, WakesConfig.Key(nameof(BoatEmissionRule.StrengthMultiplier)), rule.StrengthMultiplier);
            }
            if ((keys & BoatRuleKeys.Clouds) != 0)
            {
                Set(entry, WakesConfig.Key(nameof(BoatEmissionRule.CloudBow)), rule.CloudBow);
                SetOrRemove(entry, WakesConfig.Key(nameof(BoatEmissionRule.CloudSides)),
                    rule.CloudSides is { } sides ? new JArray(sides[0], sides[1]) : null);
                Set(entry, WakesConfig.Key(nameof(BoatEmissionRule.CloudBetween)), rule.CloudBetween);
                Set(entry, WakesConfig.Key(nameof(BoatEmissionRule.CloudSpread)), rule.CloudSpread);
            }
        }
        return WriteAndReload(root);

        // A key the rule leaves out goes from the entry; the others keep their place.
        static void SetOrRemove(JObject entry, string key, JToken? value)
        {
            if (value != null) Set(entry, key, value);
            else entry.Property(key, StringComparison.OrdinalIgnoreCase)?.Remove();
        }
    }

    // .wakes swimmer: one top-level key into the file as SetBoatEmission writes a rule, the other keys keeping their
    // text, then one reload.
    public ConfigReport SetValue(string key, JToken value)
    {
        if (Open(out JObject root) is { } failed) return failed;
        Set(root, key, value);
        return WriteAndReload(root);
    }

    static void Set(JObject o, string key, JToken value)
    {
        if (o.Property(key, StringComparison.OrdinalIgnoreCase) is { } p) p.Value = value;
        else o.Add(key, value);
    }

    // The file to change, or the defaults when there is none; null when it opened, otherwise the reply. A file the game
    // cannot load is not touched.
    ConfigReport? Open(out JObject root)
    {
        root = new JObject();
        if (!File.Exists(FilePath))
        {
            root = JObject.FromObject(Defaults());
            return null;
        }
        try
        {
            string text = File.ReadAllText(FilePath);
            // The call LoadModConfig makes: JSON with a value of the wrong type parses as a JObject, and the reload
            // would refuse the file after it got the new value.
            if (JsonConvert.DeserializeObject<WakesConfig>(text) == null) throw new InvalidDataException("The file holds no JSON object.");
            root = JObject.Parse(text);
            return null;
        }
        catch (Exception e)
        {
            stale = true;
            return new ConfigReport($"could not read {FilePath}: {e.Message} Nothing was changed.", [], Taken: false);
        }
    }

    ConfigReport WriteAndReload(JObject root)
    {
        try
        {
            File.WriteAllText(FilePath, root.ToString(Formatting.Indented));
        }
        catch (Exception e)
        {
            return new ConfigReport($"could not write {FilePath}: {e.Message} Nothing was changed.", [], Taken: false);
        }
        return Reload();
    }

    ConfigReport Read(bool keepCurrent)
    {
        // Taken before the text: a write that lands between the two shows up on the next poll. A file that does not
        // parse keeps its stamp too, so Poll warns about it once and waits for the next change.
        var before = Stamp();
        seen = before;
        WakesConfig? loaded;
        string? text;
        try
        {
            // What LoadModConfig does, with the text kept: a second read for the comparison below could take a save
            // made in between, and the write-back would put this read over it.
            text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            loaded = text == null ? null : JsonConvert.DeserializeObject<WakesConfig>(text);
            // Null for an empty file or "null". A file that is there and empty may be cut short by whatever writes
            // it, so it counts as broken: on a reload the values in use stay.
            if (loaded == null && text != null) throw new InvalidDataException("The file holds no JSON object.");
        }
        catch (Exception e)
        {
            // A JsonException for broken JSON or a value of the wrong type, an IOException for a locked file. One bad
            // value in the file takes the whole file down, so the message has to name the place.
            string reason = $"could not read {FilePath}: {e.Message} The file is left as it is, " +
                            (keepCurrent ? "the values in use stay." : "the mod runs on the defaults.");
            logger.Warning("{0}", "C" + reason[1..]);
            // A file another program holds without sharing it is fine once let go: the next poll reads it again.
            if (e is IOException) seen = null;
            if (keepCurrent) stale = true;
            else
            {
                Current = Defaults();
                origin = "the defaults, the file could not be read";
            }
            return new ConfigReport(reason, [], Taken: false);
        }

        WakesConfig config = loaded ?? new WakesConfig();
        var warnings = new List<string>();
        config.Sanitize(warnings);
        foreach (string w in warnings) logger.Warning("{0}", $"{FileName}: {w}");

        bool written = true;
        try
        {
            // Writes the fixes and the keys the file lacked, so the file shows what the mod runs on. A file that
            // already holds that text is left alone: ConfigLib watches the file it saves and rereads it (T06.07),
            // and an editor with the file open would see it change on every reload. A file that changed or appeared
            // after it was read is not written either: the next poll takes it.
            if (text != JsonConvert.SerializeObject(config, Formatting.Indented))
            {
                if (Stamp() != before) throw new IOException("The file changed while it was read; the next poll reads it again.");
                capi.StoreModConfig(config, FileName);
                seen = Stamp();
            }
        }
        catch (Exception e)
        {
            written = false;
            string line = $"Could not write {FilePath}: {e.Message} The values in use are not in the file.";
            logger.Warning("{0}", line);
            warnings.Add(line);
        }

        Current = config;
        origin = (loaded != null, written) switch
        {
            (true, true) => "the file",
            (true, false) => "the file, with fixes that could not be written to it",
            (false, true) => "the defaults, written to a new file",
            (false, false) => "the defaults, the file could not be written",
        };
        stale = false;
        string summary = loaded != null ? $"read {FilePath}" : $"no config in {FilePath}, " + (written ? "wrote the defaults" : "using the defaults");
        if (loaded == null) logger.Notification("{0}", summary);
        return new ConfigReport(summary, warnings, Taken: true);
    }

    void Notify()
    {
        if (Changed == null) return;
        foreach (Action<WakesConfig> handler in Changed.GetInvocationList().Cast<Action<WakesConfig>>())
        {
            try
            {
                handler(Current);
            }
            catch (Exception e)
            {
                logger.Error("{0}", $"Applying the new config failed in {handler.Method.DeclaringType?.Name}.{handler.Method.Name}: {e}");
            }
        }
    }

    // Every key the file holds, in the file's order, as one line each; the same serializer StoreModConfig uses.
    public IEnumerable<(string Key, string Value)> Values()
    {
        foreach (JProperty p in JObject.FromObject(Current).Properties())
        {
            yield return (p.Name, p.Value.ToString(Formatting.None));
        }
    }

    static WakesConfig Defaults()
    {
        var config = new WakesConfig();
        config.Sanitize([]);
        return config;
    }
}

// Summary is one line for the chat; Warnings are the values Sanitize fixed and a failed write. Taken is false when the
// file could not be read and nothing changed.
sealed record ConfigReport(string Summary, List<string> Warnings, bool Taken);
