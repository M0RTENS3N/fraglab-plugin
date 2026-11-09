using System.Numerics;

namespace FragLab.Modes.Practice;

public sealed partial class PracticeMode
{
    /// <summary>Where the crosshair lands when standing on feet and aiming at pitch/yaw.</summary>
    private static Vector3 AimPoint(NadeSimulator sim, Vector3 feet, float pitch, float yaw)
    {
        var eye = feet with { Z = feet.Z + EyeHeight };
        return sim.TraceEnd(eye, eye + NadeSimulator.Forward(pitch, yaw) * 4000f);
    }

    /// <summary>
    /// True when the aim point is on an edge or corner: a tiny change of aim hits something much closer or further.
    /// Those spots are easy to save and to find again, unlike a point on an empty wall.
    /// </summary>
    private static bool IsEdgeAim(NadeSimulator sim, Vector3 feet, float pitch, float yaw)
    {
        var eye = feet with { Z = feet.Z + EyeHeight };
        var center = Vector3.Distance(eye, sim.TraceEnd(eye, eye + NadeSimulator.Forward(pitch, yaw) * 4000f));
        foreach (var (dp, dy) in new[] { (0.4f, 0f), (-0.4f, 0f), (0f, 0.4f), (0f, -0.4f) })
        {
            var hit = sim.TraceEnd(eye, eye + NadeSimulator.Forward(pitch + dp, yaw + dy) * 4000f);
            if (MathF.Abs(Vector3.Distance(eye, hit) - center) > 12f)
                return true;
        }
        return false;
    }
}
