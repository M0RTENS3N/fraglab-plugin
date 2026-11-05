namespace FragLab.Skins;

public sealed class SkinsConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// CounterStrikeSharp permission needed to use skins (e.g. "@css/vip"). Empty = everyone (default).
    /// Scrim mode always lets only admins change skins.
    /// </summary>
    public string RequiredFlag { get; set; } = "";

    /// <summary>
    /// Instantly swap the weapon for a fresh copy after picking a skin so it shows right away.
    /// Ammo is carried over, so it can't be used to refill mid-round.
    /// </summary>
    public bool RefreshWeaponOnSelect { get; set; } = true;

    /// <summary>
    /// Byte signatures of CAttributeList::SetOrAddAttributeValueByName in the server binary.
    /// These can break after CS2 updates; take the new values from the cs2-WeaponPaints gamedata when they do.
    /// </summary>
    public string SignatureLinux { get; set; } =
        "55 48 89 E5 41 57 41 56 41 55 49 89 FD 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85";

    public string SignatureWindows { get; set; } = "48 89 4C 24 ? 53 41 55 41 56";

    /// <summary>
    /// Ready-made loadouts that players WITHOUT RequiredFlag can pick in !skins. Admins can pick them too.
    /// Weapons are entity classes mapped to paint kit ids (see data/skins.json). Empty list = no skins for non-admins.
    /// </summary>
    public List<SkinPreset> Presets { get; set; } =
    [
        new()
        {
            Name = "Green", Color = "#4CAF50", Knife = "weapon_knife_karambit", KnifePaint = 568, // Gamma Doppler Emerald
            Weapons = new()
            {
                ["weapon_ak47"] = 300, ["weapon_m4a1"] = 187, ["weapon_m4a1_silencer"] = 1001, ["weapon_awp"] = 1280,
                ["weapon_usp_silencer"] = 454, ["weapon_glock"] = 1200, ["weapon_deagle"] = 757
            }
        },
        new()
        {
            Name = "Red", Color = "#eb4b4b", Knife = "weapon_knife_karambit", KnifePaint = 12, // Crimson Web
            Weapons = new()
            {
                ["weapon_ak47"] = 282, ["weapon_m4a1"] = 309, ["weapon_m4a1_silencer"] = 445, ["weapon_awp"] = 259,
                ["weapon_usp_silencer"] = 217, ["weapon_glock"] = 1079, ["weapon_deagle"] = 711
            }
        },
        new()
        {
            Name = "Blue", Color = "#4B69FF", Knife = "weapon_knife_karambit", KnifePaint = 42, // Blue Steel
            Weapons = new()
            {
                ["weapon_ak47"] = 226, ["weapon_m4a1"] = 844, ["weapon_m4a1_silencer"] = 189, ["weapon_awp"] = 395,
                ["weapon_usp_silencer"] = 332, ["weapon_glock"] = 278, ["weapon_deagle"] = 231
            }
        }
    ];
}

public sealed class SkinPreset
{
    public string Name { get; set; } = "";

    /// <summary>Menu color, e.g. "#4CAF50".</summary>
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>Knife class, e.g. weapon_knife_karambit. Empty keeps the default knife.</summary>
    public string Knife { get; set; } = "";

    public int KnifePaint { get; set; }

    /// <summary>Weapon class -> paint kit id.</summary>
    public Dictionary<string, int> Weapons { get; set; } = new();
}