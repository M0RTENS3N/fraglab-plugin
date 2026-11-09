using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using FragLab.Core;
using FragLab.Core.Menus;
using FragLab.Modes.Deathmatch;

namespace FragLab.Modes.Scrim;

/// <summary>
/// Deathmatch during the scrim warmup: admins turn it on or off with !warm. While it's on, players respawn at random
/// spots with the deathmatch loadout and kills are posted in chat. Ready up as usual (!ready or !forcestart) to start the
/// match; the knife round or live match turns deathmatch off again. The setting is saved for the next scrim.
/// </summary>
public sealed partial class ScrimMode
{
    private const string DeathmatchOnCvars =
        "mp_respawn_on_death_ct 1; mp_respawn_on_death_t 1; mp_respawnwavetime_ct 1; mp_respawnwavetime_t 1; " +
        "mp_randomspawn 1; mp_teammates_are_enemies 1; mp_friendlyfire 0; mp_ignore_round_win_conditions 1; " +
        "mp_freezetime 0; mp_buy_anywhere 0; mp_buytime 0; mp_free_armor 1";

    private const string DeathmatchOffCvars =
        "mp_respawn_on_death_ct 0; mp_respawn_on_death_t 0; mp_teammates_are_enemies 0; mp_randomspawn 0; " +
        "mp_ignore_round_win_conditions 0; mp_friendlyfire 1";

    private void OpenWarmupMenu(CCSPlayerController? player)
    {
        if (player == null || !Players.IsValid(player))
            return;
        if (!Players.IsAdmin(player, Plugin.Config.AdminFlag))
        {
            Chat.To(player, "{red}Only admins can change the warmup.");
            return;
        }

        Plugin.Menus.Open(player, new Menu("Warmup", menu =>
        {
            menu.Add(Cfg.WarmupDeathmatch
                    ? "Deathmatch warmup: ON (select to turn off)"
                    : "Deathmatch warmup: OFF (select to turn on)",
                ToggleWarmupDeathmatch, Cfg.WarmupDeathmatch ? "#7CFC00" : null);
            menu.Add(Cfg.WarmupRetake
                    ? "Retake warmup: ON (select to turn off)"
                    : "Retake warmup: OFF (select to turn on)",
                ToggleWarmupRetake, Cfg.WarmupRetake ? "#7CFC00" : null);
            menu.Add("Close", p => Plugin.Menus.Close(p), "#888888");
        }));
    }

    private void ToggleWarmupDeathmatch(CCSPlayerController player)
    {
        Plugin.Menus.Close(player);
        // Deathmatch and retake are exclusive; retake is handed back to the scrim first.
        if (_parkedForRetake)
        {
            Cfg.WarmupRetake = false;
            ResumeFromRetake(startAfter: false);
        }
        if (_state != ScrimState.Warmup)
        {
            Chat.To(player, "The warmup can only be changed during the scrim warmup.");
            return;
        }

        Cfg.WarmupDeathmatch = !Cfg.WarmupDeathmatch;
        if (Cfg.WarmupDeathmatch)
            Cfg.WarmupRetake = false;
        Plugin.SaveConfig();

        // Restarting the warmup respawns everyone, so the new setting applies at once.
        ExecConfig(Cfg.WarmupConfig);
        if (Cfg.WarmupDeathmatch)
            Server.ExecuteCommand(DeathmatchOnCvars);
        else
            Server.ExecuteCommand(DeathmatchOffCvars);
        Server.ExecuteCommand("mp_warmup_start");

        Chat.All(Cfg.WarmupDeathmatch
            ? "{green}Deathmatch warmup on.{default} Shoot each other until everyone types {green}!ready{default}. It stays on for the next scrim."
            : "{red}Deathmatch warmup off.{default} Type {green}!ready{default} when you are ready.");
    }

    /// <summary>Gives the deathmatch loadout to a player who spawns during the warmup.</summary>
    private void WarmupDeathmatchSpawn(CCSPlayerController player)
    {
        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var spawned = Utilities.GetPlayerFromSlot(slot);
            if (!Players.IsValid(spawned) || !spawned!.PawnIsAlive || _state != ScrimState.Warmup)
                return;
            DeathmatchMode.GiveLoadout(spawned);
            Plugin.Skins.ReapplyLater(spawned);
        });
    }

    public override void OnPlayerDeath(CCSPlayerController victim, CCSPlayerController? attacker)
    {
        if (_state != ScrimState.Warmup || !Cfg.WarmupDeathmatch)
            return;
        if (attacker == null || attacker.Slot == victim.Slot || !Players.IsValid(attacker))
            return;
        Chat.All($"{attacker.PlayerName} killed {victim.PlayerName}.");
    }
}
