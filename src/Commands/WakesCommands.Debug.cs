#if DEBUG
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using ImmersiveWakes.Config;
using ImmersiveWakes.Debug;
using ImmersiveWakes.Effects;
using ImmersiveWakes.Game;
using ImmersiveWakes.Render;
using ImmersiveWakes.Render.Trace;
using ImmersiveWakes.Simulation;
using ImmersiveWakes.Sources;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Commands;

// The debug subcommands of .wakes, the tools of the plan's checks and of the S1 spike. A player of a release build
// has no use for them, and freeze stalls the client.
sealed partial class WakesCommands
{
    // Pairs of .wakes clouds at most: twice as many clouds as the pool holds, so one call can show the oldest dropped.
    const int MaxCloudPairs = SplashCloudPool.Capacity;
    // Blocks a second of the bow the clouds of .wakes clouds come from.
    const double CloudSpeed = 10;

    DebugSplash? splash;
    long statusUploads, statusTicks = Stopwatch.GetTimestamp();

    partial void RegisterDebug()
    {
        Root
            .BeginSubCommand("splash")
                .WithDescription($"Debug: a splash where you look at the water, or n splashes in random water blocks within {DebugSplash.Radius} blocks; the nodes come with the next step")
                .WithArgs(capi.ChatCommands.Parsers.OptionalInt("n"))
                .HandleWith(OnSplash)
            .EndSubCommand()
            .BeginSubCommand("clouds")
                .WithDescription($"Debug: n pairs of bow clouds, 1 when left out and {MaxCloudPairs} at most, on the water block nearest to you, each pair off in a random direction as from a bow at {CloudSpeed} b/s; past the pool's {SplashCloudPool.Capacity} the oldest clouds go")
                .WithArgs(capi.ChatCommands.Parsers.OptionalInt("n", 1))
                .HandleWith(OnClouds)
            .EndSubCommand()
            .BeginSubCommand("ghost")
                .WithDescription($"Debug: 'line|circle <speed b/s>[..<speed>] <width|boat> [n]' starts n invisible sources on the water nearest to you, a line of {GhostSource.Length} blocks along your look and back or a circle of radius {GhostSource.Radius} and wider ones around it, with initialStrength, times the rule's strength for a boat; a speed range spreads the n speeds evenly, the first innermost; a boat ({BoatNames}) goes with the hull of that boat, the row of points of its boatEmission rule and its cloud points; 'clear' stops them all")
                .WithArgs(capi.ChatCommands.Parsers.Word("line|circle|clear"), capi.ChatCommands.Parsers.OptionalWord("speed[..speed]"),
                    capi.ChatCommands.Parsers.OptionalWord("width|boat"), capi.ChatCommands.Parsers.OptionalInt("n", 1))
                .HandleWith(OnGhost)
            .EndSubCommand()
            .BeginSubCommand("emission")
                .WithDescription($"Debug for T07.02: '<boat>|both <point>[..<point>] [strength]' writes the boatEmission rule of a boat ({BoatNames}) or of both vanilla boats into the config file and reloads it; a point is pos, bow or stern (the hull's edges) or an offset in hull lengths toward the bow, -1..1, and two points give a row of points a block apart; the strength 0..10 is 1 when left out; without arguments, the rules in use")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("boat|both"), capi.ChatCommands.Parsers.OptionalWord("point[..point]"),
                    capi.ChatCommands.Parsers.OptionalWord("strength"))
                .HandleWith(OnEmission)
            .EndSubCommand()
            .BeginSubCommand("bow")
                .WithDescription(string.Create(CultureInfo.InvariantCulture, $"Debug for T07.04: '<boat>|both <bow> [<along> <across> [<between>]]' writes where the bow clouds of a boat ({BoatNames}) or of both vanilla boats come from into its boatEmission rule and reloads the config. <bow> and <along> are blocks from the leading edge of the hull along the course, positive ahead of it, {BoatEmissionRule.MinCloudAlong}..{BoatEmissionRule.MaxCloudAlong}: <bow> places the point on the boat's axis ({BoatEmissionRule.WakesCloudBow} is Wakes'), <along> <across> a pair of side points mirrored about the axis, <across> blocks off it 0..{BoatEmissionRule.MaxCloudAcross}, <between> 0..{BoatEmissionRule.MaxCloudBetween} points evenly on the line from the bow point to each side point, 0 when left out; with side points each step sprays one cloud at every point instead of Wakes' pair. '<boat>|both spread <times>' widens or narrows Wakes' pair across the hull, 0..{BoatEmissionRule.MaxCloudSpread}, 1 is Wakes'. '.wakes debug hull' puts a white post on each point of a boat going bow first; without arguments, the points in use"))
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("boat|both"), capi.ChatCommands.Parsers.OptionalWord("bow|spread"),
                    capi.ChatCommands.Parsers.OptionalWord("along|times"), capi.ChatCommands.Parsers.OptionalWord("across"),
                    capi.ChatCommands.Parsers.OptionalWord("between"))
                .HandleWith(OnBow)
            .EndSubCommand()
            .BeginSubCommand("swimmer")
                .WithDescription(string.Create(CultureInfo.InvariantCulture, $"Debug: '<blocks>' writes swimmerOffset, how far ahead of a swimming player's pos along its motion its wake starts, 0..{WakesConfig.MaxSwimmerOffset}, into the config file and reloads it; '.wakes debug' prints it on the line of a swimmer; without an argument, the offset in use"))
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("blocks"))
                .HandleWith(OnSwimmer)
            .EndSubCommand()
            .BeginSubCommand("mode")
                .WithDescription("Debug: core modes until you leave the world; 'fade age|amp' ends a node by its age as Wakes or keeps it while its waves stand, 'halo lag|sync' reads the neighbours' edges as Wakes or all from one step, 'flood wait|eager' brings a touched block's neighbours by flood fill as Wakes or in the same step, 'edge hard|soft' ends the wave at a block without a node or fades it out before; without arguments, the modes in use")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("fade|halo|flood|edge"), capi.ChatCommands.Parsers.OptionalWord("value"))
                .HandleWith(OnMode)
            .EndSubCommand()
            .BeginSubCommand("look")
                .WithDescription(string.Create(CultureInfo.InvariantCulture, $"Debug: the wake's look until you leave the world; 'shade bare|grey|<0..1>' darkens the water tint the see-through colours blend with, from the bare tint as Wakes (0) to the grey of the water's texture (1), 'lift <0..{WakeLook.MaxLift}>' sets the height over the water in blocks, 'fade auto|<from> <to>' the distances in blocks where the wake fades out, 'far <0..{WakeLook.MaxFarAlpha}>' the factor on the opacity from 64 blocks on, ramped from 16; without arguments, the values in use"))
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("shade|lift|fade|far"), capi.ChatCommands.Parsers.OptionalWord("value"),
                    capi.ChatCommands.Parsers.OptionalWord("to"))
                .HandleWith(OnLook)
            .EndSubCommand()
            .BeginSubCommand("tiles")
                .WithDescription($"S1: lay n tiles with a still ring wave on still water around you, at most {S1Tiles.Capacity}; 'clear' removes them")
                .WithArgs(capi.ChatCommands.Parsers.Word("n|clear"))
                .HandleWith(OnTiles)
            .EndSubCommand()
            .BeginSubCommand("res")
                .WithDescription("S1: texels per block of the tiles, 16 or 32; makes a new tile texture, the wake keeps 32")
                .WithArgs(capi.ChatCommands.Parsers.Word("16|32"))
                .HandleWith(OnRes)
            .EndSubCommand()
            .BeginSubCommand("filter")
                .WithDescription("Debug: minification filter of the wake atlas and the tiles, nearest or mip (NEAREST_MIPMAP_LINEAR, the default)")
                .WithArgs(capi.ChatCommands.Parsers.Word("nearest|mip"))
                .HandleWith(OnFilter)
            .EndSubCommand()
            .BeginSubCommand("tint")
                .WithDescription("S1: water colour the tiles' buckets blend with, bare tint or tint on the grey of water.png")
                .WithArgs(capi.ChatCommands.Parsers.Word("bare|grey"))
                .HandleWith(OnTint)
            .EndSubCommand()
            .BeginSubCommand("tex")
                .WithDescription("Debug: state of the wake atlas and its strip uploads since the last call, and of the tile texture")
                .HandleWith(_ => Reply(TextureStatus()))
            .EndSubCommand()
            .BeginSubCommand("stage")
                .WithDescription("Debug: state of the overlay pass and the mod's shader programs: shadows, Coriaender's cloud shadow, Dark Vision's lift of the dark, the units of the active samplers, frames drawn")
                .HandleWith(OnStage)
            .EndSubCommand()
            .BeginSubCommand("shadows")
                .WithDescription("Debug: the overlay darkens in shadow like the water (on) or ignores the shadow maps (off)")
                .WithArgs(capi.ChatCommands.Parsers.Word("on|off"))
                .HandleWith(OnShadows)
            .EndSubCommand()
            .BeginSubCommand("cori")
                .WithDescription("Debug: CoriaenderShaders' water buffer is set back after the water masks of boats (on, the default) or left as the masks leave it (off); without an argument, its state")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("on|off"))
                .HandleWith(OnCori)
            .EndSubCommand()
            .BeginSubCommand("modlight")
                .WithDescription("Debug: the foam and the splash clouds take Dark Vision's lift of the dark like the water (on, the default) or stay as dark as before (off); without an argument, its state")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("on|off"))
                .HandleWith(OnModLight)
            .EndSubCommand()
            .BeginSubCommand("probe")
                .WithDescription("Debug: GL state around the overlay passes in the next frame against the reference; 'ref' takes the reference, with no tiles and no live wake")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("ref"))
                .HandleWith(OnProbe)
            .EndSubCommand()
            .BeginSubCommand("freeze")
                .WithDescription($"Debug for T08.05: the main thread sleeps for <seconds>, 0.1..{MaxFreezeSeconds}; a single player server runs on, and the position packets of the entities it moves pile up as after a hitch")
                .WithArgs(capi.ChatCommands.Parsers.Word("seconds"))
                .HandleWith(OnFreeze)
            .EndSubCommand()
            .BeginSubCommand("trace")
                .WithDescription($"Debug for T08.05: '<seconds>' records every sample of the sources the server moves for up to {FreezeTrace.MaxSeconds} s, with the freezes of .wakes freeze; without an argument, writes wakes-trace.csv into the log folder and prints the speeds and impulses before the last freeze and after it")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("seconds"))
                .HandleWith(OnTrace)
            .EndSubCommand()
            .BeginSubCommand("vspt")
                .WithDescription("Debug for T11.02, T11.04 and T11.05, the foam and the splash clouds under VS Path Tracing: without an argument, what was found of vspt (Skip, Silent), the blended-layer patch and how often vspt baked the entity atlas with the regions flagged, the regions in the entity atlas, the meshes with their anchors, the steps, mesh writes and frames since the world was entered, qpc, the clock of vspt's frame logs, the hull mask with its boat types and the check of its turn against the boats' renderer, and the last mask check; 'atlas' reports the free space of the entity and item atlases; 'mask' has the next bake run without the hulls and with them and compare the texels")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("atlas|mask"))
                .HandleWith(OnVspt)
            .EndSubCommand();
    }

    TextCommandResult OnVspt(TextCommandCallingArgs args)
    {
        if (args[0] is "atlas")
        {
            return Reply("vspt: " + AtlasSpace.Report("entity atlas", capi.EntityTextureAtlas) + "\n" + AtlasSpace.Report("item atlas", capi.ItemTextureAtlas));
        }
        if (args[0] != null && args[0] is not "mask") return Reply("vspt: no argument, 'atlas' or 'mask'");
        if (session.Trace is not { } trace) return Reply($"vspt: {(session.Vspt is { } vspt ? vspt.Describe() + ", no traced foam" : "vspt not installed")}");
        if (args[0] is "mask") return Reply("vspt: " + trace.ArmMaskCheck());
        return Reply("vspt: " + trace.Status());
    }

    // И20 asks for 1.5-3 s; past 10 s nothing new is seen.
    const float MaxFreezeSeconds = 10;

    TextCommandResult OnFreeze(TextCommandCallingArgs args)
    {
        if (!Number((string)args[0], 0.1f, MaxFreezeSeconds, out float seconds))
        {
            return Reply(string.Create(CultureInfo.InvariantCulture, $"freeze: give the seconds, 0.1..{MaxFreezeSeconds}"));
        }
        FreezeTrace? trace = session.Driver?.Sources.Trace;
        double from = trace?.Now ?? 0;
        var watch = Stopwatch.StartNew();
        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
        trace?.Froze(from, trace.Now);
        return Reply(string.Create(CultureInfo.InvariantCulture, $"freeze: the main thread slept {watch.Elapsed.TotalSeconds:0.000} s"));
    }

    TextCommandResult OnTrace(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver) return Reply("trace: no driver, see client-main.log");
        if (args[0] is not string word)
        {
            return Reply(driver.Sources.Trace?.Summary() ?? "trace: none yet, '.wakes trace <seconds>' starts one");
        }
        if (!Number(word, 1, (float)FreezeTrace.MaxSeconds, out float seconds))
        {
            return Reply(string.Create(CultureInfo.InvariantCulture, $"trace: give the seconds, 1..{FreezeTrace.MaxSeconds}"));
        }
        driver.Sources.Trace = new FreezeTrace(seconds);
        return Reply(string.Create(CultureInfo.InvariantCulture, $"trace: recording the sources the server moves for {seconds:0.#} s"));
    }

    // The boats of the default boatEmission rules, each named by its code between "boat-" and "-*": the vanilla raft and
    // sailed boat first (T07.02), then Shipwright's (T08.06).
    static readonly (string Name, string Code)[] NamedBoats = WakesConfig.DefaultBoats()
        .Select(r => (r.Code![(r.Code!.IndexOf("boat-", StringComparison.Ordinal) + 5)..^2], r.Code!)).ToArray();

    static readonly string BoatNames = string.Join(", ", NamedBoats.Select(b => b.Name));

    TextCommandResult OnEmission(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver || session.Config is not { } config) return Reply("emission: no wake driver, see client-main.log");
        string usage = $"emission: <boat>|both <point>[..<point>] [strength 0..10], a point is pos, bow, stern or an offset -1..1; a boat is {BoatNames}";
        if (args[0] is not string which) return Reply(EmissionStatus(driver.Sources, config.Current));
        (string Name, string Code)[]? boats = BoatsNamed(which);
        if (boats == null || args[1] is not string at) return Reply(usage);
        float strength = 1f;
        if (args[2] is string s && !Number(s, 0, 10, out strength)) return Reply(usage);
        string[] ends = at.Split("..");
        if (ends.Length > 2) return Reply(usage);
        var rules = new BoatEmissionRule[boats.Length];
        for (int i = 0; i < boats.Length; i++)
        {
            (string name, string code) = boats[i];
            var offsets = new float[ends.Length];
            for (int k = 0; k < ends.Length; k++)
            {
                string? error = PointOffset(ends[k], name, driver.Sources.HullMatching(new AssetLocation(code))?.Hull, out offsets[k]);
                if (error != null) return Reply(error.Length == 0 ? usage : error);
            }
            rules[i] = ListedRule(config.Current, driver.Sources, code);
            rules[i].Offset = offsets[0];
            rules[i].OffsetTo = ends.Length == 2 ? offsets[1] : null;
            rules[i].StrengthMultiplier = strength;
        }
        ConfigReport report = config.SetBoatEmission(rules, BoatRuleKeys.Emission);
        return Reply(ReloadText("emission: ", report) + "\n" + EmissionStatus(driver.Sources, config.Current));
    }

    // "both" for the two vanilla boats, as T07.02 and T07.04 set them.
    static (string Name, string Code)[]? BoatsNamed(string which) =>
        which == "both" ? NamedBoats[..2] : NamedBoats.FirstOrDefault(b => b.Name == which) is { Name: not null } named ? [named] : null;

    // A copy of the rule of the config in use with this very code; without one, of the rule a loaded boat of the type
    // goes by now, so a rule the command adds changes nothing but the command's own keys; with no boat loaded either, of
    // the default.
    static BoatEmissionRule ListedRule(WakesConfig current, SourceTracker sources, string code)
    {
        if (current.BoatEmission.FirstOrDefault(r => r.Code == code) is { } listed) return listed.Copy();
        BoatEmissionRule rule = sources.HullMatching(new AssetLocation(code)) is { } seen
            ? current.EmissionFor(seen.Code).Copy()
            : WakesConfig.DefaultBoats().First(r => r.Code == code);
        rule.Code = code;
        return rule;
    }

    TextCommandResult OnBow(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver || session.Config is not { } config) return Reply("bow: no wake driver, see client-main.log");
        string usage = string.Create(CultureInfo.InvariantCulture,
            $"bow: <boat>|both <bow> [<along> <across> [<between>]], blocks from the leading edge, ahead positive, {BoatEmissionRule.MinCloudAlong}..{BoatEmissionRule.MaxCloudAlong}, across 0..{BoatEmissionRule.MaxCloudAcross}, between 0..{BoatEmissionRule.MaxCloudBetween}; or <boat>|both spread <times 0..{BoatEmissionRule.MaxCloudSpread}>; a boat is {BoatNames}");
        if (args[0] is not string which) return Reply(BowStatus(driver.Sources, config.Current));
        (string Name, string Code)[]? boats = BoatsNamed(which);
        if (boats == null || args[1] is not string first) return Reply(usage);
        var rules = new BoatEmissionRule[boats.Length];
        for (int i = 0; i < boats.Length; i++) rules[i] = ListedRule(config.Current, driver.Sources, boats[i].Code);
        if (first == "spread")
        {
            // Only the spread: the points stay as they are.
            if (args[2] is not string timesWord || args[3] != null || !Number(timesWord, 0, BoatEmissionRule.MaxCloudSpread, out float times)) return Reply(usage);
            foreach (BoatEmissionRule rule in rules) rule.CloudSpread = times;
        }
        else
        {
            if (!Number(first, BoatEmissionRule.MinCloudAlong, BoatEmissionRule.MaxCloudAlong, out float bow)) return Reply(usage);
            float[]? sides = null;
            int between = 0;
            if (args[2] is string alongWord)
            {
                if (args[3] is not string acrossWord || !Number(alongWord, BoatEmissionRule.MinCloudAlong, BoatEmissionRule.MaxCloudAlong, out float along)
                    || !Number(acrossWord, 0, BoatEmissionRule.MaxCloudAcross, out float across)
                    || args[4] is string betweenWord && !(int.TryParse(betweenWord, NumberStyles.None, CultureInfo.InvariantCulture, out between)
                                                         && between <= BoatEmissionRule.MaxCloudBetween))
                {
                    return Reply(usage);
                }
                sides = [along, across];
            }
            foreach (BoatEmissionRule rule in rules)
            {
                rule.CloudBow = bow;
                rule.CloudSides = sides;
                rule.CloudBetween = between;
            }
        }
        ConfigReport report = config.SetBoatEmission(rules, BoatRuleKeys.Clouds);
        return Reply(ReloadText("bow: ", report) + "\n" + BowStatus(driver.Sources, config.Current));
    }

    TextCommandResult OnSwimmer(TextCommandCallingArgs args)
    {
        if (session.Config is not { } config) return Reply("swimmer: no config, see client-main.log");
        if (args[0] is not string word) return Reply(SwimmerStatus(config.Current));
        if (!Number(word, 0, WakesConfig.MaxSwimmerOffset, out float offset))
        {
            return Reply(string.Create(CultureInfo.InvariantCulture, $"swimmer: give the blocks ahead of pos, 0..{WakesConfig.MaxSwimmerOffset}"));
        }
        ConfigReport report = config.SetValue(WakesConfig.Key(nameof(WakesConfig.SwimmerOffset)), offset);
        return Reply(ReloadText("swimmer: ", report) + "\n" + SwimmerStatus(config.Current));
    }

    static string SwimmerStatus(WakesConfig current) => string.Create(CultureInfo.InvariantCulture,
        $"swimmerOffset {current.SwimmerOffset:0.###}: a swimming player's wake starts that many blocks ahead of its pos along its motion, 0 starts it at pos as in Wakes; wading players start at pos");

    // A comparison with NaN is false, so "NaN" fails the range as well.
    static bool Number(string word, float min, float max, out float value) =>
        float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    // The cloud points each named boat goes by now, with the edges they are measured from once a boat of the type has
    // loaded.
    static string BowStatus(SourceTracker sources, WakesConfig current)
    {
        var sb = new StringBuilder("bow clouds in use, in blocks from the leading edge along the course, ahead positive:");
        foreach ((string name, string code) in NamedBoats)
        {
            sb.Append("\n  ").Append(name).Append(": ");
            if (sources.HullMatching(new AssetLocation(code)) is not { } seen)
            {
                BoatEmissionRule? listed = current.BoatEmission.FirstOrDefault(r => r.Code == code);
                sb.Append(listed == null ? $"no rule {code}, Wakes' bow point" : $"rule {code}, {SourceTracker.CloudText(listed)}")
                  .Append($"; no {name} has loaded yet, so a rule in front of it may win");
                continue;
            }
            BoatEmissionRule rule = current.EmissionFor(seen.Code);
            HullShape hull = seen.Hull;
            sb.Append(seen.Code.ToString()).Append(" by ").Append(rule == BoatEmissionRule.None ? "no rule" : "rule " + rule.Code).Append(", ")
              .Append(SourceTracker.CloudText(rule))
              .Append(string.Create(CultureInfo.InvariantCulture,
                  $"; the leading edge is the bow {hull.Bow:0.##} ahead of pos, or the stern {hull.Stern:0.##} behind it running stern first; hull width {2 * hull.HalfWidth:0.##}"));
        }
        return sb.ToString();
    }

    // Null when the word is a point; otherwise the reply, empty for the usage line.
    static string? PointOffset(string word, string name, HullShape? hull, out float offset)
    {
        offset = 0;
        switch (word)
        {
            case "pos":
                return null;
            case "bow" or "stern":
                if (hull == null) return $"emission: no {name} has loaded in this world yet, and the {word} comes from its hull; come near one or give the offset as a number";
                // Truncated toward Pos to three places, so the point lies on the hull's edge or just inside it.
                float edge = (word == "bow" ? hull.Bow : -hull.MaxZ) / hull.Length;
                offset = MathF.Truncate(edge * 1000) / 1000;
                return null;
            default:
                return Number(word, -1, 1, out offset) ? null : "";
        }
    }

    // The rule each named boat goes by now, with the point in blocks from Pos once a boat of the type has loaded.
    static string EmissionStatus(SourceTracker sources, WakesConfig current)
    {
        var sb = new StringBuilder("emission rules in use:");
        foreach ((string name, string code) in NamedBoats)
        {
            sb.Append("\n  ").Append(name).Append(": ");
            if (sources.HullMatching(new AssetLocation(code)) is not { } seen)
            {
                BoatEmissionRule? listed = current.BoatEmission.FirstOrDefault(r => r.Code == code);
                sb.Append(listed == null
                    ? $"no rule {code}, and no {name} has loaded yet"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"rule {code} offset {listed.Offset:0.###}{(listed.OffsetTo is { } to ? ".." + to.ToString("0.###", CultureInfo.InvariantCulture) : "")} strength x{listed.StrengthMultiplier:0.##}; no {name} has loaded yet, so a rule in front of it may win"));
                continue;
            }
            BoatEmissionRule rule = current.EmissionFor(seen.Code);
            HullShape hull = seen.Hull;
            sb.Append(seen.Code.ToString()).Append(" by ").Append(rule == BoatEmissionRule.None ? "no rule, pos and x1" : "rule " + rule.Code)
              .Append(": ").Append(SourceTracker.EmissionText(rule, hull))
              .Append(string.Create(CultureInfo.InvariantCulture,
                  $", strength x{rule.StrengthMultiplier:0.##}; bow edge at {hull.Bow / hull.Length:0.###} ({hull.Bow:0.00} blocks), stern edge at {-hull.MaxZ / hull.Length:0.###} ({-hull.MaxZ:0.00} blocks)"));
        }
        return sb.ToString();
    }

    TextCommandResult OnSplash(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver || session.Config is not { } config) return Reply("splash: no wake driver, see client-main.log");
        int n = (int)args[0];
        if (n < 0 || n > DebugSplash.MaxCount) return Reply($"splash: give a count from 1 to {DebugSplash.MaxCount}, or none for the point you look at");
        int strength = config.Current.SplashStrength;
        if (!driver.WaitForCore()) return Reply("splash: the wake thread is stuck in a job, see client-main.log");
        splash ??= new DebugSplash(capi);
        return Reply(n == 0
            ? splash.AtLook(driver.Core, driver.Water, strength)
            : splash.Around(driver.Core, driver.Water, strength, n));
    }

    TextCommandResult OnClouds(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver || session.Clouds is not { } pool) return Reply("clouds: no wake driver, see client-main.log");
        if (pool.Off) return Reply("clouds: off after an error, see client-main.log");
        int n = (int)args[0];
        if (n < 1 || n > MaxCloudPairs) return Reply($"clouds: give a count from 1 to {MaxCloudPairs}");
        if (capi.World.Player?.Entity is not { } me) return Reply("clouds: no player");
        if (!GhostSource.FindWater(capi.World.BlockAccessor, driver.Water, me.Pos.X, me.Pos.InternalY, me.Pos.Z, out int x, out int y, out int z))
        {
            return Reply("clouds: no still water surface the wake takes within 8 blocks of you");
        }
        long evicted = pool.Evicted, start = Stopwatch.GetTimestamp();
        int born = 0;
        Random random = capi.World.Rand;
        for (int i = 0; i < n; i++)
        {
            double a = random.NextDouble() * 2 * Math.PI;
            // From the middle of the block: the bow point lies reach - 0.3 ahead and the pair at most a quarter of the
            // width to its sides, so both stay over this water.
            born += pool.SpawnBow(x + 0.5, z + 0.5, y, Math.Cos(a), Math.Sin(a), 0.3, 1, CloudSpeed * WakeDriver.StepSeconds, local: false,
                BoatEmissionRule.None);
        }
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        return Reply($"clouds: {born} born over water block {x} {y} {z} in {ms.ToString("0.0", CultureInfo.InvariantCulture)} ms, " +
                     $"{pool.Evicted - evicted} of the oldest dropped from the full pool; {pool.Count} alive of {SplashCloudPool.Capacity}");
    }

    TextCommandResult OnGhost(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver || session.Config is not { } config) return Reply("ghost: no wake driver, see client-main.log");
        string which = (string)args[0];
        if (which == "clear") return Reply(driver.Sources.ClearGhosts());
        GhostPath? path = which switch
        {
            "line" => GhostPath.Line,
            "circle" => GhostPath.Circle,
            _ => null,
        };
        if (path == null) return Reply("ghost: line, circle or clear");
        // A width left out would quietly give a trail one pixel wide.
        if (args[1] is not string speedWord || args[2] is not string widthWord) return Reply($"ghost: {which} <speed b/s>[..<speed>] <width|boat> [n], a boat is {BoatNames}");
        string[] speeds = speedWord.Split("..");
        double from = 0, to = 0;
        // A comparison with NaN is false, so "NaN" fails the range as well.
        // "3...15" would split into 3 and .15.
        if (speeds.Length > 2 || speedWord.Contains("...") || !Speed(speeds[0], out from) || !Speed(speeds[^1], out to))
        {
            return Reply($"ghost: give a speed above 0 and up to {GhostSource.MaxSpeed} blocks a second, or two such speeds as a..b");
        }
        GhostBoat? boat = null;
        double width = 0;
        if (NamedBoats.FirstOrDefault(b => b.Name == widthWord) is { Name: not null } named)
        {
            if (driver.Sources.HullMatching(new AssetLocation(named.Code)) is not { } seen)
            {
                return Reply($"ghost: no {named.Name} has loaded since you entered this world, and the ghost takes its hull; come near one first");
            }
            boat = new GhostBoat(named.Name, seen.Code, seen.Hull);
        }
        else if (!(double.TryParse(widthWord, NumberStyles.Float, CultureInfo.InvariantCulture, out width) && width >= 0 && width <= GhostSource.MaxWidth))
        {
            return Reply($"ghost: give a width from 0 to {GhostSource.MaxWidth} blocks or a boat, {BoatNames}");
        }
        int n = (int)args[3];
        if (n < 1) return Reply("ghost: give a count above 0");
        return Reply(driver.Sources.AddGhosts(path.Value, from, to, (float)width, boat, n, config.Current));

        static bool Speed(string word, out double speed) =>
            double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out speed) && speed > 0 && speed <= GhostSource.MaxSpeed;
    }

    TextCommandResult OnMode(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver) return Reply("mode: no wake driver, see client-main.log");
        if (args[0] is string which)
        {
            CoreModes flag;
            string? on, off;
            switch (which)
            {
                case "fade":
                    (flag, on, off) = (CoreModes.AmplitudeFade, "amp", "age");
                    break;
                case "halo":
                    (flag, on, off) = (CoreModes.SyncHalo, "sync", "lag");
                    break;
                case "flood":
                    (flag, on, off) = (CoreModes.EagerNeighbours, "eager", "wait");
                    break;
                case "edge":
                    (flag, on, off) = (CoreModes.SoftEdge, "soft", "hard");
                    break;
                default:
                    return Reply("mode: fade age|amp, halo lag|sync, flood wait|eager or edge hard|soft");
            }
            string? value = args[1] as string;
            if (value != on && value != off) return Reply($"mode: {which} {off}|{on}");
            if (!driver.SetModes(value == on ? driver.Modes | flag : driver.Modes & ~flag)) return Reply("mode: the wake thread is stuck in a job, see client-main.log");
        }
        return Reply($"mode: {WakesStats.ModeNames(driver.Modes)}, until you leave the world");
    }

    // Farthest end of a fade .wakes look takes; past the reach the renderer cuts it to the reach anyway.
    const float MaxFadeBlocks = 4096;

    TextCommandResult OnLook(TextCommandCallingArgs args)
    {
        if (session.Driver is not { } driver || session.Overlay is not { } overlay) return Reply("look: no wake driver or overlay, see client-main.log");
        WakeLook look = overlay.Look;
        string? value = args[1] as string, to = args[2] as string;
        if (args[0] is string which && which != "fade" && to != null) return Reply($"look: {which} takes one value");
        switch (args[0] as string)
        {
            case null:
                break;
            case "shade":
                float shade = 0;
                if (!(value is "bare" or "grey" || value != null && Number(value, 0, 1, out shade))) return Reply("look: shade bare, grey or a number 0..1");
                if (value == "grey") shade = 1;
                if (!driver.SetWaterShade(shade)) return Reply("look: the wake thread is stuck in a job, see client-main.log");
                break;
            case "lift":
                if (value == null || !Number(value, 0, WakeLook.MaxLift, out float lift))
                {
                    return Reply(string.Create(CultureInfo.InvariantCulture, $"look: lift 0..{WakeLook.MaxLift} blocks"));
                }
                look.Lift = lift;
                break;
            case "fade":
                if (value == "auto" && to == null) look.SetFade(null, null);
                else if (value != null && to != null && Number(value, 0, MaxFadeBlocks, out float from) && Number(to, 0, MaxFadeBlocks, out float end) && end > from)
                {
                    look.SetFade(from, end);
                }
                else return Reply(string.Create(CultureInfo.InvariantCulture, $"look: fade auto or <from> <to> in blocks, 0..{MaxFadeBlocks}, to past from"));
                break;
            case "far":
                if (value == null || !Number(value, 0, WakeLook.MaxFarAlpha, out float far))
                {
                    return Reply(string.Create(CultureInfo.InvariantCulture, $"look: far 0..{WakeLook.MaxFarAlpha}, 1 leaves the opacity as it is"));
                }
                look.FarAlpha = far;
                break;
            default:
                return Reply("look: shade bare|grey|<0..1>, lift <blocks>, fade auto|<from> <to> or far <factor>");
        }
        float reach = (float)Math.Sqrt(capi.Render.DefaultFrustumCuller.ViewDistanceSq);
        return Reply(string.Create(CultureInfo.InvariantCulture,
            $"look: shade {driver.WaterShade:0.###}, {look.Describe(reach)}, until you leave the world"));
    }

    TextCommandResult OnProbe(TextCommandCallingArgs args)
    {
        if (session.Probe is not { } probe) return Reply("probe: not running, see client-main.log");
        if (args[0] is string arg && arg != "ref") return Reply("probe: no argument or 'ref'");
        return Reply(probe.Arm(asReference: args[0] is "ref"));
    }

    TextCommandResult OnStage(TextCommandCallingArgs args)
    {
        if (session.Overlay is not { } overlay) return Reply("stage: no overlay renderer, see client-main.log");
        return Reply(StageStatus(overlay));
    }

    TextCommandResult OnShadows(TextCommandCallingArgs args)
    {
        if (session.Overlay is not { } overlay) return Reply("shadows: no overlay renderer, see client-main.log");
        string arg = (string)args[0];
        if (arg is not ("on" or "off")) return Reply("shadows: on or off");
        overlay.Shadows = arg == "on";
        return Reply(StageStatus(overlay));
    }

    TextCommandResult OnCori(TextCommandCallingArgs args)
    {
        if (session.CoriaenderFix is not { } fix) return Reply("cori: CoriaenderShaders is not loaded, its water buffer needs no fix");
        if (args[0] is string arg)
        {
            if (arg is not ("on" or "off")) return Reply("cori: on or off");
            fix.Enabled = arg == "on";
        }
        return Reply($"cori: buffer fix {(fix.Enabled ? "on" : "off")}{(fix.Off ? ", off after an error, see client-main.log" : "")}; " +
                     $"loaded entities with a water mask {fix.MaskedEntities}; frames set back {fix.Restores}");
    }

    TextCommandResult OnModLight(TextCommandCallingArgs args)
    {
        if (args[0] is string arg)
        {
            if (arg is not ("on" or "off")) return Reply("modlight: on or off");
            ModLight.Enabled = arg == "on";
        }
        OverlayPrograms? p = session.Programs;
        string state = ModLight.Enabled ? "on" : "off";
        return Reply(ModLight.DarkVisionValue is { } lift
            ? string.Create(CultureInfo.InvariantCulture, $"modlight: {state}; Dark Vision minUnlitBright {lift:0.000}, programs wake/clouds {DarkVision(p?.Oit)}/{DarkVision(p?.Cloud)}")
            : $"modlight: {state}; Dark Vision not loaded, nothing to take");
    }

    static string DarkVision(IShaderProgram? prog) => OverlayPrograms.DarkVisionSet(prog) is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "-";

    string StageStatus(OverlayRenderer overlay)
    {
        OverlayPrograms? p = session.Programs;
        static string Live(IShaderProgram? prog) => prog != null ? "live" : "missing";
        static string Cloud(IShaderProgram? prog) => OverlayPrograms.CloudShadowsSet(prog)?.ToString(CultureInfo.InvariantCulture) ?? "-";
        string state = overlay.Off ? "off after an error, see client-main.log" : "on";
        long clouds = session.CloudRenderer?.Draws ?? 0;
        return string.Create(CultureInfo.InvariantCulture, $"stage: OIT {OverlayRenderer.OitOrder}; ") +
               $"overlay {state}; programs {OverlayPrograms.OitName} {Live(p?.Oit)}, {OverlayPrograms.CloudName} {Live(p?.Cloud)}; " +
               $"shadows {(overlay.Shadows ? "on" : "off")}, shadowMapQuality {capi.Settings.Int["shadowMapQuality"]}, " +
               $"Coriaender cloud shadows on wake/clouds {Cloud(p?.Oit)}/{Cloud(p?.Cloud)}; " +
               $"Dark Vision minUnlitBright wake/clouds {DarkVision(p?.Oit)}/{DarkVision(p?.Cloud)}; " +
               $"samplers {OverlayPrograms.OitName} [{OverlayPrograms.Samplers(p?.Oit, overlay.OitDraws > 0)}], " +
               $"{OverlayPrograms.CloudName} [{OverlayPrograms.Samplers(p?.Cloud, clouds > 0)}]; " +
               $"wake quads {session.Wakes?.QuadCount ?? 0}, tiles {session.Tiles?.Count ?? 0}; frames drawn: wake {overlay.OitDraws}, clouds {clouds}";
    }

    TextCommandResult OnRes(TextCommandCallingArgs args)
    {
        if (session.Tiles is not { } tiles) return Reply("res: no tiles, see client-main.log");
        string arg = (string)args[0];
        if (arg is not ("16" or "32")) return Reply("res: 16 or 32");
        tiles.SetResolution(int.Parse(arg));
        return Reply(TextureStatus());
    }

    TextCommandResult OnFilter(TextCommandCallingArgs args)
    {
        string arg = (string)args[0];
        if (arg is not ("nearest" or "mip")) return Reply("filter: nearest or mip");
        session.Atlas?.SetMipmaps(arg == "mip");
        session.Tiles?.SetMipmaps(arg == "mip");
        return Reply(TextureStatus());
    }

    TextCommandResult OnTint(TextCommandCallingArgs args)
    {
        if (session.Tiles is not { } tiles) return Reply("tint: no tiles, see client-main.log");
        string arg = (string)args[0];
        if (arg is not ("bare" or "grey")) return Reply("tint: bare or grey");
        tiles.SetTint(arg == "bare" ? S1Tint.Bare : S1Tint.Grey);
        return Reply(TextureStatus());
    }

    // The upload rate of the wake atlas is measured between two status lines in real time: a repaint uploads on pause too.
    string TextureStatus()
    {
        var sb = new StringBuilder("tex: ");
        if (session.Atlas is { } atlas)
        {
            long uploads = atlas.Uploads, now = Stopwatch.GetTimestamp();
            double seconds = (double)(now - statusTicks) / Stopwatch.Frequency;
            double rate = seconds > 0 ? (uploads - statusUploads) / seconds : 0;
            sb.Append($"wake atlas {atlas.Size}x{atlas.Size}, {atlas.Res} texels a slot, texture {atlas.TextureId} " +
                      $"(build {atlas.Builds} in this world), filter {(atlas.Mipmaps ? "mip" : "nearest")}; " +
                      $"{uploads - statusUploads} uploads in {seconds:0.0} s ({rate:0.0}/s), {uploads} in total, " +
                      $"last strip {atlas.LastRows} rows {atlas.LastRows * atlas.Size * 4 / 1024} KiB");
            statusUploads = uploads;
            statusTicks = now;
        }
        else sb.Append("no wake atlas");
        if (session.Tiles is { } tiles)
        {
            sb.Append($"\ntiles: {tiles.Count}, res {tiles.Res}, tint {tiles.Tint.ToString().ToLowerInvariant()}, ");
            sb.Append(tiles.Texture is { } tex
                ? $"texture {tex.TextureId} (build {tex.Builds} in this world), filter {(tex.Mipmaps ? "mip" : "nearest")}"
                : "no texture until tiles are laid");
        }
        return sb.ToString();
    }

    TextCommandResult OnTiles(TextCommandCallingArgs args)
    {
        if (session.Tiles is not { } tiles) return Reply("tiles: no tiles, see client-main.log");
        string arg = (string)args[0];
        int updates = tiles.Mesh?.Updates ?? 0;
        if (arg == "clear")
        {
            tiles.Clear();
            return Reply($"tiles: cleared; UpdateMesh calls {(tiles.Mesh?.Updates ?? 0) - updates}");
        }
        if (!int.TryParse(arg, out int asked) || asked < 1) return Reply("tiles: give a count above 0 or 'clear'");

        IBlockAccessor ba = capi.World.BlockAccessor;
        FastVec3d pos = capi.World.Player.Entity.Pos.XYZFast;
        int placed = tiles.Lay(ba, pos, asked);
        QuadMesh mesh = tiles.Mesh!;
        return Reply($"tiles: {placed} placed, {asked} asked, {tiles.Cells.Count} water cells in {tiles.Columns} columns " +
                     $"around {mesh.Origin.X} {mesh.Origin.Y} {mesh.Origin.Z} " +
                     $"(radius up to {StillWaterScan.Radius}); UpdateMesh calls {mesh.Updates - updates}\n" +
                     Check(mesh, ba, capi.World.SeaLevel));
    }

    // Reads the quads back from the mesh arrays and holds them against the world: the cell under each quad is still
    // water at level 7 with nothing liquid above, every vertex stands in its corner (NW, SW, SE, NE), a corner with the
    // wave bit sits at +0.875 and a raised one at +1, the raised corners are those the scan would raise now, and the
    // oceanity in bits 2-9 is the one OceanFlags gives the cell now. The rules come from StillWaterScan and OceanFlags,
    // so this proves the mesh against them; parity with LiquidTesselator was checked by reading the engine (reviews of
    // T02.02 and T06.01), and the author sees it in T03.02.
    static string Check(QuadMesh mesh, IBlockAccessor ba, int seaLevel)
    {
        ReadOnlySpan<float> xyz = mesh.Xyz;
        ReadOnlySpan<int> flags = mesh.Flags;
        Vec3d o = mesh.Origin;
        int good = 0, raisedCorners = 0, seaCorners = 0, maxOcean = 0;
        double minY = double.MaxValue, maxY = double.MinValue;
        string? firstBad = null;
        for (int q = 0; q < mesh.QuadCount; q++)
        {
            double x = 0, z = 0, low = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int v = (q * 4 + k) * 3;
                x += xyz[v];
                z += xyz[v + 2];
                low = Math.Min(low, xyz[v + 1]);
            }
            int bx = (int)Math.Floor(o.X + x / 4), bz = (int)Math.Floor(o.Z + z / 4);
            // The lowest corner is at +0.875, or at +1 when all four are raised; half a block down is inside the cell.
            int by = (int)Math.Floor(o.Y + low - 0.5);
            string? bad = null;
            Block fluid = ba.GetBlockRaw(bx, by, bz, BlockLayersAccess.Fluid);
            if (!StillWaterScan.IsStillWater7(fluid)) bad = $"fluid {fluid.Code}";
            else if (StillWaterScan.IsLiquid(ba, bx, by + 1, bz)) bad = "liquid above";
            int raised = StillWaterScan.RaisedCorners(ba, bx, by, bz);
            int ocean = OceanFlags.Corners(ba, seaLevel, bx, by, bz);
            for (int k = 0; k < 4 && bad == null; k++)
            {
                int v = q * 4 + k;
                double top = o.Y + xyz[v * 3 + 1] - by;
                bool wave = (flags[v] & 1) == 1;
                bool shouldRaise = (raised & (1 << k)) != 0;
                int oceanity = (flags[v] >> 2) & 0xFF, shouldOcean = (ocean >> (8 * k)) & 0xFF;
                double dx = o.X + xyz[v * 3] - bx, dz = o.Z + xyz[v * 3 + 2] - bz;
                if (Math.Abs(dx - QuadMesh.CornerDx[k]) > 1e-4 || Math.Abs(dz - QuadMesh.CornerDz[k]) > 1e-4)
                    bad = $"corner {k} at x+{dx:0.###} z+{dz:0.###}";
                else if (wave == shouldRaise) bad = $"corner {k} wave bit {flags[v] & 1}";
                else if (Math.Abs(top - (wave ? QuadMesh.SurfaceHeight : 1.0)) > 1e-4) bad = $"corner {k} at +{top:0.####}";
                else if (oceanity != shouldOcean) bad = $"corner {k} oceanity {oceanity}, the rule gives {shouldOcean}";
                if (!wave) raisedCorners++;
                if (oceanity > 0) seaCorners++;
                maxOcean = Math.Max(maxOcean, oceanity);
            }
            if (bad == null)
            {
                good++;
                minY = Math.Min(minY, by);
                maxY = Math.Max(maxY, by);
            }
            else firstBad ??= $"quad {q} at {bx} {by} {bz}: {bad}";
        }
        if (mesh.QuadCount == 0) return "check: no quads";
        return $"check: {good} of {mesh.QuadCount} quads over still water level 7 (block y {minY}..{maxY}, internal), " +
               $"{raisedCorners} raised corners, {seaCorners} corners with oceanity (up to {maxOcean} of 255)" +
               (firstBad != null ? $"; first mismatch {firstBad}" : "");
    }
}
#endif
