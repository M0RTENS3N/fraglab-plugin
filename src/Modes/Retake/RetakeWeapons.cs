namespace FragLab.Modes.Retake;

public enum RoundType
{
    Pistol,
    ForceBuy,
    FullBuy
}

public enum WeaponKind
{
    Pistol,
    Smg,
    Shotgun,
    Rifle,
    Sniper,
    MachineGun
}

/// <summary>Firearms by definition index, and which round types allow them.</summary>
public static class RetakeWeapons
{
    public const int Awp = 9;

    private static readonly Dictionary<int, (string ClassName, WeaponKind Kind)> ByDefindex = new()
    {
        [1] = ("weapon_deagle", WeaponKind.Pistol),
        [2] = ("weapon_elite", WeaponKind.Pistol),
        [3] = ("weapon_fiveseven", WeaponKind.Pistol),
        [4] = ("weapon_glock", WeaponKind.Pistol),
        [30] = ("weapon_tec9", WeaponKind.Pistol),
        [32] = ("weapon_hkp2000", WeaponKind.Pistol),
        [36] = ("weapon_p250", WeaponKind.Pistol),
        [61] = ("weapon_usp_silencer", WeaponKind.Pistol),
        [63] = ("weapon_cz75a", WeaponKind.Pistol),
        [64] = ("weapon_revolver", WeaponKind.Pistol),

        [17] = ("weapon_mac10", WeaponKind.Smg),
        [19] = ("weapon_p90", WeaponKind.Smg),
        [23] = ("weapon_mp5sd", WeaponKind.Smg),
        [24] = ("weapon_ump45", WeaponKind.Smg),
        [26] = ("weapon_bizon", WeaponKind.Smg),
        [33] = ("weapon_mp7", WeaponKind.Smg),
        [34] = ("weapon_mp9", WeaponKind.Smg),

        [25] = ("weapon_xm1014", WeaponKind.Shotgun),
        [27] = ("weapon_mag7", WeaponKind.Shotgun),
        [29] = ("weapon_sawedoff", WeaponKind.Shotgun),
        [35] = ("weapon_nova", WeaponKind.Shotgun),

        [7] = ("weapon_ak47", WeaponKind.Rifle),
        [8] = ("weapon_aug", WeaponKind.Rifle),
        [10] = ("weapon_famas", WeaponKind.Rifle),
        [13] = ("weapon_galilar", WeaponKind.Rifle),
        [16] = ("weapon_m4a1", WeaponKind.Rifle),
        [39] = ("weapon_sg556", WeaponKind.Rifle),
        [60] = ("weapon_m4a1_silencer", WeaponKind.Rifle),

        [9] = ("weapon_awp", WeaponKind.Sniper),
        [11] = ("weapon_g3sg1", WeaponKind.Sniper),
        [38] = ("weapon_scar20", WeaponKind.Sniper),
        [40] = ("weapon_ssg08", WeaponKind.Sniper),

        [14] = ("weapon_m249", WeaponKind.MachineGun),
        [28] = ("weapon_negev", WeaponKind.MachineGun)
    };

    private static readonly Dictionary<string, int> ByClassName =
        ByDefindex.ToDictionary(kv => kv.Value.ClassName, kv => kv.Key);

    public static bool TryGet(int defindex, out string className, out WeaponKind kind)
    {
        if (ByDefindex.TryGetValue(defindex, out var info))
        {
            className = info.ClassName;
            kind = info.Kind;
            return true;
        }

        className = "";
        kind = default;
        return false;
    }

    /// <summary>The free starting pistols (Glock, USP-S, P2000).</summary>
    public static bool IsDefaultPistol(string? className) =>
        className is "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000";

    public static int? DefindexOf(string className) =>
        ByClassName.TryGetValue(className, out var defindex) ? defindex : null;

    public static bool IsAllowed(RoundType round, WeaponKind kind) => round switch
    {
        RoundType.Pistol => kind == WeaponKind.Pistol,
        RoundType.ForceBuy => kind is WeaponKind.Pistol or WeaponKind.Smg or WeaponKind.Shotgun,
        _ => true
    };

    public static string Describe(RoundType round, bool awpEnabled) => round switch
    {
        RoundType.Pistol => "Pistol round: pistols only, armor only with the default pistol",
        RoundType.ForceBuy => "Force buy: pistols and SMGs",
        _ => awpEnabled ? "Full buy: everything (1 AWP per team)" : "Full buy: rifles, pistols and SMGs"
    };
}
