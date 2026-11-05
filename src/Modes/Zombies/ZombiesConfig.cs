namespace FragLab.Modes.Zombies;

public sealed class ZombiesConfig
{
    public string Config { get; set; } = "fraglab/zombies.cfg";

    /// <summary>Points (shown as money) every player starts with.</summary>
    public int StartPoints { get; set; } = 500;

    public int PointsPerHit { get; set; } = 10;
    public int PointsPerKill { get; set; } = 60;
    public int PointsPerHeadshotKill { get; set; } = 100;
    public int PointsPerKnifeKill { get; set; } = 130;

    /// <summary>Zombies in wave 1, and how many more each wave and per extra player.</summary>
    public int ZombiesFirstWave { get; set; } = 6;
    public int ZombiesPerWave { get; set; } = 3;
    public int ZombiesPerExtraPlayer { get; set; } = 3;

    /// <summary>Most zombies alive at once (also the number of zombie bots on the server).</summary>
    public int MaxZombiesAlive { get; set; } = 10;

    public int ZombieHealthFirstWave { get; set; } = 100;
    public int ZombieHealthPerWave { get; set; } = 25;

    /// <summary>Zombies run a bit faster every wave, up to MaxZombieSpeed (1 = normal).</summary>
    public float ZombieSpeedPerWave { get; set; } = 0.03f;
    public float MaxZombieSpeed { get; set; } = 1.4f;

    /// <summary>Seconds before a killed zombie comes back (while the wave still has zombies left).</summary>
    public float ZombieRespawnSeconds { get; set; } = 1.5f;

    /// <summary>Break between waves. Dead players come back at the start of the next wave.</summary>
    public float WaveBreakSeconds { get; set; } = 12f;

    public int ArmorPrice { get; set; } = 1000;
    public int AmmoPrice { get; set; } = 500;
    public int MysteryBoxPrice { get; set; } = 950;

    /// <summary>How long the mystery box spins before it shows the weapon.</summary>
    public float MysteryBoxRollSeconds { get; set; } = 3f;

    /// <summary>Weapon prices in the shop. Remove a line to take the weapon out of the shop.</summary>
    public Dictionary<string, int> WeaponPrices { get; set; } = new()
    {
        ["weapon_deagle"] = 750,
        ["weapon_mp9"] = 1000,
        ["weapon_ump45"] = 1200,
        ["weapon_nova"] = 900,
        ["weapon_p90"] = 1500,
        ["weapon_xm1014"] = 1500,
        ["weapon_ak47"] = 2000,
        ["weapon_m4a1_silencer"] = 2000,
        ["weapon_sg556"] = 2200,
        ["weapon_awp"] = 2500,
        ["weapon_m249"] = 3000,
        ["weapon_negev"] = 3000
    };

    /// <summary>Weapons the mystery box can roll (any weapon, even ones not in the shop).</summary>
    public List<string> MysteryBoxWeapons { get; set; } =
    [
        "weapon_deagle", "weapon_revolver", "weapon_mp9", "weapon_mac10", "weapon_ump45", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_mag7", "weapon_sawedoff", "weapon_ak47", "weapon_m4a1_silencer",
        "weapon_m4a1", "weapon_aug", "weapon_sg556", "weapon_famas", "weapon_galilar", "weapon_ssg08", "weapon_awp",
        "weapon_g3sg1", "weapon_scar20", "weapon_m249", "weapon_negev"
    ];

    // Perks are lost on death, like in COD zombies.
    public int JuggernogPrice { get; set; } = 2500;
    public int JuggernogHealth { get; set; } = 250;
    public int DoubleTapPrice { get; set; } = 2000;
    public float DoubleTapDamage { get; set; } = 1.5f;
    public int StaminUpPrice { get; set; } = 2000;
    public float StaminUpSpeed { get; set; } = 1.2f;
    public int QuickRevivePrice { get; set; } = 1500;

    /// <summary>Pack-a-Punch: upgrades the gun in your hands.</summary>
    public int PackAPunchPrice { get; set; } = 5000;
    public float PackAPunchDamage { get; set; } = 2f;
    /// <summary>A gun can be upgraded this many times; each upgrade adds PackAPunchDamage - 1 again (x2, x3, x4).</summary>
    public int PackAPunchMaxLevel { get; set; } = 3;

    /// <summary>Chance (0-1) that a zombie killed by a player drops a power-up (Max Ammo, Insta-Kill, Double Points, Nuke).</summary>
    public double PowerUpChance { get; set; } = 0.04;
    /// <summary>How long Insta-Kill and Double Points last.</summary>
    public float PowerUpSeconds { get; set; } = 20f;
    /// <summary>A power-up nobody picks up disappears after this long.</summary>
    public float PowerUpDespawnSeconds { get; set; } = 25f;
    public int NukePoints { get; set; } = 400;
    /// <summary>The boss survives a nuke and loses this part of its max HP.</summary>
    public float NukeBossDamage { get; set; } = 0.25f;

    /// <summary>Every Nth wave has a boss zombie (0 = no bosses).</summary>
    public int BossEveryWaves { get; set; } = 5;
    public int BossHealthMultiplier { get; set; } = 10;
    public float BossScale { get; set; } = 1.4f;
    public int BossKillPoints { get; set; } = 500;

    /// <summary>Spawn zombies at spots players have walked, between these distances from the nearest player.</summary>
    public bool SpawnNearPlayers { get; set; } = true;
    public float SpawnMinDistance { get; set; } = 700f;
    public float SpawnMaxDistance { get; set; } = 1800f;

    /// <summary>A zombie further than this from every player for ZombieLostSeconds is moved closer (so they always hunt you).</summary>
    public float ZombieLostDistance { get; set; } = 2200f;
    public float ZombieLostSeconds { get; set; } = 5f;

    /// <summary>Zombies stuck on an obstacle jump (crouch-jump) towards the nearest player.</summary>
    public bool ZombieJumps { get; set; } = true;

    /// <summary>Health regenerates like in COD: after this long without being hit, this much per second.</summary>
    public float RegenDelaySeconds { get; set; } = 4f;
    public float RegenPerSecond { get; set; } = 20f;

    /// <summary>
    /// Jump strength (sv_jump_impulse) in zombies. CS2's normal value is 301.993377; lower means lower jumps, so players
    /// can't reach spots that need a crouch-jump to camp on. 270 is about 11 units lower than a normal jump.
    /// </summary>
    public float JumpImpulse { get; set; } = 270f;
}
