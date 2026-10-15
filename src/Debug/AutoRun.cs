#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmersiveWakes.Config;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using IAttribute = Vintagestory.API.Datastructures.IAttribute;
using VsJsonObject = Vintagestory.API.Datastructures.JsonObject;

namespace ImmersiveWakes.Debug;

// Runs a scenario for tools\IWk-autorun.ps1, which can neither type into the chat nor click the menu. The script
// names the scenario file in IWK_AUTORUN and collects the IWK-REPORT lines from client-main.log; IWK-REPORT-END
// tells it the scenario is over. The /stop sent after it saves the world and stops the single player server, so the
// script can close the window without cutting a save short (the window close alone does not wait for the server).
sealed class AutoRun : IDisposable
{
    const string EnvVar = "IWK_AUTORUN";
    // A /stop 0-2 s after LevelFinalize crashed the client in both runs that tried it (TextureAtlasManager.RegenMipMaps
    // from a main thread task after the disconnect); sent 8 s after it, five runs out of five closed cleanly.
    const long MinUptimeBeforeStopMs = 10000;
    const long ReadyTimeoutMs = 60000;
    static readonly string[] Kinds = ["chat", "setting", "pause", "wait", "probe", "modconfig", "configlib", "look"];
    static readonly string[] Probes = ["shadowmaps", "gc", "renderdoc", "rivers"];
    // Degrees; the engine's own clamp of the pitch is about 89.14 (Look).
    const double MaxLookDown = 89;

    enum Phase { Loading, Settling, Running, Ending, Done }

    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly WakesSession session;
    readonly string name;
    readonly List<JsonElement> steps = new();
    readonly string? loadError;
    readonly Stopwatch clock = new();
    Phase phase = Phase.Loading;
    int next;
    long resumeAtMs;
    // configlib:immersivewakes:config-saved events seen since the last configlib step saved.
    int configSaved;

    AutoRun(WakesSession session, string path)
    {
        capi = session.Capi;
        log = session.Mod.Logger;
        this.session = session;
        name = Path.GetFileNameWithoutExtension(path);
        try
        {
            Load(path);
        }
        catch (Exception e)
        {
            // Reported once the player is in: the run still has to end with IWK-REPORT-END and /stop.
            loadError = e.Message;
        }
        capi.Event.LevelFinalize += OnLevelFinalize;
    }

    public static AutoRun? TryCreate(WakesSession session)
    {
        string? path = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrEmpty(path) ? null : new AutoRun(session, path);
    }

    void Load(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        int n = 0;
        foreach (JsonElement step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            n++;
            try
            {
                Validate(step);
            }
            catch (Exception e)
            {
                throw new FormatException($"step {n} {step.GetRawText()}: {e.Message}");
            }
            steps.Add(step.Clone());
        }
    }

    static void Validate(JsonElement step)
    {
        JsonElement arg = Arg(step, out string kind);
        (bool ok, string what) = kind switch
        {
            "pause" => (arg.ValueKind is JsonValueKind.True or JsonValueKind.False, "true or false"),
            "wait" => (arg.ValueKind == JsonValueKind.Number, "number of seconds"),
            "modconfig" => (arg.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Null, "string, object or null"),
            "configlib" => (arg.ValueKind == JsonValueKind.Object || arg.ValueKind == JsonValueKind.String && arg.GetString() == "window", "object or \"window\""),
            "look" => (arg.ValueKind == JsonValueKind.Array && arg.GetArrayLength() == 2 &&
                       arg.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number) && Math.Abs(arg[1].GetDouble()) <= MaxLookDown,
                       $"[yaw, down] in degrees, down within +-{MaxLookDown}"),
            _ => (arg.ValueKind == JsonValueKind.String, "string"),
        };
        if (!ok) throw new FormatException($"{kind} takes a {what}");
        if (kind == "chat" && !(arg.GetString()!.StartsWith('.') || arg.GetString()!.StartsWith('/')))
        {
            throw new FormatException("a chat line starts with . (client command) or / (server command)");
        }
        if (kind == "setting" && !step.TryGetProperty("value", out _)) throw new FormatException("setting needs a value");
        if (kind == "probe" && !Probes.Contains(arg.GetString())) throw new FormatException($"probes: {string.Join(", ", Probes)}");
    }

    // A step is an object with exactly one of the step kinds; a setting step carries "value" besides.
    static JsonElement Arg(JsonElement step, out string kind)
    {
        string[] found = step.EnumerateObject().Select(p => p.Name).Where(Kinds.Contains).ToArray();
        if (found.Length != 1) throw new FormatException($"a step holds exactly one of {string.Join(", ", Kinds)}");
        kind = found[0];
        return step.GetProperty(kind);
    }

    void OnLevelFinalize()
    {
        capi.Event.LevelFinalize -= OnLevelFinalize;
        Report("scenario", name);
        Report("build", WakesStats.BuildKind());
        Report("settings", SettingsSnapshot());
        capi.Event.ChatMessage += OnChat;
        capi.Event.RegisterEventBusListener(OnConfigSaved, 0.5, ConfigSavedEvent);
        clock.Start();
        phase = Phase.Settling;
        // Not Pump() right here: LevelFinalize handlers registered later and AmbientManager.LateInit still run after us.
        capi.Event.EnqueueMainThreadTask(Pump, "immersivewakes-autorun");
    }

    // Main thread tasks run once a frame after the render stages and keep running while the game is paused, where
    // tick listeners and RegisterCallback stand still. The engine does not catch exceptions here.
    void Pump()
    {
        if (phase == Phase.Done) return;
        try
        {
            switch (phase)
            {
                case Phase.Settling: Settle(); break;
                case Phase.Running: if (clock.ElapsedMilliseconds >= resumeAtMs) Advance(); break;
                case Phase.Ending: TryStop(); break;
            }
        }
        catch (Exception e)
        {
            Report("error", $"step {next}: {e}");
            End();
        }
        if (phase != Phase.Done) capi.Event.EnqueueMainThreadTask(Pump, "immersivewakes-autorun");
    }

    // A player who never picked a character gets the character dialog on every join. It pauses the game, which
    // suspends the single player server, and holds back the player-ready packet until it closes. Closing it sends
    // DidSelect = false: the server stores nothing and the dialog comes back on the next join.
    void Settle()
    {
        GuiDialog? dialog = capi.Gui.OpenedGuis.FirstOrDefault(d => d is GuiDialogCreateCharacter);
        if (dialog != null)
        {
            dialog.TryClose();
            Report("character", "dialog closed without a selection, the player has no character in this world");
        }
        if (!capi.PlayerReadyFired)
        {
            if (clock.ElapsedMilliseconds > ReadyTimeoutMs)
            {
                Report("error", $"the player was not ready {ReadyTimeoutMs / 1000} s after LevelFinalize");
                End();
            }
            return;
        }
        Report("ready", $"{clock.ElapsedMilliseconds} ms after LevelFinalize, paused={Format(capi.IsGamePaused)}");
        if (loadError != null)
        {
            Report("error", "scenario not loaded: " + loadError);
            End();
            return;
        }
        phase = Phase.Running;
    }

    void Advance()
    {
        while (next < steps.Count)
        {
            JsonElement step = steps[next++];
            Report("step", $"{next} paused={Format(capi.IsGamePaused)} {step.GetRawText()}");
            if (Run(step)) return;
        }
        End();
    }

    // True when the step is a wait and the scenario continues on a later frame.
    bool Run(JsonElement step)
    {
        JsonElement arg = Arg(step, out string kind);
        switch (kind)
        {
            case "chat":
                // A client command runs right here. A server command leaves as a packet and its reply comes back
                // within the second; a paused single player server answers only after the pause ends.
                string line = arg.GetString()!;
                if (line.StartsWith('.')) capi.TriggerChatMessage(line);
                else capi.SendChatMessage(line);
                return false;
            case "setting":
                Report("setting", Set(arg.GetString()!, step.GetProperty("value")));
                return false;
            case "pause":
                capi.PauseGame(arg.GetBoolean());
                return false;
            case "wait":
                resumeAtMs = clock.ElapsedMilliseconds + (long)(arg.GetDouble() * 1000);
                return true;
            case "probe" when arg.GetString() == "gc":
                Report("gc", CollectGarbage());
                return false;
            case "probe" when arg.GetString() == "rivers":
                Report("rivers", Rivers());
                return false;
            case "probe" when arg.GetString() == "renderdoc":
                // The capture takes a frame still to come; ten frames of markers cover it.
                session.Probe?.MarkFrames(10);
                Report("renderdoc", RenderDocCapture.Trigger());
                return false;
            case "probe":
                Report("shadowmaps", ShadowMaps());
                return false;
            case "modconfig":
                Report("modconfig", EditModConfig(arg));
                return false;
            case "configlib":
                Report("configlib", ConfigLib(arg));
                return false;
            case "look":
                Report("look", Look(arg[0].GetDouble(), arg[1].GetDouble()));
                return false;
        }
        return false;
    }

    // Edits ModConfig/immersivewakes.json as a player would before .wakes reload: a string is the file's new text,
    // broken JSON included; an object sets its keys over the file's, in a new file when there is none; null deletes
    // the file. The keys are taken as they are written, so a key in another case than the file's becomes a second one.
    static string EditModConfig(JsonElement arg)
    {
        string path = WakesConfigFile.FilePath;
        switch (arg.ValueKind)
        {
            case JsonValueKind.Null:
                File.Delete(path);
                return $"deleted {path}";
            case JsonValueKind.String:
                File.WriteAllText(path, arg.GetString());
                return $"wrote {arg.GetString()!.Length} chars to {path}";
        }
        // A missing file starts empty, as a player would create one. A file that is not an object, broken JSON among
        // them, cannot take keys, and the step fails naming it.
        var file = new JsonObject();
        if (File.Exists(path))
        {
            var read = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(path), documentOptions: read);
            }
            catch (JsonException e)
            {
                throw new FormatException($"modconfig cannot set keys in {path}: {e.Message}");
            }
            file = node as JsonObject
                   ?? throw new FormatException($"modconfig cannot set keys in {path}: it holds {node?.GetValueKind().ToString() ?? "null"}, not an object");
        }
        foreach (JsonProperty p in arg.EnumerateObject()) file[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        File.WriteAllText(path, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return $"set {string.Join(", ", arg.EnumerateObject().Select(p => $"{p.Name} {p.Value.GetRawText()}"))} in {path}";
    }

    const string ConfigSavedEvent = "configlib:immersivewakes:config-saved";

    void OnConfigSaved(string eventName, ref EnumHandling handling, IAttribute data) => configSaved++;

    // ConfigLib by reflection, as its window acts on the mod's file: an object sets the settings of the patch by their
    // codes, as the window's widgets set them, and saves the config as its Save button does (Config.WriteToFile);
    // "window" opens or closes the window as the P key and the button of the pause menu do
    // (GuiManager.ShowConfigWindowStatic). The mod sees the save only through its poll of the file.
    string ConfigLib(JsonElement arg)
    {
        ModSystem system = capi.ModLoader.GetModSystem("ConfigLib.ConfigLibModSystem")
                           ?? throw new InvalidOperationException("ConfigLib is not loaded; run with -ExtraModPaths");
        if (arg.ValueKind == JsonValueKind.String)
        {
            Type manager = system.GetType().Assembly.GetType("ConfigLib.GuiManager", throwOnError: true)!;
            manager.GetMethod("ShowConfigWindowStatic", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            // The static call returns true even when no manager was built (VSImGui missing), so the state is read.
            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            object? instance = manager.GetField("_instance", Private)!.GetValue(null);
            if (instance == null) return "no window: ConfigLib built no GuiManager";
            return (bool)manager.GetField("_showConfig", Private)!.GetValue(instance)! ? "window open" : "window closed";
        }
        object config = system.GetType().GetMethod("GetConfig", [typeof(string)])!.Invoke(system, ["immersivewakes"])
                        ?? throw new InvalidOperationException("ConfigLib holds no config for immersivewakes");
        MethodInfo getSetting = config.GetType().GetMethod("GetSetting", [typeof(string)])!;
        var set = new List<string>();
        foreach (JsonProperty p in arg.EnumerateObject())
        {
            object setting = getSetting.Invoke(config, [p.Name]) ?? throw new ArgumentException($"ConfigLib has no setting {p.Name}");
            setting.GetType().GetProperty("Value")!.SetValue(setting, new VsJsonObject(JToken.Parse(p.Value.GetRawText())));
            set.Add($"{p.Name} {p.Value.GetRawText()}");
        }
        configSaved = 0;
        config.GetType().GetMethod("WriteToFile", Type.EmptyTypes)!.Invoke(config, null);
        return $"set {(set.Count == 0 ? "nothing" : string.Join(", ", set))}; saved; {ConfigSavedEvent} events: {configSaved}; " +
               KeepsText(File.ReadAllText(WakesConfigFile.FilePath));
    }

    // Whether the mod's next read leaves the text ConfigLib wrote alone or writes it back in its own form.
    static string KeepsText(string text)
    {
        WakesConfig? read = Newtonsoft.Json.JsonConvert.DeserializeObject<WakesConfig>(text);
        if (read == null) return "the file holds no object";
        var warnings = new List<string>();
        read.Sanitize(warnings);
        bool same = Newtonsoft.Json.JsonConvert.SerializeObject(read, Newtonsoft.Json.Formatting.Indented) == text;
        return $"the mod {(same ? "keeps the text as written" : "writes the file back in its own form")}, {warnings.Count} warnings";
    }

    // Goes through capi.Settings like a mod would. The type comes from the table that already holds the key: a new
    // key would be created silently in the wrong table.
    string Set(string key, JsonElement value)
    {
        ISettings s = capi.Settings;
        switch (value.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False when s.Bool.Exists(key):
                return Change(s.Bool, key, value.GetBoolean());
            case JsonValueKind.Number when s.Int.Exists(key):
                return Change(s.Int, key, value.GetInt32());
            case JsonValueKind.Number when s.Float.Exists(key):
                return Change(s.Float, key, value.GetSingle());
            case JsonValueKind.String when s.String.Exists(key):
                return Change(s.String, key, value.GetString()!);
        }
        throw new ArgumentException($"no {value.ValueKind} setting named {key}");
    }

    static string Change<T>(ISettingsClass<T> table, string key, T value)
    {
        T old = table[key];
        table[key] = value;
        return $"{key} {Format(old)} -> {Format(table[key])}";
    }

    // A full collection with the finalizers run: LoadedTexture and MeshRef report a missed Dispose from their
    // finalizers ("is leaking memory" in client-debug.log), which otherwise wait for a collection nobody can time.
    static string CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return $"collections gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}";
    }

    // capi.Settings fires the setting's watchers only; the graphics menu also rebuilds the frame buffers and reloads
    // the shaders after a shadow change (GuiCompositeSettings.onShadowsChanged). The size is max(4, q + 2) * 1024, so
    // it tells the two apart only around a quality of 3 and up.
    string ShadowMaps()
    {
        List<FrameBufferRef> fbs = capi.Render.FrameBuffers;
        string Size(EnumFrameBuffer e) => (int)e < fbs.Count && fbs[(int)e] is { } fb ? $"{fb.Width}x{fb.Height}" : "none";
        return $"far={Size(EnumFrameBuffer.ShadowmapFar)} near={Size(EnumFrameBuffer.ShadowmapNear)} " +
               $"shadowMapQuality={capi.Settings.Int["shadowMapQuality"]}";
    }

    // Turns the camera for a capture: yaw as EntityPos.Yaw takes it, down in degrees below the horizon. The engine keeps
    // the pitch at pi for a level look and reads it back from EntityPlayer.Pos every frame, so it is set there; the yaw
    // also goes into MouseYaw, which the engine keeps apart. ClientMain clamps the pitch to 1.586..4.697 only while the
    // window has the focus and the mouse, which an autorun window has not, so Validate holds down within MaxLookDown.
    // The reply gives the look the engine had kept since the last step, so a second look shows whether it held (a seat
    // with a fixed angle sets it every frame), then the eye and the new view vector.
    string Look(double yawDeg, double downDeg)
    {
        EntityPlayer player = capi.World.Player.Entity;
        float wasYaw = player.Pos.Yaw, wasPitch = player.Pos.Pitch;
        float yaw = (float)(yawDeg * GameMath.DEG2RAD);
        float pitch = (float)(Math.PI + downDeg * GameMath.DEG2RAD);
        player.Pos.Yaw = yaw;
        player.Pos.Pitch = pitch;
        capi.Input.MouseYaw = yaw;
        capi.Input.MousePitch = pitch;
        Vec3d eye = player.Pos.XYZ.Add(player.LocalEyePos);
        Vec3f view = player.Pos.GetViewVector();
        return string.Create(CultureInfo.InvariantCulture,
            $"was yaw {wasYaw:F3} pitch {wasPitch:F3}; yaw {yaw:F3} pitch {pitch:F3} eye {eye.X:F2} {eye.Y:F3} {eye.Z:F2} view {view.X:F3} {view.Y:F3} {view.Z:F3}");
    }

    // Rivers 5.0.3 answers /riverdebug rivers with the river segments of the player's region, but only into its debug
    // map layer, and tells the chat their number alone (RiverDebugCommand.cs, RiversMod.OnRiverDebugMapMessage). The
    // probe reads them back from the layer and lists the nearest ones in the coordinates /tp takes, from the spawn.
    string Rivers()
    {
        MapLayer? layer = capi.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers
            .FirstOrDefault(l => l.GetType().FullName == "Rivers.RiverDebugMapLayer");
        if (layer == null) return "no Rivers debug map layer; run with -Stack rivers";
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var segments = new List<(double X1, double Z1, double X2, double Z2, float Width)>();
        foreach (string field in new[] { "riverSegments", "redRiverSegments" })
        {
            foreach (object seg in (System.Collections.IEnumerable)layer.GetType().GetField(field, Private)!.GetValue(layer)!)
            {
                Type t = seg.GetType();
                double D(string n) => (double)t.GetField(n)!.GetValue(seg)!;
                segments.Add((D("StartX"), D("StartZ"), D("EndX"), D("EndZ"), (float)t.GetField("Width")!.GetValue(seg)!));
            }
        }
        if (segments.Count == 0) return "no river segments; send /riverdebug rivers first";
        EntityPos me = capi.World.Player.Entity.Pos;
        EntityPos spawn = capi.World.DefaultSpawnPosition;
        string List(IEnumerable<(double X1, double Z1, double X2, double Z2, float Width)> list) => string.Join("; ", list
            .Select(g => (g, Dist: SegmentDistance(me.X, me.Z, g.X1, g.Z1, g.X2, g.Z2))).OrderBy(p => p.Dist).Take(10)
            .Select(p => FormattableString.Invariant(
                $"{p.Dist:F0} away width {p.g.Width:F0} ({p.g.X1 - spawn.X:F0} {p.g.Z1 - spawn.Z:F0})-({p.g.X2 - spawn.X:F0} {p.g.Z2 - spawn.Z:F0})")));
        return FormattableString.Invariant($"{segments.Count} segments, player at ({me.X - spawn.X:F0} {me.Y:F0} {me.Z - spawn.Z:F0}); ") +
               $"nearest: {List(segments)} | nearest of width 24 and up: {List(segments.Where(g => g.Width >= 24))}";
    }

    static double SegmentDistance(double px, double pz, double x1, double z1, double x2, double z2)
    {
        double dx = x2 - x1, dz = z2 - z1, len2 = dx * dx + dz * dz;
        double t = len2 > 0 ? Math.Clamp(((px - x1) * dx + (pz - z1) * dz) / len2, 0, 1) : 0;
        double ex = x1 + t * dx - px, ez = z1 + t * dz - pz;
        return Math.Sqrt(ex * ex + ez * ez);
    }

    // The keys a graphics preset sets (GuiCompositeSettings.onPresetChanged), to confirm what the script wrote.
    string SettingsSnapshot()
    {
        ISettings s = capi.Settings;
        string[] ints = ["graphicsPresetId", "viewDistance", "ssaoQuality", "godRays", "shadowMapQuality", "particleLevel", "maxDynamicLights"];
        string[] bools = ["smoothShadows", "fxaa", "wavingStuff", "liquidFoamAndShinyEffect", "bloom", "pauseGameOnLostFocus"];
        string[] floats = ["ssaa", "lodBiasFar"];
        return string.Join(" ", ints.Select(k => $"{k}={s.Int[k]}")
            .Concat(bools.Select(k => $"{k}={Format(s.Bool[k])}"))
            .Concat(floats.Select(k => $"{k}={Format(s.Float[k])}")));
    }

    void OnChat(int groupId, string message, EnumChatType chatType, string data) => Report("chat", message);

    void End()
    {
        if (phase >= Phase.Ending) return;
        phase = Phase.Ending;
        log.Notification("{0}", "IWK-REPORT-END");
    }

    // Chat replies that arrive meanwhile still go into the report, after the END line.
    void TryStop()
    {
        // The character dialog or a step may have paused the game; a paused single player server is suspended and
        // would never run the /stop.
        if (capi.IsGamePaused)
        {
            capi.PauseGame(false);
            return;
        }
        if (clock.ElapsedMilliseconds < MinUptimeBeforeStopMs) return;
        phase = Phase.Done;
        capi.Event.ChatMessage -= OnChat;
        // On a dedicated server or a world opened to LAN, /stop would throw the other players out; there the script
        // closes the window.
        if (capi.IsSinglePlayer && !capi.OpenedToLan) capi.SendChatMessage("/stop");
    }

    // One log line per report: a line break would leave the rest without the IWK-REPORT prefix the script looks for.
    void Report(string key, string value) =>
        log.Notification("{0}", $"IWK-REPORT {key} {value.Replace("\r", "").Replace("\n", "\\n")}");

    static string Format<T>(T value) => value switch
    {
        bool b => b ? "true" : "false",
        float f => f.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // The world closing before the end (kick, crash of the world, a scenario's own /stop) still ends the report, or
    // the script would wait for its timeout with the client on the disconnected screen.
    public void Dispose()
    {
        if (phase < Phase.Ending)
        {
            Report("error", $"the world closed in phase {phase}, before the scenario ended");
            End();
        }
        phase = Phase.Done;
        capi.Event.LevelFinalize -= OnLevelFinalize;
        capi.Event.ChatMessage -= OnChat;
        // Throws off the main thread (ClientEventAPI checks it), where a SIGINT disposes the session; the rest of the
        // session's Dispose must still run.
        try
        {
            capi.Event.UnregisterEventBusListener(OnConfigSaved);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
#endif
