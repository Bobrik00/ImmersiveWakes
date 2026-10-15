using System;
using System.Linq;
using System.Text;
using ImmersiveWakes.Config;
using ImmersiveWakes.Debug;
using ImmersiveWakes.Sources;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ImmersiveWakes.Commands;

// Root of the client command ".wakes" with the subcommands a player has. The debug toggles the plan put here (tiles,
// stage, filter, res and the rest) live in WakesCommands.Debug.cs, which only a Debug build compiles. Help lists
// whatever is registered at the moment it runs.
sealed partial class WakesCommands
{
    readonly ICoreClientAPI capi;
    readonly ModInfo info;
    readonly WakesSession session;

    public IChatCommand Root { get; }

    public WakesCommands(ICoreClientAPI capi, ModInfo info, WakesSession session)
    {
        this.capi = capi;
        this.info = info;
        this.session = session;

        Root = capi.ChatCommands.Create("wakes")
            .WithDescription("ImmersiveWakes commands")
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(_ => Reply(HelpText()));

        Root
            .BeginSubCommand("help")
                .WithDescription("List the subcommands")
                .HandleWith(_ => Reply(HelpText()))
            .EndSubCommand()
            .BeginSubCommand("version")
                .WithDescription("Print the mod version")
                .HandleWith(_ => Reply($"ImmersiveWakes {info.Version}"))
            .EndSubCommand()
            .BeginSubCommand("reload")
                .WithDescription($"Read ModConfig/{WakesConfigFile.FileName} again and apply it; the mod also does so on its own within half a second of a change to the file. A file that cannot be read keeps the values in use")
                .HandleWith(_ => OnReload())
            .EndSubCommand()
            .BeginSubCommand("config")
                .WithDescription("Print the config in use; with a word, only the keys that contain it")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("key"))
                .HandleWith(OnConfig)
            .EndSubCommand()
            .BeginSubCommand("stats")
                .WithDescription("Steps a second, nodes, refusals at the limit, CPU time and allocations of the step since the last call; quads drawn, CPU and GPU time of the strip upload, the mesh write and the overlay passes, the last probe and the oceanity here; the splash clouds alive, born and dropped, their CPU and GPU time; 'reset' only starts a new window")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("reset"))
                .HandleWith(OnStats)
            .EndSubCommand()
            .BeginSubCommand("debug")
                .WithDescription($"Every source within {SourceTracker.DebugRadius} blocks, nearest first, with the rule it goes by and whether the last step took its trail or why not; 'all' adds the entities that are not sources; 'hull' draws the hull of every boat with its bow edge, emission points and cloud points (white posts) and prints their corners, 'hull off' hides them; 'water' prints the oceanity and wave flags of the fluid the look meets, 'water here' of the column under you with the oceanity of the cells around on the sea row")
                .WithArgs(capi.ChatCommands.Parsers.OptionalWord("all|hull|water"), capi.ChatCommands.Parsers.OptionalWord("off|here"))
                .HandleWith(OnDebug)
            .EndSubCommand();
        RegisterDebug();
    }

    // WakesCommands.Debug.cs; a Release build has no body for it and drops the call.
    partial void RegisterDebug();

    TextCommandResult OnReload()
    {
        if (session.Config is not { } config) return Reply("reload: no config, see client-main.log");
        return Reply(ReloadText("reload: ", config.Reload()));
    }

    string ReloadText(string head, ConfigReport report)
    {
        var sb = new StringBuilder(head).Append(report.Summary);
        if (report.Taken)
        {
            sb.Append(report.Warnings.Count switch
            {
                0 => ", every value in range",
                1 => ", 1 warning:",
                int n => $", {n} warnings:",
            });
        }
        foreach (string w in report.Warnings) sb.Append("\n  ").Append(w);
        // The driver applies a config it took before Reload returns.
        if (report.Taken && session.Driver != null) sb.Append("\n").Append(session.Driver.LastApplied);
        return sb.ToString();
    }

    TextCommandResult OnDebug(TextCommandCallingArgs args)
    {
        if (args[0] is "water")
        {
            if (args[1] is string where && where != "here") return Reply("water: no argument or 'here'");
            return Reply(args[1] is "here" ? WaterDebug.Here(capi) : WaterDebug.AtLook(capi));
        }
        if (session.Driver is not { } driver || session.Config is not { } config) return Reply("debug: no wake driver, see client-main.log");
        if (args[0] is "hull")
        {
            if (session.Hulls is not { } hulls) return Reply("hull: no frame renderer, see client-main.log");
            if (args[1] is string off && off != "off") return Reply("hull: no argument or 'off'");
            hulls.Show(args[1] is not "off");
            return Reply(driver.Sources.HullReport(config.Current, hulls.Off ? null : hulls.On));
        }
        if (args[0] is string arg && arg != "all" || args[1] != null) return Reply("debug: no argument, 'all', 'hull' or 'water'");
        return Reply(driver.Sources.Report(config.Current, args[0] is "all"));
    }

    TextCommandResult OnConfig(TextCommandCallingArgs args)
    {
        if (session.Config is not { } config) return Reply("config: no config, see client-main.log");
        string? filter = args[0] as string;
        var sb = new StringBuilder($"config: {WakesConfigFile.FilePath}, values from {config.Origin}");
        int shown = 0;
        foreach ((string key, string value) in config.Values())
        {
            if (filter != null && !key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append('\n').Append(key).Append(' ').Append(value);
            // Fork 20 asks for 32 to be marked expensive where the player sees the value.
            if (key == WakesConfig.Key(nameof(WakesConfig.WakeResolution)) && value == "32")
            {
                sb.Append(" (expensive: the wave step takes 3 to 4 times the CPU of 16)");
            }
            shown++;
        }
        if (shown == 0) sb.Append($"\nno key contains '{filter}'");
        return Reply(sb.ToString());
    }

    TextCommandResult OnStats(TextCommandCallingArgs args)
    {
        if (session.Stats is not { } stats) return Reply("stats: not running, see client-main.log");
        if (args[0] is string arg)
        {
            if (arg != "reset") return Reply("stats: no argument or 'reset'");
            stats.Reset();
            return Reply("stats: new window");
        }
        return Reply(stats.Line());
    }

    string HelpText()
    {
        var sb = new StringBuilder($"ImmersiveWakes {info.Version}");
        // An alias puts the same subcommand under a second key.
        foreach (IChatCommand sub in Root.Subcommands.Distinct())
        {
            sb.Append('\n').Append(Root.CommandPrefix).Append(Root.Name).Append(' ').Append(sub.Name)
                .Append(" - ").Append(sub.Description);
        }
        return sb.ToString();
    }

    // The chat prints a non-null StatusMessage of a client command through Lang.Get, unescaped
    // (ChatCommandApi.Execute), and Success() carries "", an empty chat line. So the text goes through Say
    // and the result carries no message at all.
    public TextCommandResult Reply(string text)
    {
        Say(text);
        return new TextCommandResult { Status = EnumCommandStatus.Success };
    }

    // Also into client-main.log, so the author's checks can be read there without screenshots of the chat; the autorun
    // leaves these lines out of its mod notices, its report has the chat already.
    public void Say(string text)
    {
        session.Mod.Logger.Notification("{0}", "reply " + text);
        capi.ShowChatMessage(EscapeVtml(text));
    }

    // The chat parses VTML: '<' and '>' must be escaped. '&' must not be, VtmlParser decodes only
    // &lt; &gt; &nbsp; and would print "&amp;" literally.
    static string EscapeVtml(string s) => s.Replace("<", "&lt;").Replace(">", "&gt;");
}
