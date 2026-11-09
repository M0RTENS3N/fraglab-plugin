using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Scrim;

/// <summary>
/// Ready-up per team: a team is ready once one of its players types !ready, or types the team's name (e.g. "!Sovs").
/// The match starts when both teams are ready.
/// </summary>
public sealed partial class ScrimMode
{
    /// <summary>A side is ready when at least one of its players has readied up.</summary>
    private bool SideReady(CsTeam side) =>
        TeamHumans().Any(p => p.Team == side && _ready.Contains(p.SteamID));

    /// <summary>The team's name from the server (set with !teamnames), or CT / T when none is set.</summary>
    private static string SideName(CsTeam side)
    {
        var name = ClanName(side);
        return string.IsNullOrWhiteSpace(name) ? (side == CsTeam.CounterTerrorist ? "CT" : "T") : name;
    }

    private static string ClanName(CsTeam side)
    {
        var teamNumber = side == CsTeam.CounterTerrorist ? 3 : 2;
        var team = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager").FirstOrDefault(t => t.TeamNum == teamNumber);
        return team?.ClanTeamname ?? "";
    }

    public override bool HandleChatWord(CCSPlayerController player, string word) => TryReadyByTeamName(player, word);

    /// <summary>Hidden easter egg: "!sovs" readies your team exactly like !ready. Not listed in !help.</summary>
    public bool TryReadyByTeamName(CCSPlayerController player, string word)
    {
        if (word != "sovs")
            return false;
        if (!(IsActive || _parkedForRetake) || _state != ScrimState.Warmup || !Players.IsPlayingTeam(player.Team))
            return false;

        OnReady(player, null!);
        return true;
    }

    /// <summary>One line for chat, e.g. "Sovs (CT): READY   Team B (T): waiting".</summary>
    private string ReadyStatus() => $"{ReadyPart(CsTeam.CounterTerrorist)}   {ReadyPart(CsTeam.Terrorist)}";

    private string ReadyPart(CsTeam side)
    {
        var tag = side == CsTeam.CounterTerrorist ? "CT" : "T";
        var label = string.IsNullOrWhiteSpace(ClanName(side)) ? tag : $"{ClanName(side)} ({tag})";
        var state = SideReady(side) ? "{green}READY{default}" : "{red}waiting{default}";
        return $"{label}: {state}";
    }
}
