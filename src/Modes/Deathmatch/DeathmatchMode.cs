using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Deathmatch;

/// <summary>
/// Deathmatch for warmup: everyone respawns at a random spot with a knife, a pistol, the side's rifle and armor.
/// Kills are announced in chat with the attacker's kill count. !warm gets you back to the scrim warmup.
/// </summary>
public sealed class DeathmatchMode : GameMode
{
    private readonly Dictionary<int, int> _kills = new();

    public DeathmatchMode(FragLabPlugin plugin) : base(plugin)
    {
    }

    public override string Name => "dm";
    public override string Description => "Deathmatch for warmup: respawn, guns, kills in chat";
    public override string Title => "Deathmatch";
    public override IEnumerable<string> Aliases => ["deathmatch"];

    public override void Register()
    {
        AddCommand(["warm", "warmup"], "Warmup menu: retake, deathmatch or back to the scrim warmup", (p, _) => WarmupMenu.Open(Plugin, p));
    }

    public override void Activate()
    {
        _kills.Clear();
        Server.ExecuteCommand("mp_do_warmup_period 0; mp_warmup_end; mp_ignore_round_win_conditions 1; " +
                              "mp_respawn_on_death_ct 1; mp_respawn_on_death_t 1; mp_respawnwavetime_ct 1; mp_respawnwavetime_t 1; " +
                              "mp_randomspawn 1; mp_friendlyfire 0; mp_teammates_are_enemies 1; mp_freezetime 0; mp_roundtime 60; " +
                              "mp_buy_anywhere 0; mp_buytime 0; mp_free_armor 1; mp_playercashawards 0; mp_teamcashawards 0");
        Server.ExecuteCommand("mp_restartgame 1");
        Chat.All("{green}Deathmatch{default}: respawn and shoot. Type {green}!warm{default} for the menu.");
    }

    public override void Deactivate()
    {
        Server.ExecuteCommand("mp_teammates_are_enemies 0; mp_respawn_on_death_ct 0; mp_respawn_on_death_t 0; " +
                              "mp_ignore_round_win_conditions 0; mp_randomspawn 0; mp_friendlyfire 1");
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (!Players.IsPlayingTeam(player.Team))
            return;

        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var spawned = Utilities.GetPlayerFromSlot(slot);
            if (Players.IsValid(spawned) && spawned!.PawnIsAlive)
                GiveLoadout(spawned);
        });
    }

    private static void GiveLoadout(CCSPlayerController player)
    {
        var ct = player.Team == CsTeam.CounterTerrorist;
        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");
        player.GiveNamedItem(ct ? "weapon_hkp2000" : "weapon_glock");
        player.GiveNamedItem(ct ? "weapon_m4a1" : "weapon_ak47");
        player.GiveNamedItem("item_assaultsuit");
    }

    public override void OnPlayerDeath(CCSPlayerController victim, CCSPlayerController? attacker)
    {
        if (attacker == null || attacker.Slot == victim.Slot || !Players.IsValid(attacker))
            return;

        _kills[attacker.Slot] = _kills.GetValueOrDefault(attacker.Slot) + 1;
        Chat.All($"{attacker.PlayerName} ({_kills[attacker.Slot]}) killed {victim.PlayerName}.");
    }

    public override void OnPlayerDisconnect(CCSPlayerController player) => _kills.Remove(player.Slot);

    public override void PrintStatus(CCSPlayerController? player)
    {
        var top = _kills.OrderByDescending(k => k.Value).Take(3)
            .Select(k => $"{Utilities.GetPlayerFromSlot(k.Key)?.PlayerName ?? "?"} {k.Value}");
        Chat.To(player, $"Deathmatch. Top kills: {(string.Join(", ", top) is { Length: > 0 } s ? s : "none yet")}");
    }
}
