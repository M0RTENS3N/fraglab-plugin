using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CsVector = CounterStrikeSharp.API.Modules.Utils.Vector;
using QAngle = CounterStrikeSharp.API.Modules.Utils.QAngle;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Practice;

/// <summary>
/// Look at a lineup marker and press E to go to that lineup. At the lineup, a small green circle shows where to aim
/// (where your crosshair should be when you throw).
/// </summary>
public sealed partial class PracticeMode
{
    private const float MarkerUseRange = 1500f;
    private const float AimCircleSeconds = 20f;
    private const int AimCircleSegments = 16;

    private readonly List<(NadeLineup Lineup, Vector3 Position)> _markerSpots = new();
    private readonly HashSet<int> _markerUseHeld = new();
    private readonly Dictionary<int, (List<CBeam> Beams, Timer? Timer)> _aimCircles = new();

    /// <summary>Called from the 1/32 s poll: E (pressed, not held) while looking at a marker goes to its lineup.</summary>
    private void CheckMarkerUse(CCSPlayerController player)
    {
        if ((player.Buttons & PlayerButtons.Use) == 0)
        {
            _markerUseHeld.Remove(player.Slot);
            return;
        }
        if (!_markerUseHeld.Add(player.Slot))
            return; // still held from before

        var pawn = player.PlayerPawn.Value;
        if (_markerSpots.Count == 0 || !player.PawnIsAlive || pawn?.AbsOrigin == null || Plugin.Menus.IsOpen(player))
            return;

        var eye = new Vector3(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + EyeHeight);
        var forward = NadeSimulator.Forward(pawn.EyeAngles.X, pawn.EyeAngles.Y);

        NadeLineup? best = null;
        var bestOff = float.MaxValue;
        foreach (var (lineup, position) in _markerSpots)
        {
            var to = position - eye;
            var along = Vector3.Dot(to, forward);
            if (along <= 0 || along > MarkerUseRange)
                continue;

            // How far the crosshair line passes from the marker, relative to its distance (a cone that's easy to hit).
            var off = (to - forward * along).Length() / along;
            if (off < 0.06f && off < bestOff)
            {
                best = lineup;
                bestOff = off;
            }
        }

        if (best != null)
            GoToLineup(player, best);
    }

    /// <summary>A small circle on the wall (or whatever is in the way) where the crosshair should be for this lineup.</summary>
    private void ShowAimCircle(CCSPlayerController player, NadeLineup lineup)
    {
        ClearAimCircle(player.Slot);
        var pawn = player.PlayerPawn.Value;
        if (pawn == null)
            return;

        var eye = new Vector3(lineup.X, lineup.Y, lineup.Z + EyeHeight);
        var forward = NadeSimulator.Forward(lineup.Pitch, lineup.Yaw);
        var hit = new NadeSimulator(pawn).TraceEnd(eye, eye + forward * 3000f);
        var distance = Vector3.Distance(eye, hit);

        // Just in front of the surface, sized so it looks about the same from the lineup spot at any distance.
        var center = hit - forward * 2f;
        var radius = Math.Clamp(distance * 0.008f, 1.5f, 25f);
        var side = Vector3.Normalize(Vector3.Cross(forward, MathF.Abs(forward.Z) > 0.95f ? Vector3.UnitX : Vector3.UnitZ));
        var up = Vector3.Cross(side, forward);

        var beams = new List<CBeam>();
        for (var i = 0; i < AimCircleSegments; i++)
        {
            var a1 = i * MathF.Tau / AimCircleSegments;
            var a2 = (i + 1) * MathF.Tau / AimCircleSegments;
            var p1 = center + (side * MathF.Cos(a1) + up * MathF.Sin(a1)) * radius;
            var p2 = center + (side * MathF.Cos(a2) + up * MathF.Sin(a2)) * radius;
            if (MakeBeam(p1, p2, Color.FromArgb(255, 60, 255, 60), 0.6f) is { } beam)
                beams.Add(beam);
        }

        var slot = player.Slot;
        var timer = Plugin.AddTimer(AimCircleSeconds, () => ClearAimCircle(slot), TimerFlags.STOP_ON_MAPCHANGE);
        _aimCircles[slot] = (beams, timer);
    }

    private void ClearAimCircle(int slot)
    {
        if (!_aimCircles.Remove(slot, out var circle))
            return;
        circle.Timer?.Kill();
        foreach (var beam in circle.Beams.Where(b => b.IsValid))
            beam.Remove();
    }

    private void ClearAllAimCircles()
    {
        foreach (var slot in _aimCircles.Keys.ToList())
            ClearAimCircle(slot);
    }

    private static CBeam? MakeBeam(Vector3 from, Vector3 to, Color color, float width)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null)
            return null;
        beam.Render = color;
        beam.Width = width;
        beam.EndWidth = width;
        beam.Teleport(new CsVector(from.X, from.Y, from.Z), new QAngle(0, 0, 0), new CsVector(0, 0, 0));
        beam.EndPos.X = to.X;
        beam.EndPos.Y = to.Y;
        beam.EndPos.Z = to.Z;
        beam.DispatchSpawn();
        return beam;
    }
}
