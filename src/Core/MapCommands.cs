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
        ("cache", "de_cache"),
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
    private string? _workshopId; // set while a workshop map from !map is running

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

        _plugin.AddTimer(60f, ReloadIfEmpty, TimerFlags.REPEAT);
        _plugin.RegisterEventHandler<EventPlayerConnectFull>((e, _) =>
        {
            if (Players.IsValid(e.Userid) && !e.Userid!.IsBot)
                ReloadForFirstPlayer();
            return HookResult.Continue;
        });
    }

    // The game clock restarts with the map and loses precision the longer it runs, which shows as slow-motion
    // animations. So a map that has been running for hours is reloaded while nobody is playing.
    private bool MapIsOld
    {
        get
        {
            var hours = _plugin.Config.EmptyMapRefreshHours;
            return hours > 0 && !_changing && Server.CurrentTime >= hours * 3600f &&
                   (_workshopId != null || Server.IsMapValid(Server.MapName));
        }
    }

    private void ReloadIfEmpty()
    {
        if (MapIsOld && !Players.Humans().Any())
            ReloadMap();
    }

    /// <summary>If the server slept while empty the timer didn't run, so the first player to join triggers the reload.</summary>
    private void ReloadForFirstPlayer()
    {
        if (!MapIsOld || Players.Humans().Count() > 1)
            return;

        _changing = true;
        Chat.All("This map has been running for a long time. Reloading it in 5 seconds to keep animations smooth...");
        _plugin.AddTimer(5f, () =>
        {
            _changing = false;
            ReloadMap();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ReloadMap() =>
        Server.ExecuteCommand(_workshopId != null ? $"host_workshop_map {_workshopId}" : $"changelevel {Server.MapName}");

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
        var map = Shortcuts.FirstOrDefault(s => s.Command == arg).Map ?? arg;
        // "cache" means de_cache when that map is installed.
        if (!map.Contains('_') && Server.IsMapValid("de_" + map))
            map = "de_" + map;
        ChangeMap(player, map);
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
            _workshopId = workshop ? map : null;
            Server.ExecuteCommand(workshop ? $"host_workshop_map {map}" : $"changelevel {map}");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
}
