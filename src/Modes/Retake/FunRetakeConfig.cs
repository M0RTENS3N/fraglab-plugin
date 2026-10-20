namespace FragLab.Modes.Retake;

/// <summary>
/// Fun retake (!mode funretake): normal retakes (same spawns, rounds and buying as the Retake section),
/// plus a random perk for every player each round, revealed with a roulette. Sometimes a map event runs.
/// </summary>
public sealed class FunRetakeConfig
{
    /// <summary>
    /// Perks that can be rolled. Rarities are set in code (RetakeMode.Perks.cs).
    /// Common: Vampire, Regeneration, LowGravity, Fireman, Gunslinger, Pyromaniac, Glasses (Sunglasses).
    /// Rare: Speed, Jetpack, MCHammer, Bulletproof, Bhop, GlassCannon.
    /// Epic: Tank, Ghost, Ninja (CT only), Enderman, LuckyCharm (Totem of Undying), Creeper, Clone (T only).
    /// Legendary: Wallhack, HSKing, Radar, Aimbot (Golden Shot), Phoenix, SuperZeus.
    /// </summary>
    public List<string> Perks { get; set; } =
    [
        "Vampire", "Regeneration", "LowGravity", "Fireman", "Gunslinger", "Pyromaniac", "Glasses",
        "Speed", "Jetpack", "MCHammer", "Bulletproof", "Bhop", "GlassCannon",
        "Tank", "Ghost", "Ninja", "Enderman", "LuckyCharm", "Creeper", "Clone",
        "Wallhack", "HSKing", "Radar", "Aimbot", "Phoenix", "SuperZeus"
    ];

    /// <summary>Chance (in %) of each rarity. A rarity is rolled first, then a perk of that rarity.</summary>
    public int CommonChance { get; set; } = 40;
    public int RareChance { get; set; } = 30;
    public int EpicChance { get; set; } = 20;
    public int LegendaryChance { get; set; } = 10;

    /// <summary>Chance (in %) per player that the round gives a DOUBLE PERK: two perks combined.</summary>
    public int DoublePerkChance { get; set; } = 5;

    /// <summary>Chance (in %) that a round gets a map event. Most rounds have none.</summary>
    public int EventChance { get; set; } = 20;

    /// <summary>Fog event: distance where the fog starts and where it is fully thick.</summary>
    public float FogStart { get; set; } = 150f;
    public float FogEnd { get; set; } = 900f;

    /// <summary>Moon gravity event: gravity while it runs (normal is 800).</summary>
    public float MoonGravity { get; set; } = 300f;

    /// <summary>Slippery ice event: ground friction (normal is 5.2).</summary>
    public float SlipperyFriction { get; set; } = 0.3f;

    /// <summary>Turbo event: acceleration (normal is 5.5).</summary>
    public float TurboAccelerate { get; set; } = 14f;

    /// <summary>How long the perk roulette spins before showing your perk.</summary>
    public float RouletteSeconds { get; set; } = 2.5f;

    public int TankHealth { get; set; } = 200;

    public int VampireHealPerKill { get; set; } = 35;
    public int VampireMaxHealth { get; set; } = 150;

    public int RegenPerSecond { get; set; } = 4;

    /// <summary>Speed: a random multiplier between these two each round.</summary>
    public float SpeedMin { get; set; } = 1.3f;
    public float SpeedMax { get; set; } = 2.0f;

    /// <summary>LowGravity: share of normal gravity (0.5 = half).</summary>
    public float LowGravity { get; set; } = 0.5f;

    /// <summary>Ghost: how visible the player model is (0 = invisible, 255 = normal).</summary>
    public int GhostAlpha { get; set; } = 70;

    /// <summary>Jetpack: seconds of thrust per jump (refills on the ground) and the top upward speed.</summary>
    public float JetpackFuelSeconds { get; set; } = 3f;
    public float JetpackMaxUpSpeed { get; set; } = 320f;

    /// <summary>MCHammer: damage per second while standing still after freeze time.</summary>
    public float HammerDamagePerSecond { get; set; } = 10f;

    /// <summary>Enderman: seconds before you get a new decoy (ender pearl) after teleporting.</summary>
    public float EndermanCooldownSeconds { get; set; } = 6f;

    /// <summary>Golden Shot (Aimbot): every bullet deals this much more damage.</summary>
    public float AimbotDamageMultiplier { get; set; } = 10f;

    /// <summary>Bulletproof: share of bullet damage that still gets through.</summary>
    public float BulletproofMultiplier { get; set; } = 0.5f;

    /// <summary>Pyromaniac: multiplier for fire damage you deal.</summary>
    public float PyromaniacMultiplier { get; set; } = 2f;

    /// <summary>Creeper: explosion radius (units) and damage at the centre (falls off with distance).</summary>
    public float CreeperRadius { get; set; } = 300f;
    public float CreeperDamage { get; set; } = 120f;

    /// <summary>Phoenix: seconds until you respawn after dying (once per round).</summary>
    public float PhoenixRespawnSeconds { get; set; } = 3f;

    /// <summary>Totem of Undying: health given back after the saved deadly hit.</summary>
    public int TotemHealth { get; set; } = 30;

    /// <summary>Clone: seconds of wallhack you get when an enemy shoots your clone.</summary>
    public float CloneWallhackSeconds { get; set; } = 5f;
}
