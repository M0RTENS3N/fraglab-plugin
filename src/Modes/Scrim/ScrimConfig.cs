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

    /// <summary>Tactical timeouts (!tac) per team per match; they have no time limit. 0 = unlimited.</summary>
    public int TacticalTimeoutsPerTeam { get; set; } = 3;

    /// <summary>Deathmatch during the scrim warmup (toggled with !warm). Stays on for the next scrim.</summary>
    public bool WarmupDeathmatch { get; set; } = false;

    /// <summary>Retake during the scrim warmup (toggled with !warm). Players ready up as usual. Stays on for the next scrim.</summary>
    public bool WarmupRetake { get; set; } = false;

    /// <summary>Cloudflare R2 bucket for demos (leave empty to not use it). Links are public and expire by a lifecycle rule.</summary>
    public string R2AccountId { get; set; } = "";
    public string R2Bucket { get; set; } = "";
    public string R2AccessKeyId { get; set; } = "";
    public string R2SecretAccessKey { get; set; } = "";
    /// <summary>Public address of the bucket, e.g. https://pub-xxxx.r2.dev</summary>
    public string R2PublicBaseUrl { get; set; } = "";

    /// <summary>Requires tv_enable 1 to be set before the map loads (e.g. in server.cfg).</summary>
    public bool RecordDemos { get; set; } = true;

    /// <summary>Relative to the csgo directory.</summary>
    public string DemoDirectory { get; set; } = "demos";

    /// <summary>Discord webhook that gets the demo when a scrim ends. Empty = no upload.</summary>
    public string DiscordWebhookUrl { get; set; } = "";

    /// <summary>Discord's upload limit for the webhook's server: 10 MB, or 50/100 MB with boosts.</summary>
    public float DiscordMaxUploadMb { get; set; } = 10f;

    /// <summary>Zip the demo before uploading (demos shrink a lot).</summary>
    public bool ZipDemoForDiscord { get; set; } = true;

    /// <summary>
    /// Optional download link posted with the demo, e.g. "https://example.com/demos/{file}" ({file} = the demo's file name).
    /// Used when the demo is too big to attach.
    /// </summary>
    public string DemoDownloadUrl { get; set; } = "";

    public int ReadyReminderSeconds { get; set; } = 30;

    /// <summary>Seconds after the final scoreboard before the server goes back to warmup. 0 disables.</summary>
    public int ResetToWarmupAfterSeconds { get; set; } = 30;

    /// <summary>Must match mp_backup_round_file in the live config.</summary>
    public string BackupFilePrefix { get; set; } = "fraglab_backup";

    public string WarmupConfig { get; set; } = "fraglab/scrim_warmup.cfg";
    public string KnifeConfig { get; set; } = "fraglab/scrim_knife.cfg";
    public string LiveConfig { get; set; } = "fraglab/scrim_live.cfg";
}
