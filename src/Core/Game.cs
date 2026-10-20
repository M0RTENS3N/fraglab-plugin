using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FragLab.Core;

public static class Game
{
    // Looking the game rules up scans every entity, so keep the proxy until it goes away (map change).
    private static CCSGameRulesProxy? _rulesProxy;

    public static CCSGameRules? Rules()
    {
        if (_rulesProxy == null || !_rulesProxy.IsValid)
            _rulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return _rulesProxy?.GameRules;
    }

    public static bool IsWarmupOrFreezeTime()
    {
        var rules = Rules();
        return rules != null && (rules.WarmupPeriod || rules.FreezePeriod);
    }
}
