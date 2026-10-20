using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using FragLab.Core.Menus;
using FragLab.Modes.Practice;
using FragLab.Modes.Retake;
using FragLab.Modes.Scrim;
using FragLab.Skins;

namespace FragLab;

public sealed class FragLabPlugin : BasePlugin, IPluginConfig<FragLabConfig>
{
    public override string ModuleName => "FragLab Plugin";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleDescription => "FragLab: scrims, retakes, practice and skins in one plugin";

    public FragLabConfig Config { get; set; } = new();

    public ModeManager Modes { get; } = new();

    public MenuService Menus { get; } = new();

    private readonly List<HelpEntry> _globalHelp = new();

    /// <summary>Game-mode commands by name; one css_ command can be shared by several modes.</summary>
    public Dictionary<string, List<(GameMode Mode, Action<CCSPlayerController?, CommandInfo> Handler, bool AdminOnly)>> ModeCommands { get; } = new();

    public SkinsFeature Skins { get; private set; } = null!;

    public void OnConfigParsed(FragLabConfig config)
    {
        // First start after the rename: take over the settings of the old UltimatePlugin.
        var imported = UltimateMigration.ImportConfig(Logger);
        Config = imported ?? config;
        Chat.Prefix = Config.ChatPrefix;
        MigrateConfig();
        if (imported != null)
            SaveConfig();
    }

    /// <summary>Writes the current config (including runtime changes) back to its JSON file.</summary>
    public void SaveConfig()
    {
        try
        {
            Config.Update();
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not save config: {Message}", ex.Message);
        }
    }

    /// <summary>Moves configs created by older versions to the new defaults that changed.</summary>
    private void MigrateConfig()
    {
        if (Config.Version >= FragLabConfig.CurrentVersion)
            return;

        if (Config.Version < 9)
        {
            foreach (var perk in new[] { "Glasses", "Clone" })
                if (!Config.FunRetake.Perks.Contains(perk))
                    Config.FunRetake.Perks.Add(perk);
        }

        if (Config.Version < 8 && !Config.FunRetake.Perks.Contains("SuperZeus"))
            Config.FunRetake.Perks.Add("SuperZeus");

        if (Config.Version < 7)
        {
            // No Recoil was removed; Creeper, Pyromaniac, Bulletproof, Phoenix, Double Perk and map events were added.
            Config.FunRetake.Perks = new FunRetakeConfig().Perks;
        }

        if (Config.Version < 6)
        {
            // Aimbot became Golden Shot (x10 damage), and Bhop, Glass Cannon and Lucky Charm were added.
            Config.FunRetake.AimbotDamageMultiplier = 10f;
            Config.FunRetake.Perks = new FunRetakeConfig().Perks;
        }

        if (Config.Version < 5)
        {
            // Fun retake got rarities and new perks (Zeus and Grenadier were removed).
            Config.FunRetake.Perks = new FunRetakeConfig().Perks;
        }

        if (Config.Version < 4)
        {
            // The AWP became disabled in retakes by default.
            if (!Config.Retake.DisabledWeapons.Contains("weapon_awp"))
                Config.Retake.DisabledWeapons.Add("weapon_awp");
        }

        if (Config.Version < 3)
        {
            // Skins became admin-only by default.
            if (string.IsNullOrEmpty(Config.Skins.RequiredFlag))
                Config.Skins.RequiredFlag = Config.AdminFlag;
        }

        if (Config.Version < 2)
        {
            Config.Retake.PistolRounds = 5;
            Config.Retake.TGrenades.Remove("weapon_smokegrenade");
            Config.Scrim.KnifeDecisionSeconds = Math.Max(Config.Scrim.KnifeDecisionSeconds, 120);
        }

        Config.Version = FragLabConfig.CurrentVersion;
        SaveConfig();
    }

    public override void Load(bool hotReload)
    {
        // Before anything reads saved files: bring over data saved under the old plugin name.
        UltimateMigration.CopyData(ModuleDirectory, Logger);

        Menus.FreezePlayers = Config.FreezeWhileInMenu;
        Menus.Register(this);

        Skins = new SkinsFeature(this);
        Skins.Load();

        Modes.Register(new ScrimMode(this));
        Modes.Register(new RetakeMode(this));
        Modes.Register(new RetakeMode(this, funPerks: true));
        Modes.Register(new PracticeMode(this));

        foreach (var mode in Modes.All)
            mode.Register();

        RegisterCoreCommands();
        new MapCommands(this).Register();
        new DamageReport(this).Register();
        RegisterEvents();

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            Modes.Dispatch(m => m.OnMapStart(mapName));
            // Give the map a moment to load before executing configs.
            AddTimer(1.0f, () => Modes.Dispatch(m => m.Activate()), TimerFlags.STOP_ON_MAPCHANGE);
        });

        if (hotReload)
            Modes.Switch(Config.DefaultMode);
        else
            Modes.SetInitial(Config.DefaultMode);
    }

    public override void Unload(bool hotReload)
    {
        Menus.CloseAll();
        Skins.Unload();
        Modes.Disable();
        foreach (var mode in Modes.All)
            mode.Unregister();
    }

    private void RegisterCoreCommands()
    {
        AddCommand("css_mode", "Show or change the active game mode", OnModeCommand);
        AddCommand("css_help", "List available commands", OnHelpCommand);
        AddCommand("css_status", "Show the current mode status", (player, _) =>
        {
            if (Modes.Active == null)
                Chat.To(player, "No mode is active.");
            else
                Modes.Active.PrintStatus(player);
        });

        AddHelp("General", ["help"], "This menu: all commands for the current mode");
        AddHelp("General", ["status"], "Status of the current mode");
        AddHelp("General", ["mode"], "Show the current game mode");
        AddHelp("General", ["mode"], "Change game mode: !mode <scrim|retake|funretake|prac|none>", adminOnly: true);
    }

    // Pending "!mode x" confirmations while a scrim runs: player slot (-1 = console) -> (mode, when).
    private readonly Dictionary<int, (string Mode, float At)> _pendingModeChange = new();

    /// <summary>The first !mode during a scrim only asks; the same command again within 15 seconds confirms.</summary>
    private bool ConfirmModeChange(CCSPlayerController? player, string mode)
    {
        var key = player?.Slot ?? -1;
        if (_pendingModeChange.Remove(key, out var pending) && pending.Mode == mode && Server.CurrentTime - pending.At <= 15f)
            return true;

        _pendingModeChange[key] = (mode, Server.CurrentTime);
        Chat.To(player, $"{{red}}A scrim is running.{{default}} Switching to {{green}}{mode}{{default}} ends it. " +
                        $"Type {{green}}!mode {mode}{{default}} again within 15 seconds to confirm.");
        return false;
    }

    private void OnModeCommand(CCSPlayerController? player, CommandInfo info)
    {
        var available = string.Join(", ", Modes.All.Select(m => m.Name).Append("none"));

        if (info.ArgCount < 2)
        {
            Chat.To(player, $"Current mode: {{green}}{Modes.Active?.Name ?? "none"}{{default}}. Available: {available}");
            return;
        }

        if (!Players.IsAdmin(player, Config.AdminFlag))
        {
            Chat.To(player, "{red}You don't have permission to change the mode.");
            return;
        }

        var name = info.GetArg(1).ToLowerInvariant();
        if (name != "none" && Modes.Find(name) == null)
        {
            Chat.To(player, $"{{red}}Unknown mode '{name}'.{{default}} Available: {available}");
            return;
        }

        if (Modes.Active != null && Modes.Find(name) == Modes.Active)
        {
            Chat.To(player, $"{{green}}{name}{{default}} is already active.");
            return;
        }

        // Leaving a scrim by accident would end the match, so it has to be confirmed.
        if (Modes.Active is ScrimMode && !ConfirmModeChange(player, name))
            return;

        if (name == "none")
        {
            Modes.Disable();
            Chat.All("All game modes disabled.");
            return;
        }

        if (!Modes.Switch(name))
        {
            Chat.To(player, $"{{red}}Unknown mode '{name}'.{{default}} Available: {available}");
            return;
        }

        Chat.All($"Game mode changed to {{green}}{Modes.Active!.Name}{{default}}.");
    }

    /// <summary>Adds a command that isn't tied to a game mode (general, skins) to !help.</summary>
    public void AddHelp(string section, string[] names, string description, bool adminOnly = false) =>
        _globalHelp.Add(new HelpEntry(section, names, description, adminOnly));

    /// <summary>
    /// Opens a menu with every command available right now, grouped by section. Selecting a command runs it;
    /// commands that need arguments show their usage instead. The full list is also printed to the console.
    /// </summary>
    private void OnHelpCommand(CCSPlayerController? player, CommandInfo info)
    {
        var isAdmin = Players.IsAdmin(player, Config.AdminFlag);
        var modeEntries = Modes.Active?.CommandHelp ?? [];

        var sections = new List<(string Title, List<HelpEntry> Entries)>();
        if (Modes.Active != null)
        {
            sections.Add((Modes.Active.Title, modeEntries.Where(e => !e.AdminOnly).ToList()));
            if (isAdmin)
                sections.Add(($"{Modes.Active.Title} (admin)", modeEntries.Where(e => e.AdminOnly).ToList()));
        }
        foreach (var group in _globalHelp.Where(e => isAdmin || !e.AdminOnly).GroupBy(e => e.Section))
            sections.Add((group.Key, group.ToList()));
        sections.RemoveAll(s => s.Entries.Count == 0);

        PrintHelpToConsole(player, sections);
        if (player == null)
            return;

        var title = Modes.Active == null ? "Commands" : $"Commands > {Modes.Active.Title}";
        var menu = new Menu(title, m =>
        {
            foreach (var (sectionTitle, entries) in sections)
            {
                m.Add($"-- {sectionTitle} --");
                foreach (var entry in entries)
                {
                    var line = Shorten($"{entry.Usage}  {entry.Description}", 48);
                    m.Add(line, p => RunFromHelp(p, entry), entry.AdminOnly ? "#FF8080" : null);
                }
            }
        });
        menu.Cursor = 1; // skip the first section header
        Menus.Open(player, menu);
        Chat.To(player, "Select a command to run it. The full list is also in your console.");
    }

    private void RunFromHelp(CCSPlayerController player, HelpEntry entry)
    {
        if (entry.NeedsArguments)
        {
            Chat.To(player, $"Usage: {{green}}{entry.Usage}{{default}} - {entry.Description}");
            return;
        }

        Menus.Close(player);
        Server.NextFrame(() =>
        {
            if (Players.IsValid(player))
                player.ExecuteClientCommandFromServer($"css_{entry.Command}");
        });
    }

    private static void PrintHelpToConsole(CCSPlayerController? player, List<(string Title, List<HelpEntry> Entries)> sections)
    {
        void Print(string line)
        {
            if (player == null)
                Server.PrintToConsole(line);
            else
                player.PrintToConsole(line);
        }

        Print("---------------- FragLab commands ----------------");
        foreach (var (title, entries) in sections)
        {
            Print($"[{title}]");
            foreach (var entry in entries)
                Print($"  {entry.Usage,-40} {entry.Description}{entry.AliasText}");
        }
        Print("Type them in chat with ! (e.g. !noclip) or in console with css_ (e.g. css_noclip).");
    }

    private HashSet<string>? _commandNames;

    /// <summary>
    /// MatchZy-style dot commands: ".rethrow" in chat runs !rethrow. Only names of this plugin's commands
    /// are picked up, so normal messages starting with a dot are left alone.
    /// </summary>
    private HookResult OnChatMessage(CCSPlayerController? player, CommandInfo info)
    {
        if (!Players.IsValid(player))
            return HookResult.Continue;

        var text = info.ArgString.Trim().Trim('"').Trim();
        if (text.Length < 2 || text[0] != '.' || !char.IsLetter(text[1]))
            return HookResult.Continue;

        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();

        _commandNames ??= Modes.All.SelectMany(m => m.CommandHelp).Concat(_globalHelp)
            .SelectMany(e => e.Aliases.Append(e.Command))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_commandNames.Contains(name))
            return HookResult.Continue;

        var command = parts.Length > 1 ? $"css_{name} {parts[1]}" : $"css_{name}";
        Server.NextFrame(() =>
        {
            if (Players.IsValid(player))
                player!.ExecuteClientCommandFromServer(command);
        });
        return HookResult.Continue;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "...";

    private void RegisterEvents()
    {
        RegisterEventHandler<EventPlayerConnectFull>((e, _) =>
        {
            if (Players.IsValid(e.Userid))
                Modes.Dispatch(m => m.OnPlayerConnectFull(e.Userid!));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid is { IsValid: true })
                Modes.Dispatch(m => m.OnPlayerDisconnect(e.Userid));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerSpawn>((e, _) =>
        {
            var spawned = e.Userid;
            if (Players.IsValid(spawned) && Modes.Active is not PracticeMode)
            {
                // Noclip from practice must never survive into another mode.
                Server.NextFrame(() =>
                {
                    if (Players.IsValid(spawned))
                        Players.ForceWalk(spawned!);
                });
            }

            if (Players.IsValid(e.Userid))
                Modes.Dispatch(m => m.OnPlayerSpawn(e.Userid!));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (Players.IsValid(e.Userid))
                Modes.Dispatch(m => m.OnPlayerDeath(e.Userid!, Players.IsValid(e.Attacker) ? e.Attacker : null));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerTeam>((e, _) =>
        {
            if (!e.Disconnect && Players.IsValid(e.Userid))
                Modes.Dispatch(m => m.OnPlayerTeam(e.Userid!, e.Oldteam, e.Team));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundPrestart>((_, _) =>
        {
            Modes.Dispatch(m => m.OnRoundPrestart());
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            Modes.Dispatch(m => m.OnRoundStart());
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundFreezeEnd>((_, _) =>
        {
            Modes.Dispatch(m => m.OnRoundFreezeEnd());
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundEnd>((e, _) =>
        {
            Modes.Dispatch(m => m.OnRoundEnd(e.Winner, e.Reason));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventCsWinPanelMatch>((_, _) =>
        {
            Modes.Dispatch(m => m.OnMatchEnd());
            return HookResult.Continue;
        });

        AddCommandListener("say", OnChatMessage);
        AddCommandListener("say_team", OnChatMessage);

        AddCommandListener("jointeam", (player, info) =>
            Players.IsValid(player) ? Modes.Dispatch(m => m.OnJoinTeamCommand(player!, info)) : HookResult.Continue);
    }
}
