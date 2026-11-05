using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Scrim;

public enum ScrimState
{
    Warmup,
    Knife,
    KnifeDecision,
    Live,
    Ended
}

/// <summary>
/// Competitive match flow: warmup with ready-up, optional knife round, live match
/// with pauses, demo recording and round backups.
/// </summary>
public sealed class ScrimMode : GameMode
{
    private readonly HashSet<ulong> _ready = new();
    private readonly Dictionary<int, CsTeam> _coaches = new(); // player slot -> coached team
    private ScrimState _state = ScrimState.Warmup;
    private CsTeam _knifeWinner = CsTeam.None;
    private bool _paused;
    private CsTeam _tacticalTeam = CsTeam.None; // team whose tactical timeout is running (it alone ends it)
    private readonly Dictionary<CsTeam, int> _timeoutsUsed = new();
    private bool _tWantsUnpause;
    private bool _ctWantsUnpause;
    private bool _recording;
    private string? _demoPath;     // full path of the demo being (or last) recorded
    private string? _demoSummary;  // match result posted with it
    private Timer? _reminderTimer;
    private Timer? _knifeDecisionTimer;
    private Timer? _resetTimer;

    public ScrimMode(FragLabPlugin plugin) : base(plugin) { }

    public override string Name => "scrim";
    public override string Description => "Ready-up, knife round, pauses and demo recording";

    private ScrimConfig Cfg => Plugin.Config.Scrim;

    public override bool ShowDamageReport => _state == ScrimState.Live;

    public override void Register()
    {
        AddCommand(["ready", "r", "rdy"], "Mark yourself as ready", OnReady);
        AddCommand(["unready", "ur", "notready"], "Mark yourself as not ready", OnUnready);
        AddCommand(["stay"], "Knife winners: keep current sides", (p, _) => OnKnifeChoice(p, swap: false));
        AddCommand(["switch", "swap"], "Knife winners: switch sides", (p, _) => OnKnifeChoice(p, swap: true));
        AddCommand(["pause", "p"], "Pause the match at the next freeze time", (p, i) => OnPause(p, technical: false));
        AddCommand(["tech", "technical"], "Technical pause at the next freeze time (both teams !unpause)", (p, i) => OnPause(p, technical: true));
        AddCommand(["setwebhook"], "Set the Discord webhook for demos: /setwebhook <url> (or off). Use / or the console so it isn't shown in chat", OnSetWebhook, adminOnly: true);
        AddCommand(["unpause", "up"], "Request to unpause the match", OnUnpause);
        AddCommand(["tac", "timeout"], "Tactical timeout for your team, no time limit: your team types !unpause to end it", OnTacticalTimeout);
        AddCommand(["coach"], "Coach a team: !coach <ct|t>", OnCoachCommand);
        AddCommand(["uncoach"], "Stop coaching", (p, _) => StopCoaching(p));

        AddCommand(["forcestart", "start"], "Start the match without waiting for ready", (p, _) => ForceStart(p), adminOnly: true);
        AddCommand(["forceunpause", "fup"], "Unpause the match immediately", (_, _) => Unpause(), adminOnly: true);
        AddCommand(["skipknife"], "Skip the knife round and go live", (p, _) => SkipKnife(p), adminOnly: true);
        AddCommand(["kniferound"], "Turn the knife round on or off: !kniferound <on|off>", OnKnifeRoundCommand, adminOnly: true);
        AddCommand(["resetmatch"], "Abort the match and return to warmup", (_, _) =>
        {
            Chat.All("{red}Match reset by an admin.");
            EnterWarmup();
        }, adminOnly: true);
        AddCommand(["restore"], "Restore a round backup: !restore <round>", OnRestore, adminOnly: true);
        AddCommand(["teamnames"], "Set team names: !teamnames <CT name> <T name>", OnTeamNames, adminOnly: true);
        AddCommand(["warm", "warmup"], "Warmup menu: retake, deathmatch or back to the scrim warmup", (p, _) =>
        {
            if (_state != ScrimState.Warmup)
                Chat.To(p, "The warmup menu is only available during the scrim warmup.");
            else
                WarmupMenu.Open(Plugin, p);
        });
        AddCommand(["uploaddemo"], "Send the last scrim demo to Discord again", (_, _) => ResendDemo(), adminOnly: true);
    }

    public override void Activate() => EnterWarmup();

    public override void Deactivate()
    {
        KillTimers();
        StopDemo();
        if (_paused)
            Unpause();
        _ready.Clear();
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
    }

    public override void OnMapStart(string mapName)
    {
        _coaches.Clear();
        // Timers flagged STOP_ON_MAPCHANGE are already gone; drop stale references.
        _reminderTimer = null;
        _knifeDecisionTimer = null;
        _resetTimer = null;
        _recording = false;
        _state = ScrimState.Warmup;
    }

    public override void PrintStatus(CCSPlayerController? player)
    {
        Chat.To(player, $"Scrim state: {{green}}{_state}{{default}}");
        if (_state == ScrimState.Warmup)
            Chat.To(player, $"Ready: {{green}}{ReadyCount()}/{RequiredCount()}{{default}}");
        if (_paused)
            Chat.To(player, $"Paused. Unpause requested by T: {YesNo(_tWantsUnpause)}, CT: {YesNo(_ctWantsUnpause)}");
    }

    // ---------------------------------------------------------------- warmup / ready

    private void EnterWarmup()
    {
        KillTimers();
        StopDemo();
        _state = ScrimState.Warmup;
        _ready.Clear();
        _knifeWinner = CsTeam.None;
        ResetPause();

        ExecConfig(Cfg.WarmupConfig);
        Server.ExecuteCommand("mp_warmup_start");

        if (Cfg.ReadyReminderSeconds > 0)
            _reminderTimer = Plugin.AddTimer(Cfg.ReadyReminderSeconds, RemindReady,
                TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Chat.All("Warmup. Type {green}!ready{default} when you are ready to play.");
    }

    private void OnReady(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || _state != ScrimState.Warmup)
            return;

        if (!Players.IsPlayingTeam(player.Team))
        {
            Chat.To(player, "Join a team before readying up.");
            return;
        }

        if (!_ready.Add(player.SteamID))
        {
            Chat.To(player, "You are already ready.");
            return;
        }

        Chat.All($"{{green}}{player.PlayerName}{{default}} is ready. ({ReadyCount()}/{RequiredCount()})");
        TryStart();
    }

    private void OnUnready(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || _state != ScrimState.Warmup)
            return;

        if (_ready.Remove(player.SteamID))
            Chat.All($"{{red}}{player.PlayerName}{{default}} is no longer ready. ({ReadyCount()}/{RequiredCount()})");
    }

    public override void OnPlayerDisconnect(CCSPlayerController player)
    {
        _coaches.Remove(player.Slot);
        if (_state == ScrimState.Warmup && _ready.Remove(player.SteamID))
            Chat.All($"{player.PlayerName} left and was unreadied.");
    }

    public override void OnPlayerTeam(CCSPlayerController player, int oldTeam, int newTeam)
    {
        // A coach follows their team when sides swap at halftime, and stops coaching when going to spectator.
        if (_coaches.ContainsKey(player.Slot))
        {
            if (Players.IsPlayingTeam((CsTeam)newTeam))
                _coaches[player.Slot] = (CsTeam)newTeam;
            else
                _coaches.Remove(player.Slot);
        }
        if (_state == ScrimState.Warmup && !Players.IsPlayingTeam((CsTeam)newTeam) && _ready.Remove(player.SteamID))
            Chat.To(player, "You moved to spectator and were unreadied.");
    }

    /// <summary>Human players on T/CT. Bots and coaches never need to ready up.</summary>
    private List<CCSPlayerController> TeamHumans() =>
        Players.Humans().Where(p => Players.IsPlayingTeam(p.Team) && !_coaches.ContainsKey(p.Slot)).ToList();

    private int ReadyCount() => TeamHumans().Count(p => _ready.Contains(p.SteamID));

    private int RequiredCount() => Math.Max(Math.Max(1, Cfg.MinPlayersToStart), TeamHumans().Count);

    private void TryStart()
    {
        var humans = TeamHumans();
        if (humans.Count < Math.Max(1, Cfg.MinPlayersToStart))
            return;
        if (humans.Any(p => !_ready.Contains(p.SteamID)))
            return;

        Chat.All("{green}All players are ready!");
        StartMatch();
    }

    private void RemindReady()
    {
        if (_state != ScrimState.Warmup)
            return;

        var notReady = TeamHumans().Where(p => !_ready.Contains(p.SteamID)).Select(p => p.PlayerName).ToList();
        Chat.All($"Ready: {{green}}{ReadyCount()}/{RequiredCount()}{{default}}. Type {{green}}!ready{{default}} to start.");
        if (notReady.Count > 0)
            Chat.All($"Not ready: {{red}}{string.Join(", ", notReady)}");
    }

    private void ForceStart(CCSPlayerController? admin)
    {
        if (_state != ScrimState.Warmup)
        {
            Chat.To(admin, "The match has already started.");
            return;
        }

        Chat.All("{red}Match force-started by an admin.");
        StartMatch();
    }

    private void StartMatch()
    {
        _reminderTimer?.Kill();
        _reminderTimer = null;

        if (Cfg.KnifeRound)
            StartKnife();
        else
            GoLive();
    }

    // ---------------------------------------------------------------- knife round

    private void StartKnife()
    {
        _state = ScrimState.Knife;
        ExecConfig(Cfg.KnifeConfig);
        // Set explicitly so round times left over from other modes (retake uses 30s rounds) can't leak in.
        var minutes = Math.Max(0.5f, Cfg.KnifeRoundMinutes).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Server.ExecuteCommand($"mp_roundtime {minutes}; mp_roundtime_defuse {minutes}; mp_roundtime_hostage {minutes}");
        Server.ExecuteCommand("mp_warmup_end");
        Chat.All("{gold}KNIFE ROUND!{default} The winners choose their side.");
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (_coaches.ContainsKey(player.Slot) && _state is ScrimState.Knife or ScrimState.Live)
        {
            Server.NextFrame(() => BenchCoach(player));
            return;
        }

        if (_state != ScrimState.Knife)
            return;

        // Default weapons are handed out during spawn; strip them a frame later.
        Server.NextFrame(() =>
        {
            if (!Players.IsValid(player) || !player.PawnIsAlive)
                return;
            player.RemoveWeapons();
            player.GiveNamedItem("weapon_knife");
        });
    }

    public override void OnRoundEnd(int winner, int reason)
    {
        if (_state != ScrimState.Knife)
            return;

        // Restarts (e.g. ending warmup) fire round_end without a winning team; ignore those.
        var team = (CsTeam)winner;
        if (!Players.IsPlayingTeam(team))
            return;

        _knifeWinner = team;
        _state = ScrimState.KnifeDecision;

        Chat.All($"{Players.TeamName(team)} won the knife round! Type {{green}}!stay{{default}} or {{green}}!switch{{default}}.");

        // Freeze the game in warmup while the winners decide.
        Plugin.AddTimer(1.0f, () =>
        {
            if (_state != ScrimState.KnifeDecision)
                return;
            ExecConfig(Cfg.WarmupConfig);
            Server.ExecuteCommand("mp_warmup_start");
            Server.ExecuteCommand("mp_warmup_pausetimer 1");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        if (Cfg.KnifeDecisionSeconds > 0)
        {
            _knifeDecisionTimer = Plugin.AddTimer(Cfg.KnifeDecisionSeconds, () =>
            {
                _knifeDecisionTimer = null;
                if (_state != ScrimState.KnifeDecision)
                    return;
                Chat.All("No decision made, staying on current sides.");
                GoLive();
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private void OnKnifeChoice(CCSPlayerController? player, bool swap)
    {
        if (_state != ScrimState.KnifeDecision)
        {
            Chat.To(player, "There is no side choice to make right now.");
            return;
        }

        var isAdmin = Players.IsAdmin(player, Plugin.Config.AdminFlag);
        if (player != null && player.Team != _knifeWinner && !isAdmin)
        {
            Chat.To(player, "Only the knife round winners can choose.");
            return;
        }

        if (swap)
        {
            Server.ExecuteCommand("mp_swapteams");
            Chat.All($"{Players.TeamName(_knifeWinner)} chose to {{green}}switch{{default}} sides.");
        }
        else
        {
            Chat.All($"{Players.TeamName(_knifeWinner)} chose to {{green}}stay{{default}}.");
        }

        GoLive();
    }

    private void SkipKnife(CCSPlayerController? admin)
    {
        if (_state is not (ScrimState.Warmup or ScrimState.Knife or ScrimState.KnifeDecision))
        {
            Chat.To(admin, "The match is already live.");
            return;
        }

        Chat.All("{red}Knife round skipped by an admin.");
        if (_state == ScrimState.Knife)
        {
            // Knife round is being played; reset back through warmup so the live restart is clean.
            Server.ExecuteCommand("mp_warmup_start");
        }
        GoLive();
    }

    // ---------------------------------------------------------------- live

    private void GoLive()
    {
        KillTimers();
        _state = ScrimState.Live;
        ResetPause();
        _timeoutsUsed.Clear();

        ExecConfig(Cfg.LiveConfig);
        // Ending warmup restarts the game, which also clears the knife round result.
        Plugin.AddTimer(0.5f, () => Server.ExecuteCommand("mp_warmup_end"), TimerFlags.STOP_ON_MAPCHANGE);

        Plugin.AddTimer(3.0f, () =>
        {
            if (_state != ScrimState.Live)
                return;
            StartDemo();
            for (var i = 0; i < 3; i++)
                Chat.All("{green}LIVE! LIVE! LIVE!{default} Good luck, have fun!");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    public override void OnMatchEnd()
    {
        if (_state != ScrimState.Live)
            return;

        _state = ScrimState.Ended;
        var recorded = _recording;
        StopDemo();
        Chat.All("{green}Match over!{default} GG.");
        if (recorded)
        {
            _demoSummary = MatchSummary();
            UploadDemo();
        }

        if (Cfg.ResetToWarmupAfterSeconds > 0)
        {
            _resetTimer = Plugin.AddTimer(Cfg.ResetToWarmupAfterSeconds, () =>
            {
                _resetTimer = null;
                if (_state == ScrimState.Ended)
                    EnterWarmup();
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    // ---------------------------------------------------------------- pauses

    private void OnPause(CCSPlayerController? player, bool technical)
    {
        if (_state != ScrimState.Live)
        {
            Chat.To(player, "You can only pause a live match.");
            return;
        }

        if (_paused)
        {
            Chat.To(player, "The match is already paused.");
            return;
        }

        _paused = true;
        _tWantsUnpause = false;
        _ctWantsUnpause = false;
        Server.ExecuteCommand("mp_pause_match");

        var who = player == null ? "Console" : $"{player.PlayerName} ({Players.TeamName(player.Team)})";
        Chat.All(technical
            ? $"{{red}}Technical pause{{default}} by {who}. It starts at the next freeze time."
            : $"{who} paused the match. It will pause at the next freeze time.");
        if (Cfg.RequireBothTeamsToUnpause)
            Chat.All("Both teams must type {green}!unpause{default} to continue.");
    }

    private void OnUnpause(CCSPlayerController? player, CommandInfo info)
    {
        if (!_paused)
        {
            Chat.To(player, "The match is not paused.");
            return;
        }

        // A tactical timeout ends when the team that called it is ready (or the console/an admin says so).
        if (_tacticalTeam != CsTeam.None)
        {
            if (player == null || player.Team == _tacticalTeam || Players.IsAdmin(player, Plugin.Config.AdminFlag))
            {
                Unpause();
                return;
            }
            Chat.To(player, $"Only {Players.TeamName(_tacticalTeam)} can end their tactical timeout.");
            return;
        }

        if (player == null || !Cfg.RequireBothTeamsToUnpause)
        {
            Unpause();
            return;
        }

        switch (player.Team)
        {
            case CsTeam.Terrorist:
                _tWantsUnpause = true;
                break;
            case CsTeam.CounterTerrorist:
                _ctWantsUnpause = true;
                break;
            default:
                Chat.To(player, "Only players can unpause.");
                return;
        }

        if (_tWantsUnpause && _ctWantsUnpause)
        {
            Unpause();
            return;
        }

        var waitingFor = _tWantsUnpause ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        Chat.All($"{Players.TeamName(player.Team)} is ready to continue. Waiting for {Players.TeamName(waitingFor)} to type {{green}}!unpause{{default}}.");
    }

    private void Unpause()
    {
        ResetPause();
        Server.ExecuteCommand("mp_unpause_match");
        Chat.All("{green}Match unpaused!");
    }

    private void ResetPause()
    {
        _paused = false;
        _tWantsUnpause = false;
        _ctWantsUnpause = false;
        _tacticalTeam = CsTeam.None;
    }

    private void OnTacticalTimeout(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || _state != ScrimState.Live || !Players.IsPlayingTeam(player.Team))
            return;

        if (_paused)
        {
            Chat.To(player, "The match is already paused.");
            return;
        }

        var team = player.Team;
        var used = _timeoutsUsed.GetValueOrDefault(team);
        if (Cfg.TacticalTimeoutsPerTeam > 0 && used >= Cfg.TacticalTimeoutsPerTeam)
        {
            Chat.To(player, $"Your team has used all {Cfg.TacticalTimeoutsPerTeam} tactical timeouts.");
            return;
        }

        // A normal pause instead of the game's timeout: it has no time limit.
        _timeoutsUsed[team] = used + 1;
        _paused = true;
        _tacticalTeam = team;
        _tWantsUnpause = false;
        _ctWantsUnpause = false;
        Server.ExecuteCommand("mp_pause_match");

        var left = Cfg.TacticalTimeoutsPerTeam > 0 ? $" ({Cfg.TacticalTimeoutsPerTeam - used - 1} left)" : "";
        Chat.All($"{Players.TeamName(team)} called a {{green}}tactical timeout{{default}}{left}. It starts at the next freeze time, " +
                 $"no time limit: {Players.TeamName(team)} types {{green}}!unpause{{default}} when ready.");
    }

    private void OnSetWebhook(CCSPlayerController? player, CommandInfo info)
    {
        var url = info.ArgString.Trim().Trim('"');
        if (url.Equals("off", StringComparison.OrdinalIgnoreCase))
            url = "";
        else if (!url.StartsWith("https://discord.com/api/webhooks/") && !url.StartsWith("https://discordapp.com/api/webhooks/"))
        {
            Chat.To(player, "Usage: /setwebhook <https://discord.com/api/webhooks/...> or /setwebhook off");
            return;
        }

        Cfg.DiscordWebhookUrl = url;
        Plugin.SaveConfig();
        Chat.To(player, url.Length == 0 ? "Discord demo upload turned off." : "Discord webhook saved. Demos are posted there when a scrim ends.");
    }

    // ---------------------------------------------------------------- coaches

    private void OnCoachCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null)
            return;

        var arg = info.ArgCount > 1 ? info.GetArg(1).ToLowerInvariant() : "";
        var team = arg switch
        {
            "ct" => CsTeam.CounterTerrorist,
            "t" => CsTeam.Terrorist,
            _ => CsTeam.None
        };
        if (team == CsTeam.None)
        {
            Chat.To(player, "Usage: !coach <ct|t>  (!uncoach to stop)");
            return;
        }

        if (_coaches.Any(c => c.Value == team && c.Key != player.Slot))
        {
            Chat.To(player, $"{Players.TeamName(team)} already has a coach.");
            return;
        }

        _ready.Remove(player.SteamID);
        _coaches[player.Slot] = team;
        if (player.Team != team)
            player.ChangeTeam(team);

        Chat.All($"{{green}}{player.PlayerName}{{default}} is now coaching {Players.TeamName(team)}.");
        Chat.To(player, "During the knife round and the match you'll spectate your team and can talk to them. !uncoach to play again.");
    }

    private void StopCoaching(CCSPlayerController? player)
    {
        if (player == null || !_coaches.Remove(player.Slot))
        {
            Chat.To(player, "You are not coaching.");
            return;
        }

        Chat.All($"{player.PlayerName} stopped coaching.");
        if (_state == ScrimState.Warmup)
            Chat.To(player, "Type {green}!ready{default} when you're ready to play.");
    }

    /// <summary>A coach spawned for a knife/live round: hand over the bomb if they got it, then take them out of the round.</summary>
    private void BenchCoach(CCSPlayerController coach)
    {
        if (!Players.IsValid(coach) || !coach.PawnIsAlive)
            return;

        var pawn = coach.PlayerPawn.Value;
        var hasBomb = pawn?.WeaponServices?.MyWeapons.Any(w => w.Value?.DesignerName == "weapon_c4") == true;
        if (hasBomb)
        {
            var teammate = Players.OnTeam(coach.Team)
                .Where(p => p.Slot != coach.Slot && p.PawnIsAlive && !_coaches.ContainsKey(p.Slot))
                .OrderBy(_ => Random.Shared.Next())
                .FirstOrDefault();
            teammate?.GiveNamedItem("weapon_c4");
        }

        coach.RemoveWeapons();
        KillCoach(coach);
    }

    /// <summary>
    /// Killing right at spawn doesn't always work, so the coach is hidden at once and the kill is retried
    /// until they're dead. Dead coaches can still talk to their team (sv_deadtalk).
    /// </summary>
    private void KillCoach(CCSPlayerController coach, int attempt = 0)
    {
        if (!Players.IsValid(coach) || !coach.PawnIsAlive || !_coaches.ContainsKey(coach.Slot))
            return;

        var pawn = coach.PlayerPawn.Value;
        if (pawn == null)
            return;

        if (attempt == 0)
        {
            pawn.RenderMode = RenderMode_t.kRenderNone;
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");

            // Out of sight under the map: without weapons the body T-poses in spawn until the kill goes through,
            // and its ragdoll would lie in spawn afterwards.
            if (pawn.AbsOrigin is { } origin)
                pawn.Teleport(new Vector(origin.X, origin.Y, origin.Z - 4000f), null, new Vector(0, 0, 0));
        }

        pawn.CommitSuicide(false, true);
        if (attempt < 10)
            Plugin.AddTimer(0.3f, () => KillCoach(coach, attempt + 1), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- admin tools

    private void OnRestore(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out var round) || round < 1)
        {
            Chat.To(player, "Usage: !restore <round number>  (1 = pistol round)");
            return;
        }

        if (_state != ScrimState.Live)
        {
            Chat.To(player, "Backups can only be restored during a live match.");
            return;
        }

        // CS2 numbers its backup files from 0 (round00 is the pistol round), rounds are counted from 1.
        Server.ExecuteCommand($"mp_backup_restore_load_file \"{Cfg.BackupFilePrefix}_round{round - 1:D2}.txt\"");
        _paused = true;
        _tWantsUnpause = false;
        _ctWantsUnpause = false;
        Server.ExecuteCommand("mp_pause_match");
        Chat.All($"{{red}}Round {round} restored by an admin.{{default}} Type {{green}}!unpause{{default}} when ready.");
    }

    private void OnKnifeRoundCommand(CCSPlayerController? player, CommandInfo info)
    {
        var arg = info.ArgCount > 1 ? info.GetArg(1).ToLowerInvariant() : "";
        bool? enable = arg switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => null
        };

        if (enable == null)
        {
            Chat.To(player, $"Knife round is {(Cfg.KnifeRound ? "{green}on" : "{red}off")}{{default}}. Usage: !kniferound <on|off>");
            return;
        }

        Cfg.KnifeRound = enable.Value;
        Plugin.SaveConfig();
        Chat.All($"Knife round {(enable.Value ? "{green}enabled" : "{red}disabled")}{{default}} by an admin.");

        // Turned off while the knife round is running or waiting for a side choice: go straight to live.
        if (!enable.Value && _state is ScrimState.Knife or ScrimState.KnifeDecision)
            SkipKnife(player);
    }

    private void OnTeamNames(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 3)
        {
            Chat.To(player, "Usage: !teamnames <CT name> <T name>");
            return;
        }

        // mp_teamname_1 is the team that starts CT.
        Server.ExecuteCommand($"mp_teamname_1 \"{Sanitize(info.GetArg(1))}\"");
        Server.ExecuteCommand($"mp_teamname_2 \"{Sanitize(info.GetArg(2))}\"");
        Chat.All($"Team names set: {{lightblue}}{info.GetArg(1)}{{default}} vs {{gold}}{info.GetArg(2)}");
    }

    // ---------------------------------------------------------------- demos

    private void StartDemo()
    {
        if (!Cfg.RecordDemos || _recording)
            return;

        try
        {
            Directory.CreateDirectory(Path.Combine(Server.GameDirectory, "csgo", Cfg.DemoDirectory));
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Could not create demo directory: {Message}", ex.Message);
        }

        // e.g. "NaVi_vs_FaZe_de_mirage_2026-10-20_21-30"; team names come from !teamnames (mp_teamname_1/2).
        var ct = DemoPart(ConVar.Find("mp_teamname_1")?.StringValue, "CT");
        var t = DemoPart(ConVar.Find("mp_teamname_2")?.StringValue, "T");
        var name = $"{Cfg.DemoDirectory}/{ct}_vs_{t}_{Server.MapName}_{DateTime.Now:yyyy-MM-dd_HH-mm}";
        Server.ExecuteCommand($"tv_record \"{name}\"");
        _recording = true;
        _demoPath = Path.Combine(Server.GameDirectory, "csgo", name + ".dem");
        Chat.All($"Recording demo: {{green}}{name}.dem");
    }

    /// <summary>"Scrim finished on de_mirage ..." with the team names and the final score.</summary>
    private static string MatchSummary()
    {
        var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager").ToList();
        int ScoreOf(int teamNum) => teams.FirstOrDefault(t => t.TeamNum == teamNum)?.Score ?? 0;

        var team1 = ConVar.Find("mp_teamname_1")?.StringValue;
        var team2 = ConVar.Find("mp_teamname_2")?.StringValue;
        var names = string.IsNullOrWhiteSpace(team1) && string.IsNullOrWhiteSpace(team2)
            ? ""
            : $"{(string.IsNullOrWhiteSpace(team1) ? "Team 1" : team1)} vs {(string.IsNullOrWhiteSpace(team2) ? "Team 2" : team2)}\n";

        return $"**Scrim finished on {Server.MapName}** ({DateTime.Now:yyyy-MM-dd HH:mm})\n" +
               $"{names}Final score: CT {ScoreOf(3)} : {ScoreOf(2)} T";
    }

    /// <summary>Sends the demo to Discord in the background, then reports the result in chat.</summary>
    private void UploadDemo()
    {
        if (string.IsNullOrWhiteSpace(Cfg.DiscordWebhookUrl) || _demoPath == null)
            return;

        var path = _demoPath;
        var summary = _demoSummary ?? $"**Scrim demo** from {Server.MapName}";
        var cfg = Cfg;
        Chat.All("Sending the demo to Discord...");
        Task.Run(async () =>
        {
            // Give the server a moment to finish writing the demo file.
            await Task.Delay(5000);
            var result = await DemoUploader.UploadAsync(path, summary, cfg);
            Server.NextFrame(() =>
            {
                Chat.All(result);
                Plugin.Logger.LogInformation("Demo upload: {Result}", result);
            });
        });
    }

    private void ResendDemo()
    {
        if (string.IsNullOrWhiteSpace(Cfg.DiscordWebhookUrl))
        {
            Chat.All("No Discord webhook set: add {green}Scrim.DiscordWebhookUrl{default} in the config.");
            return;
        }
        if (_demoPath == null || _recording)
        {
            Chat.All(_recording ? "The demo is still recording." : "No demo recorded since the server started.");
            return;
        }
        UploadDemo();
    }

    private void StopDemo()
    {
        if (!_recording)
            return;
        Server.ExecuteCommand("tv_stoprecord");
        _recording = false;
    }

    // ---------------------------------------------------------------- helpers

    private void KillTimers()
    {
        _reminderTimer?.Kill();
        _knifeDecisionTimer?.Kill();
        _resetTimer?.Kill();
        _reminderTimer = null;
        _knifeDecisionTimer = null;
        _resetTimer = null;
    }

    private static string Sanitize(string value) => value.Replace("\"", "").Replace(";", "");

    /// <summary>A team name safe for a file name; falls back to the side name when unset.</summary>
    private static string DemoPart(string? teamName, string fallback)
    {
        var clean = new string((teamName ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ' ').ToArray());
        return clean.Length == 0 ? fallback : clean;
    }

    private static string YesNo(bool value) => value ? "{green}yes{default}" : "{red}no{default}";
}
