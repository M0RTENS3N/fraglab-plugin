using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Practice;

/// <summary>
/// Practice: infinite money/ammo, no round end, respawns, noclip, rethrow, grenade lineups, bots.
/// !dryrun switches to competitive round settings to run through executes; !prac goes back.
/// </summary>
public sealed partial class PracticeMode : GameMode
{
    private readonly Dictionary<int, (Vector Position, QAngle Angles)> _savedPositions = new();
    private readonly Dictionary<int, int> _spawnCursor = new();
    // God mode is on by default in practice; this holds the players who turned it off.
    private readonly HashSet<int> _godOff = new();
    private readonly Queue<(Vector Position, QAngle Angles)> _pendingBots = new();
    private readonly Dictionary<int, (Vector Position, QAngle Angles)> _botPlacements = new();
    private readonly Dictionary<int, CounterStrikeSharp.API.Modules.Timers.Timer> _noBlockWatch = new();
    private bool _dryRun;
    private bool _anyGrenadeThrown;
    private float _lastWarmupEnd = -10f;

    public PracticeMode(FragLabPlugin plugin) : base(plugin)
    {
        _lineups = new LineupStore(plugin.ModuleDirectory);
    }

    public override string Name => "prac";
    public override string Description => "Practice tools and dry runs";
    public override string Title => "Practice";
    public override IEnumerable<string> Aliases => ["pracc", "practice"];

    private PracticeConfig Cfg => Plugin.Config.Practice;

    public override void Register()
    {
        AddCommand(["noclip", "nc"], "Toggle noclip", (p, _) => WithPractice(p, ToggleNoclip));
        AddCommand(["god"], "Toggle god mode (on by default)", (p, _) => WithPractice(p, ToggleGod));
        AddCommand(["noflash"], "Toggle flashbangs not blinding you", (p, _) => WithPractice(p, ToggleNoFlash));
        AddCommand(["rethrow"], "Throw the last grenade again", (p, _) => WithPractice(p, Rethrow), adminOnly: true);
        AddCommand(["last"], "Teleport to where you threw your last grenade", (p, _) => WithPractice(p, TeleportToLastThrow));
        AddCommand(["savepos"], "Save your position", (p, _) => WithPractice(p, SavePosition));
        AddCommand(["loadpos"], "Go back to your saved position", (p, _) => WithPractice(p, LoadPosition));
        AddCommand(["spawn"], "Teleport to a competitive spawn: !spawn [number]", OnSpawnCommand);
        AddCommand(["clear"], "Remove all smokes, molotovs and grenades in the air", (p, _) => WithPractice(p, _ => ClearGrenades()), adminOnly: true);
        AddCommand(["bot"], "Place a standing bot at your position", (p, _) => WithPractice(p, PlaceBot), adminOnly: true);
        AddCommand(["kickbots", "botkick", "nobots"], "Kick all bots", (p, _) => WithPractice(p, _ => KickBots()), adminOnly: true);
        AddCommand(["dryrun", "dry"], "Start a dry run with real round settings", (p, _) => StartDryRun(p), adminOnly: true);
        AddCommand(["prac", "pracc", "exitdry"], "Stop the dry run and go back to practice", (p, _) => StopDryRun(p), adminOnly: true);
        AddCommand(["fas"], "Move everyone else to spectator (you stay on your team)", OnForceAllSpectate, adminOnly: true);

        RegisterLineups();
        RegisterNadeFinder();
        RegisterGrenadeTracking();
        RegisterGhost();
        RegisterMarkers();
        RegisterSpawnSquares();

        // Bots we kick (bot_kick) would print "Player left the game"; keep that out of chat.
        Plugin.RegisterEventHandler<EventPlayerDisconnect>((e, info) =>
        {
            if (IsActive && e.Userid is { IsBot: true })
                info.DontBroadcast = true;
            return HookResult.Continue;
        });

        Plugin.RegisterEventHandler<EventItemPurchase>((e, _) =>
        {
            // Money never runs out in practice. Read the player now: the event is freed after this handler,
            // and touching it next frame crashes the server.
            var buyer = e.Userid;
            if (IsActive && !_dryRun && Players.IsValid(buyer))
                Server.NextFrame(() =>
                {
                    if (Players.IsValid(buyer))
                        SetMoney(buyer!, 60000);
                });
            return HookResult.Continue;
        });
    }

    public override void Activate()
    {
        _dryRun = false;
        _pendingBots.Clear();
        _botPlacements.Clear();
        if (_lineups.Map != Server.MapName)
            _lineups.Load(Server.MapName);
        RefreshMarkers();
        LoadSpawnToggles();
        DrawSpawnMarks();
        Plugin.AddTimer(3f, DrawSpawnMarks, TimerFlags.STOP_ON_MAPCHANGE); // the map may still be loading now

        _anyGrenadeThrown = false;
        StartMousePoll();

        // Give the config (sv_cheats etc.) a moment to apply before restarting.
        ExecConfig(Cfg.Config);
        Plugin.AddTimer(0.5f, () =>
        {
            if (!IsActive)
                return;
            Server.ExecuteCommand("bot_kick");
            Server.ExecuteCommand("mp_warmup_end");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        Plugin.AddTimer(1.0f, () =>
        {
            if (IsActive)
                Server.ExecuteCommand("mp_restartgame 1");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        Chat.All("{green}Practice mode{default} enabled. Type {green}!help{default} for practice commands, {green}!dryrun{default} to run real rounds.");
    }

    public override void Deactivate()
    {
        foreach (var player in Players.Connected())
        {
            Players.LeaveNoclipSafely(player);
            if (player.PlayerPawn.Value is { } pawn)
                pawn.TakesDamage = true;
        }

        // Once more a moment later, in case noclip was toggled with the console noclip command meanwhile.
        Plugin.AddTimer(0.5f, () =>
        {
            foreach (var player in Players.Connected())
                Players.LeaveNoclipSafely(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        _godOff.Clear();
        _noFlash.Clear();
        ResetNadeFinder();
        _savedPositions.Clear();
        _pendingBots.Clear();
        _botPlacements.Clear();
        _dryRun = false;
        StopMousePoll();
        StopGhost();
        RemoveMarkers();
        ClearSpawnMarks();
        ClearAllAimCircles();

        Server.ExecuteCommand("bot_kick");
        Server.ExecuteCommand("sv_cheats 0; sv_infinite_ammo 0; mp_ignore_round_win_conditions 0; mp_respawn_on_death_ct 0; mp_respawn_on_death_t 0");
        Server.ExecuteCommand("sv_showimpacts 0; sv_grenade_trajectory_prac_pipreview 0; sv_grenade_trajectory_prac_trailtime 0; bot_stop 0");
    }

    public override void OnMapStart(string mapName)
    {
        StopGhost();
        _markers.Clear();
        _markerSpots.Clear();
        _aimCircles.Clear();
        _spawnBeams.Clear();
        _disabledSpawns.Clear();
        _godOff.Clear();
        _noFlash.Clear();
        _savedPositions.Clear();
        _spawnCursor.Clear();
        _lastThrows.Clear();
        _pendingBots.Clear();
        _botPlacements.Clear();
        _noBlockWatch.Clear(); // timers are gone after a map change
        _searchTimer = null;
        ResetNadeFinder();
        _anyGrenadeThrown = false;
        _lastWarmupEnd = -10f;
    }

    public override void OnPlayerDisconnect(CCSPlayerController player)
    {
        _godOff.Remove(player.Slot);
        _noFlash.Remove(player.Slot);
        _finder.Remove(player.Slot);
        _nfCheck.Remove(player.Slot);
        _savedPositions.Remove(player.Slot);
        _spawnCursor.Remove(player.Slot);
        _lastThrows.Remove(player.Slot);
        _botPlacements.Remove(player.Slot);
    }

    public override void PrintStatus(CCSPlayerController? player)
    {
        Chat.To(player, _dryRun
            ? "Practice: {red}dry run{default} in progress. !prac to stop."
            : $"Practice: {_lineups.Lineups.Count} lineups saved for {Server.MapName}. !dryrun to run real rounds.");
    }

    public override void OnPlayerConnectFull(CCSPlayerController player) => EndWarmupIfRunning();

    /// <summary>
    /// After a map change the game's own settings turn warmup back on, and it only starts once the first player
    /// joins (after practice already ended it). End it again as soon as players are in.
    /// </summary>
    private void EndWarmupIfRunning()
    {
        if (Game.Rules()?.WarmupPeriod != true || Server.CurrentTime - _lastWarmupEnd < 3f)
            return;

        _lastWarmupEnd = Server.CurrentTime;
        Server.NextFrame(() =>
        {
            if (IsActive && Game.Rules()?.WarmupPeriod == true)
                Server.ExecuteCommand("mp_warmup_end");
        });
    }

    /// <summary>A round (re)start removes everything the plugin spawned, so the lineup markers are made again.</summary>
    public override void OnRoundStart()
    {
        if (!_dryRun)
            Server.NextFrame(RefreshMarkers);
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        EndWarmupIfRunning();
        if (_dryRun)
            return;

        Server.NextFrame(() =>
        {
            if (!Players.IsValid(player) || !player.PawnIsAlive)
                return;

            if (player.IsBot)
            {
                PositionBot(player);
                // Bots get kevlar and helmet like players.
                if (player.PlayerPawn.Value is { ArmorValue: < 100 })
                    player.GiveNamedItem("item_assaultsuit");
                return;
            }

            if (player.PlayerPawn.Value is { } pawn)
                pawn.TakesDamage = _godOff.Contains(player.Slot);
            SetMoney(player, 60000);
            if (Cfg.GiveAllGrenades)
                GiveAllGrenades(player);
        });
    }

    private void WithPractice(CCSPlayerController? player, Action<CCSPlayerController> action)
    {
        if (player == null || !Players.IsValid(player))
            return;
        if (_dryRun)
        {
            Chat.To(player, "Not available during a dry run. Type {green}!prac{default} to go back to practice.");
            return;
        }
        action(player);
    }

    // ---------------------------------------------------------------- movement tools

    private void ToggleNoclip(CCSPlayerController player)
    {
        if (!player.PawnIsAlive)
            return;
        var enable = player.PlayerPawn.Value?.MoveType != MoveType_t.MOVETYPE_NOCLIP;
        SetNoclip(player, enable);
        Chat.To(player, $"Noclip {(enable ? "{green}on" : "{red}off")}");
    }

    private static void SetNoclip(CCSPlayerController player, bool enable)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return;
        if (!enable && pawn.MoveType != MoveType_t.MOVETYPE_NOCLIP)
            return;

        var moveType = enable ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    private void ToggleGod(CCSPlayerController player)
    {
        var enable = _godOff.Remove(player.Slot);
        if (!enable)
            _godOff.Add(player.Slot);
        if (player.PlayerPawn.Value is { } pawn)
            pawn.TakesDamage = !enable;
        Chat.To(player, $"God mode {(enable ? "{green}on" : "{red}off")}");
    }

    private void SavePosition(CCSPlayerController player)
    {
        if (!TryGetPose(player, out var pose))
            return;
        _savedPositions[player.Slot] = pose;
        Chat.To(player, "Position saved. {green}!loadpos{default} to go back.");
    }

    private void LoadPosition(CCSPlayerController player)
    {
        if (!_savedPositions.TryGetValue(player.Slot, out var pose))
        {
            Chat.To(player, "No saved position. Use {green}!savepos{default} first.");
            return;
        }
        Teleport(player, pose.Position, pose.Angles);
    }

    private void OnSpawnCommand(CCSPlayerController? player, CommandInfo info)
    {
        WithPractice(player, p =>
        {
            var team = Players.IsPlayingTeam(p.Team) ? p.Team : CsTeam.CounterTerrorist;
            var className = team == CsTeam.Terrorist ? "info_player_terrorist" : "info_player_counterterrorist";
            var spawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(className)
                .Where(s => s.AbsOrigin != null && s.Enabled && SpawnEnabled(s))
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.Index)
                .ToList();

            if (spawns.Count == 0)
            {
                Chat.To(p, "This map has no spawns for your team.");
                return;
            }

            int index;
            if (info.ArgCount > 1 && int.TryParse(info.GetArg(1), out var requested))
                index = Math.Clamp(requested, 1, spawns.Count) - 1;
            else
                index = (_spawnCursor.GetValueOrDefault(p.Slot, -1) + 1) % spawns.Count;

            _spawnCursor[p.Slot] = index;
            var spawn = spawns[index];
            Teleport(p, spawn.AbsOrigin!, spawn.AbsRotation ?? new QAngle(0, 0, 0));
            Chat.To(p, $"Spawn {{green}}{index + 1}/{spawns.Count}{{default}} ({Players.TeamName(team)})");
        });
    }

    private static void ClearGrenades()
    {
        string[] classes =
        [
            "smokegrenade_projectile", "inferno", "molotov_projectile", "flashbang_projectile",
            "hegrenade_projectile", "decoy_projectile"
        ];

        var removed = 0;
        foreach (var className in classes)
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(className).ToList())
            {
                if (!entity.IsValid)
                    continue;
                // Let the game remove it the normal way; deleting fires directly can crash the server.
                entity.AcceptInput("Kill");
                removed++;
            }
        }

        Chat.All($"Cleared {removed} grenade(s).");
    }

    /// <summary>Smoke, flash, HE, molotov/incendiary (by team) and decoy. Infinite ammo keeps them topped up.</summary>
    /// <summary>
    /// The practice loadout, exact every spawn: knife, the side's pistol and rifle (T: AK-47, CT: M4A4), and each
    /// grenade exactly once (the fire grenade matches the side), so a side change never adds a second flash or drops one.
    /// </summary>
    /// <summary>
    /// The practice loadout, without replacing what the player already has: each grenade the player doesn't hold is
    /// given (one fire grenade only, matching the side), and the side's rifle if they hold no rifle. Replacing weapons
    /// on every spawn stripped the skins and the knife, so nothing is removed here.
    /// </summary>
    private static void GiveAllGrenades(CCSPlayerController player)
    {
        var fire = player.Team == CsTeam.CounterTerrorist ? "weapon_incgrenade" : "weapon_molotov";
        string[] grenades = ["weapon_smokegrenade", "weapon_flashbang", "weapon_hegrenade", "weapon_decoy"];
        var rifle = player.Team == CsTeam.CounterTerrorist ? "weapon_m4a1" : "weapon_ak47";

        var services = player.PlayerPawn.Value?.WeaponServices;
        if (services == null)
            return;

        var held = new HashSet<string>();
        var hasRifle = false;
        foreach (var handle in services.MyWeapons)
        {
            if (handle.Value is not { IsValid: true } weapon)
                continue;
            held.Add(weapon.DesignerName);
            if (new CCSWeaponBase(weapon.Handle).VData?.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE)
                hasRifle = true;
        }

        // A molotov from the other side counts as well: only one fire grenade at a time.
        var holdsFire = held.Contains("weapon_molotov") || held.Contains("weapon_incgrenade");

        foreach (var grenade in grenades.Where(g => !held.Contains(g)))
            player.GiveNamedItem(grenade);
        if (!holdsFire)
            player.GiveNamedItem(fire);
        if (!hasRifle)
            player.GiveNamedItem(rifle);
    }
    private void Rethrow(CCSPlayerController player)
    {
        if (!_anyGrenadeThrown)
        {
            Chat.To(player, "Throw a grenade first, then !rethrow repeats it.");
            return;
        }
        Server.ExecuteCommand("sv_rethrow_last_grenade");
    }

    // ---------------------------------------------------------------- bots

    private void PlaceBot(CCSPlayerController player)
    {
        if (!TryGetPose(player, out var pose))
            return;

        _pendingBots.Enqueue((pose.Position, new QAngle(0, pose.Angles.Y, 0)));
        Server.ExecuteCommand(player.Team == CsTeam.Terrorist ? "bot_add_ct" : "bot_add_t");
        Chat.To(player, $"Bot placed ({_botPlacements.Count + _pendingBots.Count} total). You can walk through bots. {{green}}!kickbots{{default}} removes them.");

        // If no bot joins, the server is out of player slots.
        Plugin.AddTimer(3f, () =>
        {
            if (!IsActive || _pendingBots.Count == 0)
                return;
            _pendingBots.Clear();
            Chat.To(player, "{red}No bot could join{default} - the server is probably out of player slots.");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void PositionBot(CCSPlayerController bot)
    {
        // The ghost replay bot belongs to the replay: don't place it like a !bot, and don't kick it as unwanted.
        if (_ghost is { } ghost && (ghost.BotSlot == bot.Slot || (ghost.BotSlot == WaitingForBot && !ghost.BotsBefore.Contains(bot.Slot))))
        {
            if (ghost.BotSlot == WaitingForBot)
                ClaimGhostBot(ghost, bot);
            return;
        }

        if (!_botPlacements.TryGetValue(bot.Slot, out var placement))
        {
            if (!_pendingBots.TryDequeue(out placement))
            {
                // A bot nobody asked for (the server sometimes adds two): remove it.
                Server.ExecuteCommand($"bot_kick \"{bot.PlayerName}\"");
                return;
            }
            _botPlacements[bot.Slot] = placement;
        }

        Teleport(bot, placement.Position, new QAngle(0, placement.Angles.Y, 0));
        MakePassableUntilClear(bot);
    }

    /// <summary>
    /// Lets players walk through a freshly placed bot (it's placed where you stand) and makes it solid again
    /// once nobody is inside it. Keeping it non-solid would also stop HE and molotov damage from reaching it.
    /// </summary>
    private void MakePassableUntilClear(CCSPlayerController bot)
    {
        var pawn = bot.PlayerPawn.Value;
        if (pawn == null)
            return;

        const byte passable = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING;
        var group = pawn.Collision.CollisionGroup;
        var attribute = pawn.Collision.CollisionAttribute.CollisionGroup;
        if (group == passable)
            return;

        SetCollision(pawn, passable, passable);
        StopNoBlockWatch(bot.Slot);

        _noBlockWatch[bot.Slot] = Plugin.AddTimer(0.5f, () =>
        {
            if (!Players.IsValid(bot) || !bot.PawnIsAlive || bot.PlayerPawn.Value?.Handle != pawn.Handle || pawn.AbsOrigin == null)
            {
                StopNoBlockWatch(bot.Slot);
                return;
            }

            if (HumanNear(pawn.AbsOrigin, 48f))
                return;

            SetCollision(pawn, group, attribute);
            StopNoBlockWatch(bot.Slot);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopNoBlockWatch(int slot)
    {
        if (_noBlockWatch.Remove(slot, out var timer))
            timer.Kill();
    }

    private static bool HumanNear(Vector position, float radius) =>
        Players.Humans().Any(p =>
        {
            var origin = p.PawnIsAlive ? p.PlayerPawn.Value?.AbsOrigin : null;
            if (origin == null)
                return false;
            var dx = origin.X - position.X;
            var dy = origin.Y - position.Y;
            return dx * dx + dy * dy < radius * radius && Math.Abs(origin.Z - position.Z) < 80f;
        });

    private static void SetCollision(CCSPlayerPawn pawn, byte group, byte attribute)
    {
        pawn.Collision.CollisionGroup = group;
        pawn.Collision.CollisionAttribute.CollisionGroup = attribute;
        Utilities.SetStateChanged(pawn, "CCollisionProperty", "m_CollisionGroup");
        Utilities.SetStateChanged(pawn, "CCollisionProperty", "m_collisionAttribute");
    }
    private void OnForceAllSpectate(CCSPlayerController? admin, CommandInfo info)
    {
        var moved = 0;
        foreach (var p in Players.Playing().Where(p => !p.IsBot && p.Slot != admin?.Slot).ToList())
        {
            p.ChangeTeam(CsTeam.Spectator);
            moved++;
        }
        Chat.All($"All other players moved to spectator ({moved}).");
    }

    private void KickBots()
    {
        foreach (var slot in _noBlockWatch.Keys.ToList())
            StopNoBlockWatch(slot);
        _pendingBots.Clear();
        _botPlacements.Clear();
        Server.ExecuteCommand("bot_kick");
        Chat.All("All bots kicked.");
    }

    // ---------------------------------------------------------------- dry run

    private void StartDryRun(CCSPlayerController? player)
    {
        if (_dryRun)
        {
            Chat.To(player, "Restarting the dry run.");
            Server.ExecuteCommand("mp_restartgame 1");
            return;
        }

        _dryRun = true;
        foreach (var p in Players.Connected())
        {
            SetNoclip(p, false);
            if (p.PlayerPawn.Value is { } pawn)
                pawn.TakesDamage = true;
        }

        if (Cfg.DryRunKicksBots)
        {
            _pendingBots.Clear();
            _botPlacements.Clear();
            Server.ExecuteCommand("bot_kick");
        }

        ExecConfig(Cfg.DryRunConfig);
        Server.ExecuteCommand("mp_restartgame 1");

        var who = player == null ? "Console" : player.PlayerName;
        Chat.All($"{{red}}DRY RUN{{default}} started by {who}: freeze time, buy time and full money every round.");
        Chat.All("Type {green}!prac{default} to go back to practice.");
    }

    private void StopDryRun(CCSPlayerController? player)
    {
        if (!_dryRun)
        {
            Chat.To(player, "No dry run is running. Type {green}!dryrun{default} to start one.");
            return;
        }

        _dryRun = false;
        ExecConfig(Cfg.Config);
        Server.ExecuteCommand("mp_restartgame 1");
        Chat.All("Dry run over, {green}back to practice{default}.");
    }

    // ---------------------------------------------------------------- helpers

    private static bool TryGetPose(CCSPlayerController player, out (Vector Position, QAngle Angles) pose)
    {
        pose = default;
        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null || !player.PawnIsAlive)
        {
            Chat.To(player, "You must be alive to do that.");
            return false;
        }

        pose = (new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z),
            new QAngle(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0));
        return true;
    }

    private static void Teleport(CCSPlayerController player, Vector position, QAngle angles)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return;

        // The full angles point the view, but they also tilt the body model with the pitch (you'd see your
        // own legs when looking down), so the body is set straight again right after.
        pawn.Teleport(position, angles, new Vector(0, 0, 0));
        KeepBodyUpright(pawn);
        Server.NextFrame(() =>
        {
            if (pawn.IsValid)
                KeepBodyUpright(pawn);
        });
    }

    private static void KeepBodyUpright(CCSPlayerPawn pawn)
    {
        var node = pawn.CBodyComponent?.SceneNode;
        if (node == null)
            return;

        node.Rotation.X = 0;
        node.Rotation.Z = 0;
        node.AbsRotation.X = 0;
        node.AbsRotation.Z = 0;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
    }
    private static void SetMoney(CCSPlayerController player, int amount)
    {
        var money = player.InGameMoneyServices;
        if (money == null)
            return;
        money.Account = amount;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }
}
