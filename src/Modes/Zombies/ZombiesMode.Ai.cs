using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Modes.Zombies;

/// <summary>
/// Helps the bots act like zombies: a zombie that is far from every player for a while is moved to a spot near
/// them (so they always hunt you), and a zombie stuck on an obstacle crouch-jumps towards the nearest player.
/// Also COD-style health regeneration for players.
/// </summary>
public sealed partial class ZombiesMode
{
    private readonly Dictionary<int, float> _lostSince = new();
    private readonly Dictionary<int, (Vector Position, float Since)> _stuckCheck = new();
    private readonly Dictionary<int, float> _lastHurt = new();

    private void ResetAi()
    {
        _lostSince.Clear();
        _stuckCheck.Clear();
        _lastHurt.Clear();
    }

    private void ZombieAi()
    {
        var humans = Humans().Where(h => h.PawnIsAlive).Select(h => h.PlayerPawn.Value?.AbsOrigin).OfType<Vector>()
            .Select(o => new Vector(o.X, o.Y, o.Z)).ToList();
        if (humans.Count == 0)
            return;

        var now = Server.CurrentTime;
        foreach (var zombie in Zombies())
        {
            var pawn = zombie.PlayerPawn.Value;
            if (!zombie.PawnIsAlive || pawn?.AbsOrigin == null)
            {
                _lostSince.Remove(zombie.Slot);
                _stuckCheck.Remove(zombie.Slot);
                continue;
            }

            var position = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
            var target = humans.MinBy(h => Distance(h, position))!;
            var distance = Distance(target, position);

            // Lost: wandering far away from everyone.
            if (distance > Cfg.ZombieLostDistance)
            {
                var since = _lostSince.TryGetValue(zombie.Slot, out var s) ? s : _lostSince[zombie.Slot] = now;
                if (now - since >= Cfg.ZombieLostSeconds)
                {
                    _lostSince.Remove(zombie.Slot);
                    PlaceZombie(pawn);
                    continue;
                }
            }
            else
            {
                _lostSince.Remove(zombie.Slot);
            }

            if (Cfg.ZombieJumps)
                JumpIfStuck(zombie.Slot, pawn, position, target, distance, now);
        }
    }

    /// <summary>Hardly moved for a second while not next to a player: jump (crouch-jump height) towards them.</summary>
    private void JumpIfStuck(int slot, CCSPlayerPawn pawn, Vector position, Vector target, float distance, float now)
    {
        if (!_stuckCheck.TryGetValue(slot, out var check) || Distance(check.Position, position) > 40f)
        {
            _stuckCheck[slot] = (position, now);
            return;
        }

        if (now - check.Since < 1f || distance < 120f || (pawn.Flags & 1) == 0) // FL_ONGROUND
            return;

        var dx = target.X - position.X;
        var dy = target.Y - position.Y;
        var length = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        pawn.Teleport(null, null, new Vector(dx / length * 250f, dy / length * 250f, 330f));
        _stuckCheck[slot] = (position, now);
    }

    // ---------------------------------------------------------------- health regen

    private void OnHumanHurt(CCSPlayerController player) => _lastHurt[player.Slot] = Server.CurrentTime;

    /// <summary>Runs every 0.5 s: after RegenDelaySeconds without damage, health comes back.</summary>
    private void RegenHealth()
    {
        if (Cfg.RegenPerSecond <= 0)
            return;

        var now = Server.CurrentTime;
        foreach (var player in Humans().Where(h => h.PawnIsAlive))
        {
            if (player.PlayerPawn.Value is not { } pawn || pawn.Health >= pawn.MaxHealth || pawn.Health <= 0)
                continue;
            if (now - _lastHurt.GetValueOrDefault(player.Slot, -100f) < Cfg.RegenDelaySeconds)
                continue;

            var health = Math.Min(pawn.MaxHealth, pawn.Health + (int)MathF.Ceiling(Cfg.RegenPerSecond * 0.5f));
            SetHealth(pawn, health, pawn.MaxHealth);
        }
    }
}
