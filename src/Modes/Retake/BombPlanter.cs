using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Modes.Retake;

/// <summary>Spawns an already-ticking C4 at a position, as if a player had just planted it.</summary>
public static class BombPlanter
{
    public static bool Plant(CCSPlayerController planter, Vector position, QAngle angles, Bombsite site)
    {
        var c4 = Utilities.CreateEntityByName<CPlantedC4>("planted_c4");
        if (c4 == null)
            return false;

        var timer = ConVar.Find("mp_c4timer")?.GetPrimitiveValue<int>() ?? 40;

        c4.BombSite = (int)site;
        c4.BombTicking = true;
        c4.HasExploded = false;
        c4.CannotBeDefused = false;
        c4.DispatchSpawn();

        Server.NextFrame(() =>
        {
            if (!c4.IsValid)
                return;

            c4.Teleport(position, angles, new Vector(0, 0, 0));
            c4.TimerLength = timer;
            c4.C4Blow = Server.CurrentTime + timer;
            Utilities.SetStateChanged(c4, "CPlantedC4", "m_flC4Blow");

            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (rules != null)
                rules.BombPlanted = true;

            if (planter.IsValid)
            {
                var planted = new EventBombPlanted(true) { Userid = planter, Site = (int)site };
                planted.FireEvent(false);
            }
        });

        return true;
    }
}
