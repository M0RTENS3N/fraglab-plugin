using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Core;

public static class Players
{
    public static bool IsValid(CCSPlayerController? player) =>
        player is { IsValid: true, IsHLTV: false, Connected: PlayerConnectedState.Connected };

    /// <summary>All connected players (humans and bots), excluding SourceTV.</summary>
    public static IEnumerable<CCSPlayerController> Connected() =>
        Utilities.GetPlayers().Where(IsValid);

    public static IEnumerable<CCSPlayerController> Humans() =>
        Connected().Where(p => !p.IsBot);

    public static IEnumerable<CCSPlayerController> OnTeam(CsTeam team) =>
        Connected().Where(p => p.Team == team);

    public static IEnumerable<CCSPlayerController> Playing() =>
        Connected().Where(p => IsPlayingTeam(p.Team));

    public static bool IsPlayingTeam(CsTeam team) =>
        team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    public static CsTeam Opposite(CsTeam team) => team switch
    {
        CsTeam.Terrorist => CsTeam.CounterTerrorist,
        CsTeam.CounterTerrorist => CsTeam.Terrorist,
        _ => team
    };

    public static string TeamName(CsTeam team) => team switch
    {
        CsTeam.Terrorist => "{gold}T{default}",
        CsTeam.CounterTerrorist => "{lightblue}CT{default}",
        CsTeam.Spectator => "{grey}Spectator{default}",
        _ => "None"
    };

    /// <summary>
    /// Turns noclip off (from !noclip or the console noclip cheat) and stops any leftover flying speed.
    /// Returns true if the player was in noclip. Other move types are left alone.
    /// </summary>
    public static bool ForceWalk(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return false;
        if (pawn.MoveType != MoveType_t.MOVETYPE_NOCLIP && pawn.ActualMoveType != MoveType_t.MOVETYPE_NOCLIP)
            return false;

        pawn.MoveType = MoveType_t.MOVETYPE_WALK;
        pawn.ActualMoveType = MoveType_t.MOVETYPE_WALK;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        pawn.Teleport(null, null, new Vector(0, 0, 0));
        return true;
    }

    /// <summary>
    /// Leaving noclip mid-air or inside a wall can glitch movement, so a player who was in noclip also gets a
    /// clean respawn at a normal spawn point.
    /// </summary>
    public static void LeaveNoclipSafely(CCSPlayerController player)
    {
        if (!ForceWalk(player))
            return;

        Server.NextFrame(() =>
        {
            if (IsValid(player) && player.PawnIsAlive && IsPlayingTeam(player.Team))
                player.Respawn();
        });
    }

    /// <summary>The server console (null player) is always treated as admin.</summary>
    public static bool IsAdmin(CCSPlayerController? player, string flag) =>
        player == null || AdminManager.PlayerHasPermissions(player, flag);
}
