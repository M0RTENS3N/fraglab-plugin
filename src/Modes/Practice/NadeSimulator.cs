using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CsVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace FragLab.Modes.Practice;

public enum NadeKind
{
    Smoke,
    Flash,
    HE,
    Molotov
}

/// <summary>Standing throws (left = full, middle = both buttons, right = short) and jump-throws (left and middle).</summary>
public enum ThrowType
{
    Left,
    Middle,
    Right,
    JumpLeft,
    JumpMiddle
}

/// <summary>A surface the simulated grenade hit, for comparing with real throws.</summary>
public readonly record struct SimBounce(int Tick, Vector3 Position, Vector3 Normal);

/// <summary>
/// Predicts where a throw lands by flying the grenade through the map with world traces.
/// The throw (release point, 675 u/s, aim correction) is verified against real CS2 throws with !nfcheck.
/// Flight follows CS's grenade code: half-step gravity at 0.4 x sv_gravity, reflection with 0.45 elasticity,
/// stop below 30 u/s, and after a floor bounce the rest of the tick is still travelled.
/// </summary>
public sealed class NadeSimulator
{
    private const float TickInterval = 1f / 64f;
    private const float ThrowSpeed = 750f * 0.9f;
    private const float Elasticity = 0.45f;
    private const float StopSpeed = 30f;
    private const float StopEpsilon = 0.1f;
    private const float FuseTime = 1.5f;        // flash / HE
    private const float MolotovAirTime = 2.0f;  // molotovs burst in the air after this
    private const float MaxTime = 12f;

    // Moving things a lineup can't rely on: grenades do bounce off them in game, but they won't be there.
    private const Contents Moving = Contents.Player | Contents.Npc | Contents.Hitbox;

    /// <summary>
    /// Exactly what real grenades collide with on this server, copied from a thrown grenade (see Learn).
    /// Until one has been seen, a best guess is used.
    /// </summary>
    public static (ulong As, ulong With, ulong Exclude)? LearnedCollision { get; set; }

    public static bool HasLearnedCollision => LearnedCollision != null;

    /// <summary>
    /// How a real jump-throw differs from a standing throw: extra upward speed (u/s) and how much higher the
    /// grenade is released (units). Measured from real jump-throws; until then CS's old formula is used.
    /// </summary>
    public static (float UpSpeed, float ReleaseHeight)? LearnedJump { get; set; }

    private readonly CBaseEntity _ignore;
    private readonly float _gravity;
    private readonly float _jumpSpeed;
    private readonly TraceOptions _options;

    // Reused for every trace: creating native vectors per step would allocate millions of them.
    private readonly CsVector _start = new(0, 0, 0);
    private readonly CsVector _end = new(0, 0, 0);
    private readonly CsVector _mins = new(-2, -2, -2);
    private readonly CsVector _maxs = new(2, 2, 2);

    public NadeSimulator(CBaseEntity ignore)
    {
        _ignore = ignore;
        var svGravity = ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() ?? 800f;
        _gravity = svGravity * 0.4f;
        _jumpSpeed = ConVar.Find("sv_jump_impulse")?.GetPrimitiveValue<float>() ?? 301.993377f;

        _options = LearnedCollision is { } learned
            ? new TraceOptions
            {
                InteractsAs = (Contents)learned.As,
                InteractsWith = (Contents)learned.With & ~Moving,
                InteractsExclude = (Contents)learned.Exclude | Moving
            }
            : new TraceOptions
            {
                InteractsWith = Contents.Solid | Contents.Window | Contents.WorldGeometry | Contents.StaticLevel |
                                Contents.PhysicsProp | Contents.CsgoGrenadeClip | Contents.CsgoMoveable | Contents.PassBullets,
                InteractsExclude = Moving | Contents.Trigger
            };
    }

    /// <summary>Copies the collision layers of a real grenade projectile so traces hit exactly what grenades hit.</summary>
    public static bool Learn(CBaseEntity projectile)
    {
        var attribute = projectile.Collision?.CollisionAttribute;
        if (attribute == null || attribute.InteractsWith == 0)
            return false;

        var learned = (attribute.InteractsAs, attribute.InteractsWith, attribute.InteractsExclude);
        if (LearnedCollision == learned)
            return false;

        LearnedCollision = learned;
        return true;
    }

    public static float Strength(ThrowType type) => type switch
    {
        ThrowType.Left or ThrowType.JumpLeft => 1f,
        ThrowType.Middle or ThrowType.JumpMiddle => 0.5f,
        _ => 0f
    };

    public static bool IsJump(ThrowType type) => type is ThrowType.JumpLeft or ThrowType.JumpMiddle;

    public static string Describe(ThrowType type) => type switch
    {
        ThrowType.Left => "left click",
        ThrowType.Middle => "middle throw (left + right click)",
        ThrowType.Right => "right click",
        ThrowType.JumpLeft => "jump-throw (left click)",
        _ => "jump middle-throw (left + right click)"
    };

    /// <summary>Where the grenade ends up (smokes: where it stops, molotovs: where it bursts, flash/HE: where it pops).</summary>
    public Vector3 Simulate(Vector3 eye, float pitch, float yaw, ThrowType type, NadeKind kind)
    {
        var (position, velocity) = ThrowStart(eye, pitch, yaw, type);
        return Fly(position, velocity, kind);
    }

    /// <summary>Where the grenade leaves the hand and how fast, for a throw from this eye position and aim.</summary>
    public (Vector3 Position, Vector3 Velocity) ThrowStart(Vector3 eye, float pitch, float yaw, ThrowType type)
    {
        var strength = Strength(type);

        // Aim correction: throws go a bit higher than where you look, less so near straight up/down.
        if (pitch > 90f) pitch -= 360f;
        else if (pitch < -90f) pitch += 360f;
        pitch -= (90f - MathF.Abs(pitch)) * 10f / 90f;

        var forward = Forward(pitch, yaw);
        var speed = ThrowSpeed * Math.Clamp(strength * 0.7f + 0.3f, 0f, 1f);

        // Release point: eye height (lower for underhand throws), pushed 22 units forward unless a wall is in the way.
        var src = eye with { Z = eye.Z + strength * 12f - 12f };
        var (hit, end, _, _) = Hull(src, src + forward * 22f);
        var position = (hit ? end : src + forward * 22f) - forward * 6f;
        var velocity = forward * speed;

        // The thrower's own velocity is added (x1.25), which is what makes jump-throws go further.
        if (IsJump(type))
        {
            var (upSpeed, releaseHeight) = LearnedJump ?? (_jumpSpeed * 1.25f, 0f);
            velocity += new Vector3(0, 0, upSpeed);
            position += new Vector3(0, 0, releaseHeight);
        }

        return (position, velocity);
    }

    /// <summary>
    /// Flies a grenade from its release point until it lands, bursts or pops.
    /// Optionally records the position after every tick and every surface hit.
    /// </summary>
    public Vector3 Fly(Vector3 position, Vector3 velocity, NadeKind kind,
        List<Vector3>? path = null, List<SimBounce>? bounces = null)
    {
        var tick = 0;
        while (tick * TickInterval < MaxTime)
        {
            tick++;
            var time = tick * TickInterval;

            if (velocity != Vector3.Zero)
            {
                // Half-step gravity, as the engine does it.
                var newVz = velocity.Z - _gravity * TickInterval;
                var move = new Vector3(velocity.X * TickInterval, velocity.Y * TickInterval, (velocity.Z + newVz) * 0.5f * TickInterval);
                velocity = velocity with { Z = newVz };

                var (didHit, hitPos, normal, fraction) = Hull(position, position + move);
                if (didHit)
                {
                    position = hitPos;
                    bounces?.Add(new SimBounce(tick, position, normal));

                    if (kind == NadeKind.Molotov && normal.Z > 0.7f)
                    {
                        path?.Add(position);
                        return position;
                    }

                    var bounced = Clip(velocity, normal) * Elasticity;
                    var slow = bounced.LengthSquared() < StopSpeed * StopSpeed;

                    if (normal.Z > 0.7f)
                    {
                        // Floor: stop when slow, otherwise keep moving for the rest of this tick.
                        velocity = slow ? Vector3.Zero : bounced;
                        if (!slow)
                        {
                            var rest = velocity * ((1f - fraction) * TickInterval);
                            var (restHit, restPos, _, _) = Hull(position, position + rest);
                            position = restHit ? restPos : position + rest;
                        }
                    }
                    else
                    {
                        // Wall or ceiling: too slow and it would stick, so it drops.
                        velocity = slow ? Vector3.Zero : bounced;
                    }
                }
                else
                {
                    position += move;
                }
            }
            else if (kind is NadeKind.Smoke && OnGround(position))
            {
                // Resting smokes pop where they lie.
                path?.Add(position);
                return position;
            }
            else if (!OnGround(position))
            {
                // Stopped against a wall in the air: gravity pulls it down again.
                velocity = new Vector3(0, 0, -StopEpsilon);
            }

            path?.Add(position);

            switch (kind)
            {
                case NadeKind.Flash or NadeKind.HE when time >= FuseTime:
                    return position;
                case NadeKind.Molotov when time >= MolotovAirTime:
                    return DropToFloor(position);
            }
        }

        return position;
    }

    /// <summary>Reflects velocity off a surface (overbounce 2) and zeroes tiny components, like the engine.</summary>
    private static Vector3 Clip(Vector3 velocity, Vector3 normal)
    {
        var result = velocity - normal * (Vector3.Dot(velocity, normal) * 2f);
        return new Vector3(
            MathF.Abs(result.X) < StopEpsilon ? 0 : result.X,
            MathF.Abs(result.Y) < StopEpsilon ? 0 : result.Y,
            MathF.Abs(result.Z) < StopEpsilon ? 0 : result.Z);
    }

    private bool OnGround(Vector3 position)
    {
        var (hit, _, normal, _) = Hull(position, position - new Vector3(0, 0, 2f));
        return hit && normal.Z > 0.7f;
    }

    /// <summary>The floor straight below a point (or the point itself if there's none within 300 units).</summary>
    public Vector3 FloorBelow(Vector3 position)
    {
        var (hit, end, _, _) = Hull(position, position - new Vector3(0, 0, 300f));
        return hit ? end : position;
    }

    private Vector3 DropToFloor(Vector3 position)
    {
        var (hit, end, _, _) = Hull(position, position - new Vector3(0, 0, 1000f));
        return hit ? end : position;
    }

    private (bool Hit, Vector3 End, Vector3 Normal, float Fraction) Hull(Vector3 from, Vector3 to)
    {
        _start.X = from.X; _start.Y = from.Y; _start.Z = from.Z;
        _end.X = to.X; _end.Y = to.Y; _end.Z = to.Z;

        var result = Trace.TraceHullShape(_start, _end, _mins, _maxs, _ignore, _options);
        if (!result.DidHit() || result.Fraction >= 1f)
            return (false, to, Vector3.Zero, 1f);

        var end = result.EndPos;
        var normal = result.Normal;
        return (true, new Vector3(end.X, end.Y, end.Z), new Vector3(normal.X, normal.Y, normal.Z), result.Fraction);
    }

    public static Vector3 Forward(float pitch, float yaw)
    {
        var p = pitch * MathF.PI / 180f;
        var y = yaw * MathF.PI / 180f;
        return new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p));
    }

    public static float Distance(Vector3 a, Vector3 b, NadeKind kind) =>
        kind is NadeKind.Flash or NadeKind.HE
            ? Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)) // they pop in the air: compare on the map
            : Vector3.Distance(a, b);
}
