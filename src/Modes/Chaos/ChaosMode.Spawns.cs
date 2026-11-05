using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Chaos;

/// <summary>
/// Spawns: every player spawns at the spot furthest from everyone else (with a little randomness), picked from
/// the map's spawn points (team and deathmatch spawns) and spots players have walked on. Then spawn protection:
/// see-through and no damage for a few seconds, or until you shoot.
/// </summary>
public sealed partial class ChaosMode
{
    private const int MaxSpawnCrumbs = 400;

    private readonly List<Vector> _mapSpawns = new();
    private readonly List<Vector> _spawnCrumbs = new();
    private readonly Dictionary<int, Vector> _lastSpawnCrumb = new();
    private int _crumbCounter;

    private void ResetSpawns()
    {
        _mapSpawns.Clear();
        _spawnCrumbs.Clear();
        _lastSpawnCrumb.Clear();
    }

    private List<Vector> MapSpawns()
    {
        if (_mapSpawns.Count > 0)
            return _mapSpawns;

        foreach (var name in new[] { "info_deathmatch_spawn", "info_player_terrorist", "info_player_counterterrorist" })
        {
            foreach (var spawn in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
            {
                if (spawn.IsValid && spawn.AbsOrigin is { } o)
                    _mapSpawns.Add(new Vector(o.X, o.Y, o.Z));
            }
        }
        return _mapSpawns;
    }

    /// <summary>Remembers where players have stood (on the ground), so spawns spread over the whole map.</summary>
    private void RecordSpawnCrumbs()
    {
        if (++_crumbCounter % 4 != 0)
            return;

        foreach (var player in Players.Playing().Where(p => p.PawnIsAlive))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn?.AbsOrigin == null || (pawn.Flags & 1) == 0) // FL_ONGROUND
                continue;

            var position = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
            if (_lastSpawnCrumb.TryGetValue(player.Slot, out var last) && SpawnDistance(last, position) < 200f)
                continue;
            if (_spawnCrumbs.Any(c => SpawnDistance(c, position) < 150f))
                continue;

            _lastSpawnCrumb[player.Slot] = position;
            _spawnCrumbs.Add(position);
            if (_spawnCrumbs.Count > MaxSpawnCrumbs)
                _spawnCrumbs.RemoveAt(Random.Shared.Next(_spawnCrumbs.Count));
        }
    }

    /// <summary>Moves a freshly spawned player to one of the spots furthest from every other living player.</summary>
    private void PlaceSpawn(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        var others = Players.Playing()
            .Where(p => p.Slot != player.Slot && p.PawnIsAlive && p.PlayerPawn.Value?.AbsOrigin != null)
            .Select(p => p.PlayerPawn.Value!.AbsOrigin!)
            .Select(o => new Vector(o.X, o.Y, o.Z))
            .ToList();
        if (others.Count == 0)
            return;

        var candidates = MapSpawns().Concat(_spawnCrumbs)
            .Select(c => (Spot: c, Nearest: others.Min(o => SpawnDistance(o, c))))
            .Where(c => c.Nearest > 100f)
            .OrderByDescending(c => c.Nearest)
            .Take(6)
            .ToList();
        if (candidates.Count == 0)
            return;

        // One of the best few, so spawns aren't predictable.
        var spot = candidates[Random.Shared.Next(candidates.Count)].Spot;
        pawn.Teleport(new Vector(spot.X, spot.Y, spot.Z + 4f), new QAngle(0, Random.Shared.Next(360), 0), new Vector(0, 0, 0));
    }

    // ---------------------------------------------------------------- spawn protection

    private bool IsProtected(int slot) => _protectedUntil.GetValueOrDefault(slot) > Server.CurrentTime;

    private void StartProtection(CCSPlayerPawn pawn, int slot)
    {
        if (Cfg.SpawnProtectionSeconds <= 0)
            return;
        _protectedUntil[slot] = Server.CurrentTime + Cfg.SpawnProtectionSeconds;
        SetAlpha(pawn, 110);
    }

    private void EndProtection(int slot)
    {
        if (!_protectedUntil.Remove(slot))
            return;
        var pawn = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value;
        if (pawn != null && pawn.IsValid)
            SetAlpha(pawn, _chaos == ChaosEvent.Ghosts ? 45 : 255);
    }

    private void CheckProtectionExpiry()
    {
        var now = Server.CurrentTime;
        foreach (var (slot, until) in _protectedUntil.ToList())
        {
            if (until <= now)
                EndProtection(slot);
        }
    }

    private static float SpawnDistance(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
