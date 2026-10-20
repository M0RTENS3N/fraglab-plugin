using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Modes.Retake;

/// <summary>Weapons each player last bought, per team, round type and slot. Saved per player.</summary>
public sealed class RetakeLoadoutPrefs
{
    /// <summary>Key: "T.FullBuy.Primary" etc. Value: weapon definition index.</summary>
    public Dictionary<string, int> Weapons { get; set; } = new();

    /// <summary>Wants the team's AWP in full-buy rounds (set by buying one).</summary>
    public bool WantsAwp { get; set; }
}

public sealed class WeaponAllocator
{
    private const string Smoke = "weapon_smokegrenade";

    private readonly RetakeConfig _config;
    private readonly HashSet<int> _awpers = new(); // player slots

    public WeaponAllocator(RetakeConfig config)
    {
        _config = config;
    }

    public RoundType RoundType { get; private set; }

    /// <summary>Set by the admin rules (!deagleonly, !awponly): everyone gets only this weapon.</summary>
    public string? OnlyWeapon { get; set; }

    public void StartRound(int roundsPlayed)
    {
        _awpers.Clear();
        RoundType = roundsPlayed < _config.PistolRounds ? RoundType.Pistol
            : roundsPlayed < _config.PistolRounds + _config.ForceBuyRounds ? RoundType.ForceBuy
            : RoundType.FullBuy;
    }

    public static string Key(CsTeam team, RoundType round, bool primary) =>
        $"{(team == CsTeam.Terrorist ? "T" : "CT")}.{round}.{(primary ? "Primary" : "Secondary")}";

    /// <summary>
    /// Stores a purchase so the player gets it again in the next round of the same type.
    /// Buying an AWP signs the player up for the AWP instead of replacing their saved rifle.
    /// </summary>
    public void Remember(RetakeLoadoutPrefs prefs, CsTeam team, int defindex, WeaponKind kind)
    {
        if (defindex == RetakeWeapons.Awp)
        {
            prefs.WantsAwp = true;
            return;
        }

        prefs.Weapons[Key(team, RoundType, kind != WeaponKind.Pistol)] = defindex;
    }

    /// <summary>
    /// Picks who gets an AWP this full-buy round: up to MaxAwpsPerTeam random players per team among
    /// those who want one. If nobody on a team wants it, one random player gets it with AwpChance.
    /// </summary>
    public void PickAwpers(IEnumerable<CCSPlayerController> players, Func<CCSPlayerController, RetakeLoadoutPrefs> prefsOf)
    {
        _awpers.Clear();
        if (RoundType != RoundType.FullBuy || !_config.AwpEnabled)
            return;

        foreach (var team in players.GroupBy(p => p.Team))
        {
            var members = team.ToList();
            var wanting = members.Where(p => prefsOf(p).WantsAwp).ToList();
            var pool = wanting.Count > 0 ? wanting
                : Random.Shared.NextDouble() < _config.AwpChance ? members
                : [];

            foreach (var player in pool.OrderBy(_ => Random.Shared.Next()).Take(_config.MaxAwpsPerTeam))
                _awpers.Add(player.Slot);
        }
    }

    public bool IsAwper(CCSPlayerController player) => _awpers.Contains(player.Slot);

    public void Equip(CCSPlayerController player, bool giveBomb, RetakeLoadoutPrefs prefs)
    {
        var team = player.Team;
        var round = RoundType switch
        {
            RoundType.Pistol => _config.Pistol,
            RoundType.ForceBuy => _config.ForceBuy,
            _ => _config.FullBuy
        };
        var loadout = team == CsTeam.Terrorist ? round.T : round.CT;

        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");

        if (OnlyWeapon != null)
        {
            player.GiveNamedItem(OnlyWeapon);
            player.GiveNamedItem("item_assaultsuit");
            if (team == CsTeam.CounterTerrorist)
                GiveDefuser(player);
            if (giveBomb)
                player.GiveNamedItem("weapon_c4");
            return;
        }

        if (RoundType != RoundType.Pistol)
        {
            var primary = RoundType == RoundType.FullBuy && IsAwper(player)
                ? "weapon_awp"
                : SavedWeapon(prefs, team, primary: true) ?? Pick(loadout.Primaries);
            if (primary != null)
                player.GiveNamedItem(primary);
        }

        var secondary = SavedWeapon(prefs, team, primary: false) ?? Pick(loadout.Secondaries);
        if (secondary != null)
            player.GiveNamedItem(secondary);

        if (!string.IsNullOrEmpty(loadout.Armor) && GetsArmor(secondary))
            player.GiveNamedItem(loadout.Armor);

        var grenades = team == CsTeam.Terrorist
            ? _config.TGrenades.Where(g => g != Smoke)
            : _config.CTGrenades;
        var count = Math.Max(0, _config.UtilityPerPlayer);
        foreach (var grenade in grenades.OrderBy(_ => Random.Shared.Next()).Take(count))
            player.GiveNamedItem(grenade);

        if (team == CsTeam.CounterTerrorist && loadout.Defuser)
            GiveDefuser(player);

        if (giveBomb)
            player.GiveNamedItem("weapon_c4");
    }

    /// <summary>In pistol rounds an upgraded pistol replaces the armor.</summary>
    public bool GetsArmor(string? secondary) =>
        RoundType != RoundType.Pistol || !_config.PistolRoundArmorOnlyWithDefaultPistol || RetakeWeapons.IsDefaultPistol(secondary);

    private string? SavedWeapon(RetakeLoadoutPrefs prefs, CsTeam team, bool primary)
    {
        if (!prefs.Weapons.TryGetValue(Key(team, RoundType, primary), out var defindex))
            return null;
        if (!RetakeWeapons.TryGet(defindex, out var className, out var kind) || !RetakeWeapons.IsAllowed(RoundType, kind) ||
            _config.DisabledWeapons.Contains(className))
            return null;
        if (defindex == RetakeWeapons.Awp)
            return null; // AWPs are handed out through PickAwpers
        return className;
    }

    /// <summary>Gives a defuse kit (only if the player doesn't have one, so no spare kit drops on the floor).</summary>
    public static void GiveDefuser(CCSPlayerController player)
    {
        var itemServices = player.PlayerPawn.Value?.ItemServices;
        if (itemServices == null || new CCSPlayer_ItemServices(itemServices.Handle).HasDefuser)
            return;
        player.GiveNamedItem("item_defuser");
    }

    private static string? Pick(List<string> options) =>
        options.Count == 0 ? null : options[Random.Shared.Next(options.Count)];
}
