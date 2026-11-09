using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace FragLab.Core;

/// <summary>
/// Base class for every game mode (scrim, retake, future minigames).
/// Only the active mode receives events; commands of inactive modes reply with a notice.
/// </summary>
public abstract class GameMode
{
    private readonly List<HelpEntry> _commandHelp = new();

    protected GameMode(FragLabPlugin plugin)
    {
        Plugin = plugin;
    }

    protected FragLabPlugin Plugin { get; }

    /// <summary>Identifier used with !mode, e.g. "scrim".</summary>
    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>Display name, e.g. in !help.</summary>
    public virtual string Title => char.ToUpperInvariant(Name[0]) + Name[1..];

    /// <summary>Other names accepted by !mode.</summary>
    public virtual IEnumerable<string> Aliases => [];

    public bool IsActive => Plugin.Modes.Active == this;

    /// <summary>Print the damage report (to/from each enemy) at round end.</summary>
    public virtual bool ShowDamageReport => false;

    public IReadOnlyList<HelpEntry> CommandHelp => _commandHelp;

    /// <summary>Called once on plugin load. Register commands with <see cref="AddCommand"/> and any hooks here.</summary>
    public virtual void Register() { }

    /// <summary>Called when the plugin unloads. Remove native hooks here.</summary>
    public virtual void Unregister() { }

    /// <summary>Called when the mode becomes active and again after every map start.</summary>
    public virtual void Activate() { }

    /// <summary>Called when switching away from this mode.</summary>
    public virtual void Deactivate() { }

    /// <summary>Called on map start before <see cref="Activate"/> is re-run.</summary>
    public virtual void OnMapStart(string mapName) { }

    public virtual void PrintStatus(CCSPlayerController? player) { }

    /// <summary>A chat word like !Sovs while this mode is active (not a registered command). Return true if it was used.</summary>
    public virtual bool HandleChatWord(CCSPlayerController player, string word) => false;

    /// <summary>"!mode <this mode>" while it's already active.</summary>
    public virtual void Reselected(CCSPlayerController? player) => Chat.To(player, $"{Title} is already active.");

    // Event hooks, forwarded by the plugin to the active mode only.
    public virtual void OnPlayerConnectFull(CCSPlayerController player) { }
    public virtual void OnPlayerDisconnect(CCSPlayerController player) { }
    public virtual void OnPlayerSpawn(CCSPlayerController player) { }
    public virtual void OnPlayerDeath(CCSPlayerController victim, CCSPlayerController? attacker) { }
    public virtual void OnPlayerTeam(CCSPlayerController player, int oldTeam, int newTeam) { }
    public virtual void OnRoundPrestart() { }
    public virtual void OnRoundStart() { }
    public virtual void OnRoundFreezeEnd() { }
    public virtual void OnRoundEnd(int winner, int reason) { }
    public virtual void OnMatchEnd() { }
    public virtual HookResult OnJoinTeamCommand(CCSPlayerController player, CommandInfo info) => HookResult.Continue;

    /// <summary>
    /// Registers css_&lt;name&gt; for every alias (usable as !name / /name in chat).
    /// The handler only runs while this mode is active. Several modes may register the same name
    /// (e.g. retake and fun retake); the command is registered once and the active mode answers.
    /// </summary>
    protected void AddCommand(string[] names, string description,
        Action<CCSPlayerController?, CommandInfo> handler, bool adminOnly = false)
    {
        foreach (var name in names)
        {
            if (!Plugin.ModeCommands.TryGetValue(name, out var owners))
            {
                Plugin.ModeCommands[name] = owners = [];
                Plugin.AddCommand($"css_{name}", description, (player, info) => RunModeCommand(name, owners, player, info));
            }

            owners.Add((this, handler, adminOnly));
        }

        _commandHelp.Add(new HelpEntry(Title, names, description, adminOnly));
    }

    private void RunModeCommand(string name, List<(GameMode Mode, Action<CCSPlayerController?, CommandInfo> Handler, bool AdminOnly)> owners,
        CCSPlayerController? player, CommandInfo info)
    {
        var owner = owners.FirstOrDefault(o => o.Mode.IsActive);
        if (owner.Mode == null)
        {
            var modes = string.Join(" / ", owners.Select(o => o.Mode.Name).Distinct());
            Chat.To(player, $"{{red}}!{name}{{default}} is only available in {{green}}{modes}{{default}} mode.");
            return;
        }

        if (owner.AdminOnly && !Players.IsAdmin(player, Plugin.Config.AdminFlag))
        {
            Chat.To(player, "{red}You don't have permission to use this command.");
            return;
        }

        owner.Handler(player, info);
    }

    protected static void ExecConfig(string cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg))
            Server.ExecuteCommand($"exec {cfg}");
    }
}
