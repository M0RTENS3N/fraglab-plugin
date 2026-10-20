namespace FragLab.Modes.Scrim;

public sealed class ScrimConfig
{
    /// <summary>Minimum number of ready human players before the match starts (e.g. 10 for 5v5, 4 for 2v2).</summary>
    public int MinPlayersToStart { get; set; } = 10;

    public bool KnifeRound { get; set; } = true;

    /// <summary>Length of the knife round in minutes.</summary>
    public float KnifeRoundMinutes { get; set; } = 2f;

    /// <summary>Seconds the knife round winners get to pick !stay or !switch before defaulting to stay. 0 = wait forever.</summary>
    public int KnifeDecisionSeconds { get; set; } = 120;

    /// <summary>Both teams must type !unpause before a pause ends.</summary>
    public bool RequireBothTeamsToUnpause { get; set; } = true;

    /// <summary>Requires tv_enable 1 to be set before the map loads (e.g. in server.cfg).</summary>
    public bool RecordDemos { get; set; } = true;

    /// <summary>Relative to the csgo directory.</summary>
    public string DemoDirectory { get; set; } = "demos";

    public int ReadyReminderSeconds { get; set; } = 30;

    /// <summary>Seconds after the final scoreboard before the server goes back to warmup. 0 disables.</summary>
    public int ResetToWarmupAfterSeconds { get; set; } = 30;

    /// <summary>Must match mp_backup_round_file in the live config.</summary>
    public string BackupFilePrefix { get; set; } = "fraglab_backup";

    public string WarmupConfig { get; set; } = "fraglab/scrim_warmup.cfg";
    public string KnifeConfig { get; set; } = "fraglab/scrim_knife.cfg";
    public string LiveConfig { get; set; } = "fraglab/scrim_live.cfg";
}
