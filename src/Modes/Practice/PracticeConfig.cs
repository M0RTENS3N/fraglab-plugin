namespace FragLab.Modes.Practice;

public sealed class PracticeConfig
{
    public string Config { get; set; } = "fraglab/prac.cfg";

    /// <summary>Competitive round settings used by !dryrun.</summary>
    public string DryRunConfig { get; set; } = "fraglab/prac_dryrun.cfg";

    /// <summary>Kick practice bots when a dry run starts (they would move and shoot without bot_stop).</summary>
    public bool DryRunKicksBots { get; set; } = true;

    /// <summary>Everyone spawns with every grenade in practice (infinite ammo keeps them topped up).</summary>
    public bool GiveAllGrenades { get; set; } = true;

    /// <summary>
    /// Nade finder, Normal accuracy: a lineup counts when it lands within this many units of the chosen spot.
    /// Always kept between 5 and 50 (Precise is 10, Exact 5, Loose 50).
    /// </summary>
    public float NadeFinderTolerance { get; set; } = 25f;
}
