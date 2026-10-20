using System.Text.Json.Serialization;

namespace FragLab.Modes.Retake;

public sealed class RetakeConfig
{
    public string Config { get; set; } = "fraglab/retake.cfg";

    /// <summary>Warmup after retakes start or the map changes, so everyone can pick a team. 0 = none.</summary>
    public int WarmupSeconds { get; set; } = 15;

    /// <summary>Players beyond this wait in the queue.</summary>
    public int MaxPlayers { get; set; } = 9;

    /// <summary>Share of active players placed on T (0.45 gives 4T/5CT with 9 players).</summary>
    public double TerroristRatio { get; set; } = 0.45;

    /// <summary>Plant the bomb automatically when freeze time ends. Otherwise the planter gets the C4.</summary>
    public bool AutoPlant { get; set; } = true;

    /// <summary>Scramble the teams after the Ts win this many rounds in a row. 0 disables.</summary>
    public int ScrambleAfterTWinStreak { get; set; } = 5;

    /// <summary>Round order: this many pistol rounds, then force-buy rounds, then full buys for the rest.</summary>
    public int PistolRounds { get; set; } = 5;

    public int ForceBuyRounds { get; set; } = 5;

    /// <summary>
    /// Let players buy (for free) from the buy menu, limited to what the round type allows:
    /// pistols only in pistol rounds, no rifles/snipers/MGs in force buys. The last purchase is remembered.
    /// </summary>
    public bool AllowBuying { get; set; } = true;

    /// <summary>Buying is only possible during freeze time, not once the round is running.</summary>
    public bool BuyOnlyInFreezeTime { get; set; } = true;

    /// <summary>Grenades each player gets per round, picked at random from their team's list (CTs only get smokes).</summary>
    public int UtilityPerPlayer { get; set; } = 1;

    /// <summary>Pistol rounds: armor only with the default pistol (Glock/USP-S/P2000), like a real pistol round.</summary>
    public bool PistolRoundArmorOnlyWithDefaultPistol { get; set; } = true;

    /// <summary>Weapons that can't be bought or handed out. Default: AWP, auto-snipers and shotguns.</summary>
    public List<string> DisabledWeapons { get; set; } =
        ["weapon_awp", "weapon_g3sg1", "weapon_scar20", "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7"];

    /// <summary>End the round as a T win when no living CT can finish a defuse before the bomb explodes.</summary>
    public bool EndRoundWhenDefuseImpossible { get; set; } = true;

    /// <summary>Open all doors every round and lock them open (doors are not broken).</summary>
    public bool OpenDoors { get; set; } = true;

    /// <summary>Break all windows and other breakable objects (not doors) every round.</summary>
    public bool BreakWindows { get; set; } = true;

    /// <summary>CTs don't spawn closer than this (units) to the bomb when enough other spawns exist. 0 = off.</summary>
    public float CtMinDistanceFromBomb { get; set; } = 1000f;

    /// <summary>Defuse instantly when no Terrorists are alive and no molotov/incendiary is burning near the bomb.</summary>
    public bool InstantDefuse { get; set; } = true;

    /// <summary>Burning molotovs within this distance (units) of the bomb block instant defuse.</summary>
    public float InstantDefuseMolotovRadius { get; set; } = 400f;

    /// <summary>Chance that a random player gets the AWP in a full-buy round when nobody on the team bought one before.</summary>
    public double AwpChance { get; set; } = 0.5;

    public int MaxAwpsPerTeam { get; set; } = 1;

    /// <summary>The AWP is on unless it's in DisabledWeapons or MaxAwpsPerTeam is 0.</summary>
    [JsonIgnore]
    public bool AwpEnabled => MaxAwpsPerTeam > 0 && !DisabledWeapons.Contains("weapon_awp");

    /// <summary>Smoke grenades are never given to Terrorists, regardless of TGrenades.</summary>
    public List<string> TGrenades { get; set; } =
        ["weapon_flashbang", "weapon_hegrenade", "weapon_molotov"];

    public List<string> CTGrenades { get; set; } =
        ["weapon_flashbang", "weapon_smokegrenade", "weapon_hegrenade", "weapon_incgrenade"];

    public RoundLoadout Pistol { get; set; } = new()
    {
        T = new TeamLoadout { Secondaries = ["weapon_glock"], Armor = "item_kevlar" },
        CT = new TeamLoadout { Secondaries = ["weapon_usp_silencer", "weapon_hkp2000"], Armor = "item_kevlar", Defuser = true }
    };

    public RoundLoadout ForceBuy { get; set; } = new()
    {
        T = new TeamLoadout { Primaries = ["weapon_mac10", "weapon_ump45"], Secondaries = ["weapon_glock", "weapon_p250"], Armor = "item_assaultsuit" },
        CT = new TeamLoadout { Primaries = ["weapon_mp9", "weapon_ump45"], Secondaries = ["weapon_usp_silencer", "weapon_p250"], Armor = "item_assaultsuit", Defuser = true }
    };

    public RoundLoadout FullBuy { get; set; } = new()
    {
        T = new TeamLoadout { Primaries = ["weapon_ak47"], Secondaries = ["weapon_glock", "weapon_deagle"], Armor = "item_assaultsuit" },
        CT = new TeamLoadout { Primaries = ["weapon_m4a1_silencer", "weapon_m4a1"], Secondaries = ["weapon_usp_silencer", "weapon_deagle"], Armor = "item_assaultsuit", Defuser = true }
    };
}

public sealed class RoundLoadout
{
    public TeamLoadout T { get; set; } = new();
    public TeamLoadout CT { get; set; } = new();
}

public sealed class TeamLoadout
{
    /// <summary>Default primaries, one picked at random when the player has no saved purchase. Empty for none.</summary>
    public List<string> Primaries { get; set; } = [];

    public List<string> Secondaries { get; set; } = [];

    /// <summary>"item_kevlar", "item_assaultsuit" or empty.</summary>
    public string Armor { get; set; } = "";

    /// <summary>CT only.</summary>
    public bool Defuser { get; set; }
}
