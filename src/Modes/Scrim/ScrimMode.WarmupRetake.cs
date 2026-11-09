using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using FragLab.Core;

namespace FragLab.Modes.Scrim;

/// <summary>
/// Retake during the scrim warmup. The scrim is parked (its ready list and state are kept) while retake runs as the
/// active mode. Ready-up (!ready) or !forcestart in retake hands back to the scrim, which starts the match.
/// The setting is saved for the next scrim, and it's exclusive with deathmatch warmup.
/// </summary>
public sealed partial class ScrimMode
{
    private bool _parkedForRetake;      // scrim is waiting in warmup while retake runs
    private bool _resumeFromRetake;     // the next Activate comes from retake, not a fresh warmup
    private bool _startAfterResume;     // start the match as soon as the scrim is back

    /// <summary>Retake is the active mode, with the scrim parked behind it.</summary>
    private void ParkForRetake()
    {
        _parkedForRetake = true;
        Chat.All("{green}Retake warmup.{default} Type {green}!ready{default} when you are done. Admins: {green}!warm{default} turns it off.");
        Plugin.Modes.Switch("retake");
    }

    /// <summary>Back to the scrim warmup; the match starts straight away when asked to.</summary>
    private void ResumeFromRetake(bool startAfter)
    {
        _resumeFromRetake = true;
        _startAfterResume = startAfter;
        Plugin.Modes.Switch("scrim");
    }

    /// <summary>Runs in place of EnterWarmup when the scrim is activated again from retake.</summary>
    private void ResumeWarmup()
    {
        _resumeFromRetake = false;
        _parkedForRetake = false;
        KillTimers();
        ExecConfig(Cfg.WarmupConfig);
        if (Cfg.WarmupDeathmatch)
            Server.ExecuteCommand(DeathmatchOnCvars);
        Server.ExecuteCommand("mp_warmup_start");

        if (Cfg.ReadyReminderSeconds > 0)
            _reminderTimer = Plugin.AddTimer(Cfg.ReadyReminderSeconds, RemindReady,
                TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        if (_startAfterResume)
        {
            _startAfterResume = false;
            StartMatch();
        }
        else
        {
            Chat.All("Back to the scrim warmup. Type {green}!ready{default} when you are ready to play.");
        }
    }

    private void ToggleWarmupRetake(CCSPlayerController player)
    {
        Plugin.Menus.Close(player);
        if (_state != ScrimState.Warmup)
        {
            Chat.To(player, "The warmup can only be changed during the scrim warmup.");
            return;
        }

        Cfg.WarmupRetake = !Cfg.WarmupRetake;
        if (Cfg.WarmupRetake)
        {
            Cfg.WarmupDeathmatch = false;
            Plugin.SaveConfig();
            if (!_parkedForRetake)
                ParkForRetake();
        }
        else
        {
            Plugin.SaveConfig();
            if (_parkedForRetake)
                ResumeFromRetake(startAfter: false);
            Chat.All("{red}Retake warmup off.{default} Type {green}!ready{default} when you are ready.");
        }
    }

    /// <summary>Called by retake: a player readies up for the scrim.</summary>
    public void WarmupReadyFromRetake(CCSPlayerController? player)
    {
        if (!_parkedForRetake)
        {
            Chat.To(player, "Ready-up is only used in the scrim warmup.");
            return;
        }
        OnReady(player, null!);
    }

    /// <summary>Called by retake: an admin starts the scrim match.</summary>
    public void WarmupForceStartFromRetake(CCSPlayerController? admin)
    {
        if (!_parkedForRetake)
        {
            Chat.To(admin, "The match can only be started from the scrim warmup.");
            return;
        }
        ForceStart(admin);
    }

    /// <summary>Called by retake: the warmup menu (admin).</summary>
    public void WarmupMenuFromRetake(CCSPlayerController? player) => OpenWarmupMenu(player);
}
