namespace FragLab.Modes.Chaos;

public sealed class ChaosConfig
{
    public string Config { get; set; } = "fraglab/chaos.cfg";

    /// <summary>
    /// The weapon ladder, one level per kill. The knife is always the last level after these:
    /// a knife kill on the last level wins the game.
    /// </summary>
    public List<string> Ladder { get; set; } =
    [
        "weapon_glock", "weapon_usp_silencer", "weapon_p250", "weapon_fiveseven", "weapon_deagle",
        "weapon_mac10", "weapon_mp9", "weapon_ump45", "weapon_p90", "weapon_nova",
        "weapon_xm1014", "weapon_mag7", "weapon_galilar", "weapon_famas", "weapon_ak47",
        "weapon_m4a1_silencer", "weapon_aug", "weapon_ssg08", "weapon_awp", "weapon_negev"
    ];

    /// <summary>Bots fill the server up to this many players (0 = no bots).</summary>
    public int FillBots { get; set; } = 6;

    /// <summary>After spawning you take no damage for this long (or until you shoot).</summary>
    public float SpawnProtectionSeconds { get; set; } = 3f;

    /// <summary>Seconds between chaos events, and how long each one lasts.</summary>
    public float ChaosInterval { get; set; } = 40f;
    public float ChaosDuration { get; set; } = 20f;

    /// <summary>Chaos events that can be rolled. Remove a name to turn it off.</summary>
    public List<string> ChaosEvents { get; set; } =
        ["LowGravity", "SpeedRush", "HeadshotsOnly", "OneShot", "Vampire", "Ghosts", "Bhop", "Juggernauts", "Explosive", "Tiny"];

    /// <summary>Kill streak rewards: a heal at 3 kills, a speed boost at 5.</summary>
    public int StreakHealKills { get; set; } = 3;
    public int StreakSpeedKills { get; set; } = 5;
    public float StreakSpeedSeconds { get; set; } = 8f;

    /// <summary>The leader glows through walls for everyone.</summary>
    public bool LeaderGlow { get; set; } = true;

    public float RestartSeconds { get; set; } = 12f;
}
