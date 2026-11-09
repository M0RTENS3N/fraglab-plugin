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


    // Throw times per player and grenade type, used to print the airtime when it lands.
    private readonly Dictionary<(int Slot, string Grenade), Queue<float>> _throwTimes = new();

    private void RegisterLineups()
    {
        AddCommand(["nades", "lineups"], "Open the lineup menu: !nades [smoke|flash|he|molotov|decoy|jump|run|duck|full|medium|short]", OnNades);
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

            var pawn = p.PlayerPawn.Value;
            var sim = new NadeSimulator(pawn!);
            var start = new System.Numerics.Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            var airborne = pawn != null && (pawn.Flags & 1) == 0; // FL_ONGROUND not set
            if (airborne)
                Chat.To(p, "You were in the air, so the lineup is saved on the floor below you. Stand still on the ground for exact spots.");
            var ground = airborne ? sim.FloorBelow(start) : start;
            var aim = AimPoint(sim, ground, pose.Angles.X, pose.Angles.Y);

            var active = p.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.DesignerName ?? "";            var lineup = new NadeLineup
            {
                Name = name,
                Grenade = GrenadeNames.ContainsKey(active) ? active : "",
                Author = p.PlayerName,
                X = ground.X,
                Y = ground.Y,
                Z = ground.Z,
                Pitch = pose.Angles.X,
                Yaw = pose.Angles.Y,
                HasAim = true,
                AimX = aim.X,
                AimY = aim.Y,
                AimZ = aim.Z
            };

            var existing = _lineups.Find(name);
            if (existing != null)
                _lineups.Lineups.Remove(existing);
            _lineups.Lineups.Add(lineup);

            if (TrySaveLineups(p))
            {
                _pendingLineups[p.Slot] = (lineup, Server.CurrentTime);
                Chat.To(p, $"{{green}}Lineup '{name}' saved{{default}} ({GrenadeLabel(lineup.Grenade)}). Throw it now to record the technique.");
            }
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
            RefreshMarkers();
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

        var filter = _lineupFilter.GetValueOrDefault(player.Slot, "");
        var shown = _lineups.Lineups.Where(l => Matches(l, filter)).OrderBy(l => l.Grenade).ThenBy(l => l.Name).ToList();
        if (shown.Count == 0)
        {
            Chat.To(player, $"No lineups match {{green}}{filter}{{default}}. Try {{green}}!nades{{default}} to see all of them.");
            return;
        }

        var title = filter.Length == 0 ? $"Lineups > {Server.MapName}" : $"Lineups > {Server.MapName} > {filter}";
        Plugin.Menus.Open(player, new Menu(title, menu =>
        {
            foreach (var lineup in shown)
                menu.Add($"[{GrenadeLabel(lineup.Grenade)}] {lineup.Name}{TechniqueLabel(lineup)}", p => OpenLineupDetail(p, lineup));
        }));
    }

    private static string TechniqueLabel(NadeLineup lineup) =>
        lineup.Throw.Length == 0 ? "" : $"  ({lineup.Throw}, {lineup.Strength} click)";

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

        Chat.To(player, $"Lineup {{green}}{lineup.Name}{{default}} by {lineup.Author}{TechniqueLabel(lineup)}. Aim at the green circle.");
        ShowAimCircle(player, lineup);

        // Lineups saved at the same spot are a stack: throw them one after another.
        var stack = _lineups.Lineups.Where(l => l != lineup && LineupDistance(l.Position, lineup.Position) <= 16f).ToList();
        if (stack.Count > 0)
            Chat.To(player, $"Stacked here: {string.Join(", ", stack.Select(s => s.Name))}. Throw them one after another.");
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
            RecordThrowInfo(player, grenade);
            CheckFinderThrow(player, grenade);
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
        if (!Players.IsValid(victim) || !Players.IsValid(attacker) || attacker!.IsBot || victim!.Slot == attacker.Slot)
            return;

        var health = Math.Max(0, victim.PlayerPawn.Value?.Health ?? 0);
        var head = hitgroup == 1 ? " (headshot)" : "";
        Chat.To(attacker, $"{WeaponLabel(weapon)}: {{green}}-{damage}{{default}} to {victim.PlayerName} ({health} hp){head}");
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
    private static string Normalize(string weapon)
    {
        var name = weapon.StartsWith("weapon_") ? weapon[7..] : weapon;
        return name == "incgrenade" ? "molotov" : name;
    }

    private HookResult Landed(CCSPlayerController? player, string grenade, string label, System.Numerics.Vector3 position)
    {
        if (!IsActive || _dryRun || !Players.IsValid(player))
            return HookResult.Continue;

        CheckFinderLanding(player!, grenade, position);
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
