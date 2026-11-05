using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Practice;

/// <summary>
/// How every throw was made: the technique (standing, walk, run, duck or jump variants, from speed and buttons at
/// release) and the click (full = left, medium = both, short = right, from a mouse poll just before the throw).
/// After !savenade the next throw is recorded into that lineup. !nades can filter by grenade, technique or click.
/// </summary>
public sealed partial class PracticeMode
{
    private const float MouseWindow = 0.25f;     // seconds a click still counts as "held" before the throw
    private const float PendingSeconds = 60f;    // how long after !savenade the next throw is recorded
    private const float RunSpeed = 150f;         // units/s, above this is a run
    private const float MoveSpeed = 10f;         // units/s, above this is a step or a move

    private readonly Dictionary<int, float> _leftAt = new();
    private readonly Dictionary<int, float> _rightAt = new();
    private readonly Dictionary<int, float> _bothAt = new();
    private readonly Dictionary<int, (NadeLineup Lineup, float At)> _pendingLineups = new();
    private readonly Dictionary<int, string> _lineupFilter = new();
    private Timer? _mousePoll;

    private void StartMousePoll()
    {
        _mousePoll?.Kill();
        _mousePoll = Plugin.AddTimer(0.03f, PollMouse, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopMousePoll()
    {
        _mousePoll?.Kill();
        _mousePoll = null;
        _leftAt.Clear();
        _rightAt.Clear();
        _bothAt.Clear();
        _pendingLineups.Clear();
    }

    private void PollMouse()
    {
        if (!IsActive)
        {
            StopMousePoll();
            return;
        }

        var now = Server.CurrentTime;
        foreach (var player in Players.Humans())
        {
            RecordSample(player);
            CheckMarkerUse(player);
            var left = (player.Buttons & PlayerButtons.Attack) != 0;
            var right = (player.Buttons & PlayerButtons.Attack2) != 0;
            if (left)
                _leftAt[player.Slot] = now;
            if (right)
                _rightAt[player.Slot] = now;
            if (left && right)
                _bothAt[player.Slot] = now;
        }
    }

    /// <summary>Called at every throw: records the technique and click, and fills in the lineup waiting for it.</summary>
    private void RecordThrowInfo(CCSPlayerController player, string grenade)
    {
        if (!_pendingLineups.TryGetValue(player.Slot, out var pending))
            return;
        _pendingLineups.Remove(player.Slot);

        if (Server.CurrentTime - pending.At > PendingSeconds || !_lineups.Lineups.Contains(pending.Lineup))
            return;
        if (pending.Lineup.Grenade.Length > 0 && Normalize(pending.Lineup.Grenade) != grenade)
        {
            // Threw a different grenade than the one saved: keep waiting for the right one.
            _pendingLineups[player.Slot] = pending;
            return;
        }

        var technique = ThrowTechnique(player);
        var click = ClickOf(player.Slot);
        pending.Lineup.Throw = technique;
        pending.Lineup.Strength = click;
        pending.Lineup.Path = RecentPath(player.Slot);
        if (pending.Lineup.Grenade.Length == 0)
            pending.Lineup.Grenade = "weapon_" + grenade;

        if (TrySaveLineups(player))
            Chat.To(player, $"Recorded {{green}}{pending.Lineup.Name}{{default}}: {technique}, {click} click.");
    }

    /// <summary>Full = left click, medium = both (held right before the throw), short = right click.</summary>
    private string ClickOf(int slot)
    {
        var now = Server.CurrentTime;
        if (Recent(_bothAt, slot, now))
            return "medium";
        if (Recent(_leftAt, slot, now))
            return "full";
        if (Recent(_rightAt, slot, now))
            return "short";
        return "full";
    }

    private static bool Recent(Dictionary<int, float> times, int slot, float now) =>
        times.TryGetValue(slot, out var at) && now - at <= MouseWindow;

    /// <summary>
    /// The technique from the state at release: on the ground or in the air, ducking, walking (Speed button), and speed.
    /// Names follow the usual CS terms: standing still is a jumpthrow, W + jump (moving) is a wjumpthrow.
    /// </summary>
    private static string ThrowTechnique(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null)
            return "unknown";

        var velocity = pawn.AbsVelocity;
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        var onGround = (pawn.Flags & 1) != 0;          // FL_ONGROUND
        var ducking = (player.Buttons & PlayerButtons.Duck) != 0 || (pawn.Flags & 2) != 0; // FL_DUCKING
        var walking = (player.Buttons & PlayerButtons.Speed) != 0;
        var running = speed >= RunSpeed;
        var moving = speed >= MoveSpeed;

        if (onGround)
        {
            if (ducking) return "duckthrow";
            if (walking && moving) return "walkthrow";
            if (running) return "runthrow";
            if (moving) return "stepthrow";
            return "normal";
        }

        if (ducking) return "duckjumpthrow";
        if (walking && moving) return "walkjumpthrow";
        if (running) return "runjumpthrow";
        if (moving) return "wjumpthrow";  // W + jump: moving forward while jumping
        return "jumpthrow";               // standing still: the usual jumpthrow
    }

    // ---------------------------------------------------------------- !nades filter

    private void OnNades(CCSPlayerController? player, CommandInfo info)
    {
        WithPractice(player, p =>
        {
            var filter = info.ArgString.Trim().Trim('"').ToLowerInvariant();
            if (filter.Length == 0 || filter == "all")
                _lineupFilter.Remove(p.Slot);
            else
                _lineupFilter[p.Slot] = filter;
            OpenLineupMenu(p);
        });
    }

    /// <summary>A grenade name (smoke, flash, he, molotov, decoy), a technique (jump, run, duck...) or a click (full, medium, short).</summary>
    private static bool Matches(NadeLineup lineup, string filter) =>
        filter.Length == 0 ||
        GrenadeFilterMatches(lineup.Grenade, filter) ||
        (lineup.Throw.Length > 0 && lineup.Throw.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
        lineup.Strength.Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static bool GrenadeFilterMatches(string grenade, string filter)
    {
        var wanted = filter switch
        {
            "smoke" => "smokegrenade",
            "flash" => "flashbang",
            "he" => "hegrenade",
            "molotov" => "molotov",
            "decoy" => "decoy",
            _ => null
        };
        return wanted != null && grenade.Length > 0 && Normalize(grenade) == wanted;
    }

    private static float LineupDistance(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
