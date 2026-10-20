using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using FragLab.Core.Menus;

namespace FragLab.Modes.Practice;

/// <summary>Grenade lineups (!savenade, !nades), last throw position (!last) and grenade airtime.</summary>
public sealed partial class PracticeMode
{
    private static readonly Dictionary<string, string> GrenadeNames = new()
    {
        ["weapon_smokegrenade"] = "Smoke",
        ["weapon_flashbang"] = "Flash",
        ["weapon_hegrenade"] = "HE",
        ["weapon_molotov"] = "Molotov",
        ["weapon_incgrenade"] = "Incendiary",
        ["weapon_decoy"] = "Decoy"
    };

    private readonly LineupStore _lineups;
    private readonly Dictionary<int, (Vector Position, QAngle Angles)> _lastThrows = new();

    private readonly HashSet<int> _noFlash = new();

    // Damage (bullets and utility) is summed per attacker, victim and weapon, then reported once it stops.
    private readonly Dictionary<(int Attacker, int Victim, string Weapon), (int Damage, int Hits, int Headshots, float LastHit)> _nadeDamage = new();
    private bool _nadeDamageFlushScheduled;

    // Throw times per player and grenade type, used to print the airtime when it lands.
    private readonly Dictionary<(int Slot, string Grenade), Queue<float>> _throwTimes = new();

    private void RegisterLineups()
    {
        AddCommand(["nades", "lineups"], "Open the grenade lineup menu", (p, _) => WithPractice(p, OpenLineupMenu));
        AddCommand(["savenade"], "Save a lineup at your position: !savenade <name>", OnSaveNade, adminOnly: true);
        AddCommand(["delnade"], "Delete a lineup: !delnade <name>", OnDeleteNade, adminOnly: true);
    }

    private void OnSaveNade(CCSPlayerController? player, CommandInfo info)
    {
        WithPractice(player, p =>
        {
            var name = info.ArgString.Trim().Trim('"');
            if (name.Length == 0)
            {
                Chat.To(p, "Usage: !savenade <name>  (hold the grenade and aim where you throw from)");
                return;
            }

            if (!TryGetPose(p, out var pose))
                return;

            var active = p.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.DesignerName ?? "";
            var lineup = new NadeLineup
            {
                Name = name,
                Grenade = GrenadeNames.ContainsKey(active) ? active : "",
                Author = p.PlayerName,
                X = pose.Position.X,
                Y = pose.Position.Y,
                Z = pose.Position.Z,
                Pitch = pose.Angles.X,
                Yaw = pose.Angles.Y
            };

            var existing = _lineups.Find(name);
            if (existing != null)
                _lineups.Lineups.Remove(existing);
            _lineups.Lineups.Add(lineup);

            if (TrySaveLineups(p))
                Chat.To(p, $"{{green}}Lineup '{name}' saved{{default}} ({GrenadeLabel(lineup.Grenade)}). Open with {{green}}!nades{{default}}.");
        });
    }

    private void OnDeleteNade(CCSPlayerController? player, CommandInfo info)
    {
        WithPractice(player, p =>
        {
            var name = info.ArgString.Trim().Trim('"');
            var lineup = _lineups.Find(name);
            if (lineup == null)
            {
                Chat.To(p, $"No lineup called '{name}'.");
                return;
            }

            _lineups.Lineups.Remove(lineup);
            if (TrySaveLineups(p))
                Chat.To(p, $"Lineup '{lineup.Name}' deleted.");
        });
    }

    private bool TrySaveLineups(CCSPlayerController player)
    {
        try
        {
            _lineups.Save();
            return true;
        }
        catch (Exception ex)
        {
            Chat.To(player, $"{{red}}Saving lineups failed: {ex.Message}");
            Plugin.Logger.LogError("Saving lineups failed: {Message}", ex.Message);
            return false;
        }
    }

    private void OpenLineupMenu(CCSPlayerController player)
    {
        if (_lineups.Lineups.Count == 0)
        {
            Chat.To(player, "No lineups saved for this map. Hold a grenade, aim, and type {green}!savenade <name>{default}.");
            return;
        }

        Plugin.Menus.Open(player, new Menu($"Lineups > {Server.MapName}", menu =>
        {
            foreach (var lineup in _lineups.Lineups.OrderBy(l => l.Grenade).ThenBy(l => l.Name))
                menu.Add($"[{GrenadeLabel(lineup.Grenade)}] {lineup.Name}", p => GoToLineup(p, lineup));
        }));
    }

    private void GoToLineup(CCSPlayerController player, NadeLineup lineup)
    {
        Plugin.Menus.Close(player);
        SetNoclip(player, false);
        Teleport(player, lineup.Position, lineup.Angles);

        if (lineup.Grenade.Length > 0)
        {
            var pawn = player.PlayerPawn.Value;
            var hasIt = pawn?.WeaponServices?.MyWeapons.Any(w => w.Value?.DesignerName == lineup.Grenade) == true;
            if (!hasIt)
                player.GiveNamedItem(lineup.Grenade);
            Server.NextFrame(() => player.ExecuteClientCommandFromServer($"use {lineup.Grenade}"));
        }

        Chat.To(player, $"Lineup {{green}}{lineup.Name}{{default}} by {lineup.Author}.");
    }

    private static string GrenadeLabel(string grenade) => GrenadeNames.GetValueOrDefault(grenade, "Any");

    // ---------------------------------------------------------------- throw tracking

    private void RegisterGrenadeTracking()
    {
        Plugin.RegisterEventHandler<EventGrenadeThrown>((e, _) =>
        {
            if (!IsActive || _dryRun || !Players.IsValid(e.Userid))
                return HookResult.Continue;

            var player = e.Userid!;
            _anyGrenadeThrown = true;
            if (TryGetPoseQuiet(player, out var pose))
                _lastThrows[player.Slot] = pose;

            var grenade = Normalize(e.Weapon ?? "");
            RecordPrediction(player, grenade);

            var key = (player.Slot, grenade);
            if (!_throwTimes.TryGetValue(key, out var queue))
                _throwTimes[key] = queue = new Queue<float>();
            queue.Enqueue(Server.CurrentTime);
            return HookResult.Continue;
        });

        Plugin.RegisterEventHandler<EventSmokegrenadeDetonate>((e, _) => Landed(e.Userid, "smokegrenade", "Smoke", new System.Numerics.Vector3(e.X, e.Y, e.Z)));
        Plugin.RegisterEventHandler<EventFlashbangDetonate>((e, _) => Landed(e.Userid, "flashbang", "Flash", new System.Numerics.Vector3(e.X, e.Y, e.Z)));
        Plugin.RegisterEventHandler<EventHegrenadeDetonate>((e, _) => Landed(e.Userid, "hegrenade", "HE", new System.Numerics.Vector3(e.X, e.Y, e.Z)));
        Plugin.RegisterEventHandler<EventMolotovDetonate>((e, _) => Landed(e.Userid, "molotov", "Molotov", new System.Numerics.Vector3(e.X, e.Y, e.Z)));

        Plugin.RegisterEventHandler<EventPlayerBlind>((e, _) =>
        {
            if (IsActive && !_dryRun)
                OnBlind(e.Userid, e.Attacker, e.BlindDuration);
            return HookResult.Continue;
        });

        Plugin.RegisterEventHandler<EventPlayerHurt>((e, _) =>
        {
            if (IsActive && !_dryRun)
                OnHurt(e.Userid, e.Attacker, e.Weapon ?? "", e.DmgHealth, e.Hitgroup);
            return HookResult.Continue;
        });
    }

    // ---------------------------------------------------------------- flashes and grenade damage

    private void ToggleNoFlash(CCSPlayerController player)
    {
        var enable = _noFlash.Add(player.Slot);
        if (!enable)
            _noFlash.Remove(player.Slot);
        Chat.To(player, $"No-flash {(enable ? "{green}on{default}: flashes won't blind you" : "{red}off")}");
    }

    private void OnBlind(CCSPlayerController? victim, CCSPlayerController? attacker, float duration)
    {
        if (!Players.IsValid(victim))
            return;

        if (Players.IsValid(attacker) && !attacker!.IsBot)
        {
            var who = attacker.Slot == victim!.Slot ? "yourself" : victim.PlayerName;
            Chat.To(attacker, $"Flashed {who} for {{green}}{duration:0.00}s");
        }

        if (_noFlash.Contains(victim!.Slot) && victim.PlayerPawn.Value is { } pawn)
        {
            pawn.FlashDuration = 0;
            pawn.FlashMaxAlpha = 0;
            pawn.BlindUntilTime = Server.CurrentTime;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashMaxAlpha");
        }
    }

    private void OnHurt(CCSPlayerController? victim, CCSPlayerController? attacker, string weapon, int damage, int hitgroup)
    {
        if (!Players.IsValid(victim) || !Players.IsValid(attacker) || attacker!.IsBot)
            return;

        var label = WeaponLabel(weapon);
        var key = (attacker.Slot, victim!.Slot, label);
        var (total, hits, headshots, _) = _nadeDamage.GetValueOrDefault(key);
        _nadeDamage[key] = (total + damage, hits + 1, headshots + (hitgroup == 1 ? 1 : 0), Server.CurrentTime);

        if (!_nadeDamageFlushScheduled)
        {
            _nadeDamageFlushScheduled = true;
            Plugin.AddTimer(1f, FlushNadeDamage, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private static string WeaponLabel(string weapon)
    {
        var name = weapon.StartsWith("weapon_") ? weapon[7..] : weapon;
        return name switch
        {
            "hegrenade" => "HE",
            "inferno" or "molotov" or "incgrenade" => "Molotov",
            "" => "Damage",
            _ => name.ToUpperInvariant()
        };
    }

    /// <summary>Reports totals once damage has stopped for a moment (sprays and molotovs hit many times).</summary>
    private void FlushNadeDamage()
    {
        _nadeDamageFlushScheduled = false;
        var now = Server.CurrentTime;

        foreach (var (key, (damage, hits, headshots, lastHit)) in _nadeDamage.ToList())
        {
            if (now - lastHit < 0.9f)
                continue;

            _nadeDamage.Remove(key);
            var attacker = Utilities.GetPlayerFromSlot(key.Attacker);
            var victim = Utilities.GetPlayerFromSlot(key.Victim);
            if (!Players.IsValid(attacker))
                continue;

            var who = !Players.IsValid(victim) ? "someone" : key.Attacker == key.Victim ? "yourself" : victim!.PlayerName;
            var isUtility = key.Weapon is "HE" or "Molotov";
            var detail = isUtility ? "" : $" in {hits} hit{(hits == 1 ? "" : "s")}{(headshots > 0 ? $" ({headshots} headshot{(headshots == 1 ? "" : "s")})" : "")}";
            Chat.To(attacker, $"{key.Weapon}: {{green}}{damage} damage{{default}}{detail} to {who}");
        }

        if (_nadeDamage.Count > 0)
        {
            _nadeDamageFlushScheduled = true;
            Plugin.AddTimer(0.5f, FlushNadeDamage, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }
    }
    /// <summary>Event weapon names come with or without the weapon_ prefix; incendiaries detonate as molotovs.</summary>
    private static string Normalize(string weapon)
    {
        var name = weapon.StartsWith("weapon_") ? weapon[7..] : weapon;
        return name == "incgrenade" ? "molotov" : name;
    }

    private HookResult Landed(CCSPlayerController? player, string grenade, string label, System.Numerics.Vector3 position)
    {
        if (!IsActive || _dryRun || !Players.IsValid(player))
            return HookResult.Continue;

        ComparePrediction(player!, grenade, position);

        if (_throwTimes.TryGetValue((player!.Slot, grenade), out var queue) && queue.TryDequeue(out var thrownAt))
            Chat.To(player, $"{label} airtime: {{green}}{Server.CurrentTime - thrownAt:0.00}s");
        return HookResult.Continue;
    }

    private void TeleportToLastThrow(CCSPlayerController player)
    {
        if (!_lastThrows.TryGetValue(player.Slot, out var pose))
        {
            Chat.To(player, "You haven't thrown a grenade yet.");
            return;
        }
        Teleport(player, pose.Position, pose.Angles);
    }

    private static bool TryGetPoseQuiet(CCSPlayerController player, out (Vector Position, QAngle Angles) pose)
    {
        pose = default;
        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null)
            return false;
        pose = (new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z),
            new QAngle(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0));
        return true;
    }
}
