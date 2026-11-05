using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using FragLab.Core;

namespace FragLab.Modes.Practice;

/// <summary>
/// Closes the loop on the nade finder: when you throw from a found lineup, the throw is checked against the one the
/// finder asked for (technique, click, aim). When it lands, the miss is measured, and if it's too far, the aim is
/// searched again with the real offset taken into account (what the simulation gets wrong at this spot), so the next
/// throw lands closer.
/// </summary>
public sealed partial class PracticeMode
{
    private const float FinderSpotRadius = 24f;   // you threw "from the lineup" when your feet were this close
    private const float AimTolerance = 0.3f;      // degrees of mouse movement before we mention it
    private const float CorrectionRange = 1.5f;   // degrees searched around the old aim
    private const float CorrectionStep = 0.1f;

    private sealed class FinderThrow
    {
        public required FinderResult Result;
        public required Vector3 Target;
        public required float Pitch;
        public required float Yaw;
        public required string Grenade;
        public required bool ThrownRight;
    }

    private readonly Dictionary<int, FinderThrow> _finderThrows = new();

    /// <summary>Called at every throw: is this a throw of the found lineup, and was it thrown the way the finder said?</summary>
    private void CheckFinderThrow(CCSPlayerController player, string grenade)
    {
        var state = Finder(player);
        var pawn = player.PlayerPawn.Value;
        if (state.Result is not { } result || state.Target is not { } target || pawn?.AbsOrigin == null)
            return;
        if (NadeKindOf(grenade) != result.Kind)
            return;

        var dx = pawn.AbsOrigin.X - result.Feet.X;
        var dy = pawn.AbsOrigin.Y - result.Feet.Y;
        if (MathF.Sqrt(dx * dx + dy * dy) > FinderSpotRadius)
            return;

        var technique = ThrowTechnique(player);
        var click = ClickOf(player.Slot);
        var wantJump = NadeSimulator.IsJump(result.Type);
        var wantClick = result.Type switch
        {
            ThrowType.Left or ThrowType.JumpLeft => "full",
            ThrowType.Middle or ThrowType.JumpMiddle => "medium",
            _ => "short"
        };
        var jumped = technique.Contains("jump");
        var rightTechnique = jumped == wantJump && (jumped || technique == "normal");
        var rightClick = click == wantClick;

        var pitch = pawn.EyeAngles.X;
        var yaw = pawn.EyeAngles.Y;
        var aimOff = MathF.Max(MathF.Abs(pitch - result.Pitch), MathF.Abs(NormalizeYaw(yaw - result.Yaw)));

        if (!rightTechnique || !rightClick)
            Chat.To(player, $"{{red}}Different throw:{{default}} you did a {technique} with a {click} click, " +
                            $"the lineup is a {NadeSimulator.Describe(result.Type)}{(wantJump ? "" : ", standing still")}.");
        else if (aimOff > AimTolerance)
            Chat.To(player, $"Your aim moved {aimOff:0.0} degrees from the lineup before the throw.");

        _finderThrows[player.Slot] = new FinderThrow
        {
            Result = result,
            Target = target,
            Pitch = pitch,
            Yaw = yaw,
            Grenade = grenade,
            ThrownRight = rightTechnique && rightClick
        };
    }

    /// <summary>Called when a grenade lands: how far from the target, and correct the lineup if it missed.</summary>
    private void CheckFinderLanding(CCSPlayerController player, string grenade, Vector3 landed)
    {
        if (!_finderThrows.TryGetValue(player.Slot, out var thrown) || thrown.Grenade != grenade)
            return;
        _finderThrows.Remove(player.Slot);

        var miss = NadeSimulator.Distance(landed, thrown.Target, thrown.Result.Kind);
        var tolerance = Tolerance(Finder(player));
        if (miss <= tolerance)
        {
            Chat.To(player, $"{{green}}Landed {miss:0} units from your spot.{{default}} The lineup works, save it with {{green}}!nfsave <name>{{default}}.");
            return;
        }

        Chat.To(player, $"{{red}}Landed {miss:0} units from your spot.");
        if (!thrown.ThrownRight)
        {
            Chat.To(player, "Throw it exactly as described, then the finder can correct the aim.");
            return;
        }

        StartCorrection(player, thrown, landed);
    }

    /// <summary>
    /// The real grenade landed somewhere else than the simulation said for the aim you used. That offset is what the
    /// simulation gets wrong here, so search the aim again (around the old one) for the target minus that offset.
    /// </summary>
    private void StartCorrection(CCSPlayerController player, FinderThrow thrown, Vector3 landed)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null)
            return;
        if (_search != null)
        {
            Chat.To(player, "Another search is running, throw again in a moment to correct the lineup.");
            return;
        }

        var result = thrown.Result;
        var simulator = new NadeSimulator(pawn);
        var eye = result.Feet with { Z = result.Feet.Z + EyeHeight };
        var predicted = simulator.Simulate(eye, thrown.Pitch, thrown.Yaw, result.Type, result.Kind);
        var offset = landed - predicted;

        var job = new SearchJob
        {
            Slot = player.Slot,
            SteamId = player.SteamID,
            Kind = result.Kind,
            Feet = result.Feet,
            Target = thrown.Target - offset,
            Tolerance = Tolerance(Finder(player)),
            Simulator = simulator,
            Phase = 2 // straight to the fine search, then the slightly-off-aim checks
        };
        for (var dp = -CorrectionRange; dp <= CorrectionRange; dp += CorrectionStep)
            for (var dy = -CorrectionRange; dy <= CorrectionRange; dy += CorrectionStep)
                job.Queue.Add(new Throw(Math.Clamp(result.Pitch + dp, -89f, 89f), NormalizeYaw(result.Yaw + dy), result.Type, 0));

        _search = job;
        Finder(player).Result = null;
        _searchTimer = Plugin.AddTimer(0.01f, StepSearch, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        Chat.To(player, $"Correcting the lineup for the real throw (the prediction was {offset.Length():0} units off here)...");
    }
}
