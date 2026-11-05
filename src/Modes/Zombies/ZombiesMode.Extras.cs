using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Zombies;

public enum PowerUp
{
    MaxAmmo,
    InstaKill,
    DoublePoints,
    Nuke
}

/// <summary>
/// Power-ups dropped by zombies (a glowing chicken, walk into it), boss zombies, Pack-a-Punch,
/// and spawning zombies near the players instead of at the far side of the map.
/// </summary>
public sealed partial class ZombiesMode
{
    private const int MaxCrumbs = 300;
    private const int MaxPowerUpsOnMap = 3;

    private sealed record PowerUpDrop(CBaseModelEntity Prop, PowerUp Type, Vector Position, float ExpiresAt);

    private readonly List<PowerUpDrop> _drops = new();
    private readonly List<Vector> _crumbs = new();
    private readonly Dictionary<int, Vector> _lastCrumb = new();
    private readonly Dictionary<uint, int> _papWeapons = new(); // entity index of an upgraded weapon -> times upgraded
    private float _instaKillUntil;
    private float _doublePointsUntil;
    private bool _bossPending;
    private int _bossSlot = -1;

    private void ResetExtras()
    {
        foreach (var drop in _drops)
        {
            if (drop.Prop.IsValid)
                drop.Prop.Remove();
        }
        _drops.Clear();
        _crumbs.Clear();
        _recentSpots.Clear();
        _lastCrumb.Clear();
        _papWeapons.Clear();
        _instaKillUntil = 0;
        _doublePointsUntil = 0;
        _bossPending = false;
        _bossSlot = -1;
    }

    private bool InstaKill => _instaKillUntil > Server.CurrentTime;
    private bool DoublePoints => _doublePointsUntil > Server.CurrentTime;

    private string PowerUpHud()
    {
        var parts = new List<string>();
        if (InstaKill)
            parts.Add($"INSTA-KILL {_instaKillUntil - Server.CurrentTime:0}s");
        if (DoublePoints)
            parts.Add($"DOUBLE POINTS {_doublePointsUntil - Server.CurrentTime:0}s");
        return parts.Count == 0 ? "" : "  |  " + string.Join("  |  ", parts);
    }

    // ---------------------------------------------------------------- bosses

    private void StartWaveExtras()
    {
        _bossSlot = -1;
        _bossPending = Cfg.BossEveryWaves > 0 && _wave % Cfg.BossEveryWaves == 0;
        if (_bossPending)
            Chat.All("{red}BOSS WAVE!{default} A giant zombie is coming.");
    }

    /// <summary>The first zombie of a boss wave becomes the boss: much more HP, bigger, dark red.</summary>
    private void SetupBossOrNormal(CCSPlayerController bot, CCSPlayerPawn pawn)
    {
        var boss = _bossPending;
        if (boss)
        {
            _bossPending = false;
            _bossSlot = bot.Slot;
            var health = ZombieHealth() * Cfg.BossHealthMultiplier;
            SetHealth(pawn, health, health);
            Chat.All($"{{red}}The boss is here{{default}} with {health} HP! Killing it gives {Cfg.BossKillPoints} points.");
        }
        else if (_bossSlot == bot.Slot)
        {
            _bossSlot = -1;
        }

        var scale = boss ? Cfg.BossScale : 1f;
        if (pawn.CBodyComponent?.SceneNode is { } node && node.Scale != scale)
        {
            node.Scale = scale;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        }
        pawn.Render = boss ? Color.FromArgb(255, 180, 20, 20) : Color.FromArgb(255, 120, 255, 120);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    private void OnZombieKilled(CCSPlayerController victim, CCSPlayerController? attacker)
    {
        if (victim.Slot == _bossSlot)
        {
            _bossSlot = -1;
            if (IsHuman(attacker))
            {
                AddPoints(attacker!, Cfg.BossKillPoints);
                Chat.All($"{{gold}}{attacker!.PlayerName}{{default}} killed the boss! +{Cfg.BossKillPoints} points.");
            }
            else
            {
                Chat.All("{gold}The boss is dead!");
            }
        }

        if (IsHuman(attacker) && victim.PlayerPawn.Value?.AbsOrigin is { } origin && Random.Shared.NextDouble() < Cfg.PowerUpChance)
            DropPowerUp(new Vector(origin.X, origin.Y, origin.Z));
    }

    // ---------------------------------------------------------------- spawning near players

    /// <summary>Remembers where players have stood, so zombies can spawn at safe spots near them.</summary>
    private void RecordCrumbs()
    {
        foreach (var player in Humans())
        {
            var pawn = player.PlayerPawn.Value;
            if (!player.PawnIsAlive || pawn?.AbsOrigin == null || (pawn.Flags & 1) == 0) // FL_ONGROUND
                continue;

            var position = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
            if (_lastCrumb.TryGetValue(player.Slot, out var last) && Distance(last, position) < 150f)
                continue;

            _lastCrumb[player.Slot] = position;
            _crumbs.Add(position);
            if (_crumbs.Count > MaxCrumbs)
                _crumbs.RemoveAt(0);
        }
    }

    /// <summary>Moves a fresh zombie to a spot players stood on, out of sight range but not too far away.</summary>
    private void PlaceZombie(CCSPlayerPawn pawn)
    {
        if (!Cfg.SpawnNearPlayers || _crumbs.Count == 0)
            return;

        var humans = Humans().Where(h => h.PawnIsAlive).Select(h => h.PlayerPawn.Value?.AbsOrigin).OfType<Vector>().ToList();
        if (humans.Count == 0)
            return;

        // Spots another zombie is standing on (or was just put on) are taken: zombies spawned on top of each other get stuck.
        var now = Server.CurrentTime;
        _recentSpots.RemoveAll(s => s.At < now - 3f);
        var taken = Players.Playing()
            .Where(p => p.PawnIsAlive && p.PlayerPawn.Value?.AbsOrigin != null && p.PlayerPawn.Value.Handle != pawn.Handle)
            .Select(p => p.PlayerPawn.Value!.AbsOrigin!)
            .Select(o => new Vector(o.X, o.Y, o.Z))
            .Concat(_recentSpots.Select(s => s.Spot))
            .ToList();

        var spots = _crumbs.Where(c =>
        {
            var nearest = humans.Min(h => Distance(h, c));
            return nearest >= Cfg.SpawnMinDistance && nearest <= Cfg.SpawnMaxDistance && taken.All(t => Distance(t, c) > 80f);
        }).ToList();
        if (spots.Count == 0)
            return;

        var spot = spots[Random.Shared.Next(spots.Count)];
        _recentSpots.Add((spot, now));
        pawn.Teleport(new Vector(spot.X, spot.Y, spot.Z + 4f), null, new Vector(0, 0, 0));
    }

    private readonly List<(Vector Spot, float At)> _recentSpots = new();

    // ---------------------------------------------------------------- power-ups

    private void DropPowerUp(Vector position)
    {
        if (_drops.Count >= MaxPowerUpsOnMap)
            return;

        var type = Enum.GetValues<PowerUp>()[Random.Shared.Next(Enum.GetValues<PowerUp>().Length)];
        var model = ChickenModel();
        var prop = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (prop == null || string.IsNullOrEmpty(model))
            return;

        prop.SetModel(model);
        prop.Spawnflags = 256u;
        prop.DispatchSpawn();
        prop.Teleport(position, new QAngle(0, Random.Shared.Next(360), 0), new Vector(0, 0, 0));
        prop.Glow.GlowColorOverride = PowerUpColor(type);
        prop.Glow.GlowRange = 3000;
        prop.Glow.GlowRangeMin = 0;
        prop.Glow.GlowTeam = -1;
        prop.Glow.GlowType = 3;

        _drops.Add(new PowerUpDrop(prop, type, position, Server.CurrentTime + Cfg.PowerUpDespawnSeconds));
        Chat.All($"A {{gold}}{PowerUpName(type)}{{default}} power-up dropped! Walk into the glowing chicken to take it.");
    }

    /// <summary>The model of a real chicken (spawning one also makes sure the model is loaded on this map).</summary>
    private static string? ChickenModel()
    {
        var chicken = Utilities.CreateEntityByName<CBaseModelEntity>("chicken");
        if (chicken == null)
            return null;
        chicken.DispatchSpawn();
        var model = chicken.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        chicken.Remove();
        return string.IsNullOrEmpty(model) ? "models/chicken/chicken.vmdl" : model;
    }

    private void CheckPowerUps()
    {
        if (_drops.Count == 0)
            return;

        var humans = Humans().Where(h => h.PawnIsAlive && h.PlayerPawn.Value?.AbsOrigin != null).ToList();
        foreach (var drop in _drops.ToList())
        {
            var picker = humans.FirstOrDefault(h => Distance(h.PlayerPawn.Value!.AbsOrigin!, drop.Position) < 70f);
            if (picker == null && drop.ExpiresAt > Server.CurrentTime && drop.Prop.IsValid)
                continue;

            _drops.Remove(drop);
            if (drop.Prop.IsValid)
                drop.Prop.Remove();
            if (picker != null)
                ActivatePowerUp(drop.Type, picker);
        }
    }

    private void ActivatePowerUp(PowerUp type, CCSPlayerController picker)
    {
        Chat.All($"{{gold}}{picker.PlayerName}{{default}} picked up {{green}}{PowerUpName(type)}{{default}}!");
        switch (type)
        {
            case PowerUp.MaxAmmo:
                foreach (var player in Humans().Where(h => h.PawnIsAlive))
                    RefillAmmo(player);
                break;
            case PowerUp.InstaKill:
                _instaKillUntil = Server.CurrentTime + Cfg.PowerUpSeconds;
                break;
            case PowerUp.DoublePoints:
                _doublePointsUntil = Server.CurrentTime + Cfg.PowerUpSeconds;
                break;
            case PowerUp.Nuke:
                foreach (var zombie in Zombies().Where(z => z.PawnIsAlive))
                {
                    // The boss survives a nuke: it only loses part of its health.
                    if (zombie.Slot == _bossSlot && zombie.PlayerPawn.Value is { } boss)
                    {
                        var damage = (int)(boss.MaxHealth * Cfg.NukeBossDamage);
                        SetHealth(boss, Math.Max(1, boss.Health - damage), boss.MaxHealth);
                        Chat.All($"The nuke hurt the boss: {boss.Health} HP left.");
                        continue;
                    }
                    zombie.CommitSuicide(false, true);
                }
                foreach (var player in Humans())
                    AddPoints(player, Cfg.NukePoints);
                break;
        }
    }

    private string PowerUpName(PowerUp type) => type switch
    {
        PowerUp.MaxAmmo => "Max Ammo",
        PowerUp.InstaKill => $"Insta-Kill ({Cfg.PowerUpSeconds:0}s)",
        PowerUp.DoublePoints => $"Double Points ({Cfg.PowerUpSeconds:0}s)",
        _ => $"Nuke (+{Cfg.NukePoints} points)"
    };

    private static Color PowerUpColor(PowerUp type) => type switch
    {
        PowerUp.MaxAmmo => Color.FromArgb(255, 40, 200, 40),
        PowerUp.InstaKill => Color.FromArgb(255, 255, 40, 40),
        PowerUp.DoublePoints => Color.FromArgb(255, 255, 215, 0),
        _ => Color.FromArgb(255, 255, 120, 0)
    };

    // ---------------------------------------------------------------- Pack-a-Punch

    /// <summary>Upgrades the gun in your hands: more damage, full ammo, gold color.</summary>
    private void BuyPackAPunch(CCSPlayerController player)
    {
        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        var data = weapon == null ? null : new CCSWeaponBase(weapon.Handle).VData;
        if (weapon == null || data?.GearSlot is not (gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL))
        {
            Chat.To(player, "Hold the gun you want to upgrade.");
            return;
        }
        var level = _papWeapons.GetValueOrDefault(weapon.Index);
        if (level >= Cfg.PackAPunchMaxLevel)
        {
            Chat.To(player, $"That gun is already Pack-a-Punched {Cfg.PackAPunchMaxLevel} times (max).");
            return;
        }
        if (!TrySpend(player, Cfg.PackAPunchPrice))
            return;

        _papWeapons[weapon.Index] = ++level;
        weapon.Render = Color.FromArgb(255, 255, 200, 40);
        Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_clrRender");
        RefillAmmo(player);
        Chat.All($"{{gold}}{player.PlayerName}{{default}} Pack-a-Punched their {{green}}{WeaponName(weapon.DesignerName)}{{default}} (level {level}/{Cfg.PackAPunchMaxLevel}, x{PapMultiplier(level):0.#} damage)!");
    }

    /// <summary>Damage multiplier of the gun in the attacker's hands (1 when it isn't upgraded).</summary>
    private float PapDamage(CCSPlayerController attacker) =>
        _papWeapons.Count > 0 && attacker.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is { } weapon &&
        _papWeapons.TryGetValue(weapon.Index, out var level)
            ? PapMultiplier(level)
            : 1f;

    /// <summary>Each upgrade adds the first upgrade's bonus again: x2, x3, x4 with the default PackAPunchDamage of 2.</summary>
    private float PapMultiplier(int level) => 1f + (Cfg.PackAPunchDamage - 1f) * level;

    private static float Distance(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
