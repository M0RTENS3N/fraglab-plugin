using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace FragLab.Core;

/// <summary>
/// Admin map changes: !map &lt;name|workshop id&gt; plus shortcuts like !ancient, !mirage.
/// The active game mode stays active on the new map.
/// </summary>
public sealed class MapCommands
{
    private const float Delay = 3f;

    private static readonly (string Command, string Map)[] Shortcuts =
    [
        ("ancient", "de_ancient"),
        ("anubis", "de_anubis"),
        ("dust2", "de_dust2"),
        ("inferno", "de_inferno"),
        ("mirage", "de_mirage"),
        ("nuke", "de_nuke"),
        ("overpass", "de_overpass"),
        ("train", "de_train"),
        ("vertigo", "de_vertigo"),
        ("office", "cs_office"),
        ("italy", "cs_italy")
    ];

    private readonly FragLabPlugin _plugin;
    private bool _changing;

    public MapCommands(FragLabPlugin plugin)
    {
        _plugin = plugin;
    }

    public void Register()
    {
        _plugin.AddCommand("css_map", "Change map: !map <name|workshop id>", (p, info) => OnMapCommand(p, info));
        _plugin.AddHelp("Maps", ["map"], "Change map: !map <name|workshop id>", adminOnly: true);

        foreach (var (command, map) in Shortcuts)
        {
            _plugin.AddCommand($"css_{command}", $"Change map to {map}", (p, _) => ChangeMap(p, map));
            _plugin.AddHelp("Maps", [command], $"Change map to {map}", adminOnly: true);
        }

        // A map change started by someone else cancels our timer; don't stay stuck in "changing".
        _plugin.RegisterListener<Listeners.OnMapStart>(_ => _changing = false);
    }

    private void OnMapCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            Chat.To(player, $"Current map: {{green}}{Server.MapName}{{default}}. Usage: !map <name|workshop id>, " +
                            $"or {string.Join(" ", Shortcuts.Select(s => "!" + s.Command))}");
            return;
        }

        var arg = info.GetArg(1).Trim().ToLowerInvariant();
        if (arg.All(char.IsDigit))
        {
            ChangeMap(player, arg, workshop: true);
            return;
        }

        // Accept "mirage" as well as "de_mirage".
        var shortcut = Shortcuts.FirstOrDefault(s => s.Command == arg).Map;
        ChangeMap(player, shortcut ?? arg);
    }

    private void ChangeMap(CCSPlayerController? player, string map, bool workshop = false)
    {
        if (!Players.IsAdmin(player, _plugin.Config.AdminFlag))
        {
            Chat.To(player, "{red}Only admins can change the map.");
            return;
        }

        if (_changing)
        {
            Chat.To(player, "A map change is already in progress.");
            return;
        }

        if (!workshop && !Server.IsMapValid(map))
        {
            Chat.To(player, $"{{red}}Map '{map}' is not installed on this server.{{default}} For workshop maps use !map <workshop id>.");
            return;
        }

        _changing = true;
        var label = workshop ? $"workshop map {map}" : map;
        Chat.All($"Changing map to {{green}}{label}{{default}} in {Delay:0} seconds...");

        _plugin.AddTimer(Delay, () =>
        {
            _changing = false;
            Server.ExecuteCommand(workshop ? $"host_workshop_map {map}" : $"changelevel {map}");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
}
