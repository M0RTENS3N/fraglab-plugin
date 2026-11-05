using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using FragLab.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Chaos;

/// <summary>
/// Chaos Gun Game: free for all. Every kill moves you one weapon up the ladder; the knife is the last level
/// and a knife kill there wins. A knife kill on anyone steals a level from them. Every ChaosInterval seconds
/// a random chaos event changes the rules for a while (low gravity, one-shot kills, vampire, ghosts...).
/// The leader glows through walls. Kill streaks heal you and give speed.
/// </summary>
public sealed partial class ChaosMode : GameMode
{
    private const string Knife = "weapon_knife";

    private sealed class Stats
    {
        public int Level;
        public int Kills;
        public int Deaths;
        public int Streak;
        public int BestStreak;
        public float LevelReachedAt;
    }

    private readonly Dictionary<int, Stats> _stats = new();
    private readonly Dictionary<int, float> _protectedUntil = new();
    private readonly Dictionary<int, float> _speedUntil = new();
    private Timer? _tick;
    private Timer? _restartTimer;
    private bool _gameOver;
    private int _leaderSlot = -1;

    public ChaosMode(FragLabPlugin plugin) : base(plugin) { }

    public override string Name => "chaos";
    public override string Description => "Chaos Gun Game: free for all weapon ladder with random chaos events";
    public override string Title => "Chaos Gun Game";
    public override IEnumerable<string> Aliases => ["gungame", "gg", "chaosgg"];

    private ChaosConfig Cfg => Plugin.Config.Chaos;

    /// <summary>Levels: the ladder, then the knife.</summary>
    private int LevelCount => Cfg.Ladder.Count + 1;

    private string WeaponAt(int level) => level < Cfg.Ladder.Count ? Cfg.Ladder[level] : Knife;

    public override void Register()
    {
        AddCommand(["level", "lvl"], "Show your level and the next weapons", (p, _) => ShowLevel(p));
        AddCommand(["ladder"], "Show the whole weapon ladder", (p, _) => ShowLadder(p));
        AddCommand(["top", "leaders"], "Show the top players", (p, _) => ShowTop(p));
        AddCommand(["chaos"], "Show the current chaos event", (p, _) => ShowChaos(p));
        AddCommand(["chaosnow"], "Start a random chaos event now: !chaosnow [name]", OnChaosNow, adminOnly: true);
        AddCommand(["ggrestart"], "Restart the gun game", (_, _) => RestartGame(), adminOnly: true);

        Plugin.RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (IsActive)
                OnDeath(e.Userid, e.Attacker, e.Weapon ?? "", e.Headshot);
            return HookResult.Continue;
        });

        Plugin.RegisterEventHandler<EventWeaponFire>((e, _) =>
        {
            // Shooting ends your spawn protection.
            if (IsActive && Players.IsValid(e.Userid))
                EndProtection(e.Userid!.Slot);
            return HookResult.Continue;
        });

        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(OnDamagePre);
        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePost>(OnDamagePost);
        Plugin.RegisterListener<Listeners.CheckTransmit>(HideLeaderGlowFromLeader);
    }

    public override void Activate()
    {
        StopTimers();
        ResetGame();

        ExecConfig(Cfg.Config);
        Plugin.AddTimer(0.5f, () =>
        {
            if (!IsActive)
                return;
            Server.ExecuteCommand("bot_kick");
            Server.ExecuteCommand("mp_warmup_end");
            if (Cfg.FillBots > 0)
                Server.ExecuteCommand($"bot_quota_mode fill; bot_quota {Cfg.FillBots}");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        Plugin.AddTimer(1.0f, () =>
        {
            if (IsActive)
                Server.ExecuteCommand("mp_restartgame 1");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        _tick = Plugin.AddTimer(0.25f, Tick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Chat.All($"{{green}}Chaos Gun Game{{default}}! {LevelCount} levels, every kill gives you the next weapon. " +
                 "Knife kills steal a level. Expect chaos. Type {green}!ladder{default} to see the weapons.");
    }

    public override void Deactivate()
    {
        StopTimers();
        EndChaos(announce: false);
        RemoveLeaderGlow();
        ResetGame();
        foreach (var player in Players.Connected())
        {
            if (player.PlayerPawn.Value is { } pawn)
                pawn.VelocityModifier = 1f;
        }
        Server.ExecuteCommand("bot_kick; bot_quota 0; mp_teammates_are_enemies 0; mp_randomspawn 0; sv_infinite_ammo 0");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0; mp_respawn_on_death_ct 0; mp_respawn_on_death_t 0; mp_weapons_allow_map_placed 1");
    }

    public override void OnMapStart(string mapName)
    {
        StopTimers();
        _chaosBackup.Clear();
        _chaos = null;
        _leaderGlow = null;
        ResetSpawns();
        ResetGame();
    }

    public override void PrintStatus(CCSPlayerController? player) => ShowTop(player);

    private void StopTimers()
    {
        _tick?.Kill();
        _tick = null;
        _restartTimer?.Kill();
        _restartTimer = null;
    }

    private void ResetGame()
    {
        _stats.Clear();
        _protectedUntil.Clear();
        _speedUntil.Clear();
        _gameOver = false;
        _leaderSlot = -1;
        _nextChaosAt = Server.CurrentTime + Cfg.ChaosInterval;
    }

    private void RestartGame()
    {
        _restartTimer?.Kill();
        _restartTimer = null;
        EndChaos(announce: false);
        RemoveLeaderGlow();
        ResetGame();
        Server.ExecuteCommand("mp_restartgame 1");
        Chat.All("{green}New game!{default} Everyone is back to level 1.");
    }

    private Stats StatsOf(CCSPlayerController player)
    {
        if (!_stats.TryGetValue(player.Slot, out var stats))
        {
            // Late joiners start at the lowest level anyone has, so they aren't hopelessly behind... or ahead.
            var lowest = _stats.Count == 0 ? 0 : _stats.Values.Min(s => s.Level);
            _stats[player.Slot] = stats = new Stats { Level = lowest, LevelReachedAt = Server.CurrentTime };
        }
        return stats;
    }

    public override void OnPlayerDisconnect(CCSPlayerController player)
    {
        _stats.Remove(player.Slot);
        if (player.Slot == _leaderSlot)
            UpdateLeader(announce: false);
    }

    // ---------------------------------------------------------------- spawning

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (!Players.IsPlayingTeam(player.Team))
            return;

        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p == null || !p.PawnIsAlive || p.PlayerPawn.Value is not { } pawn)
                return;

            PlaceSpawn(p, pawn);
            GiveLevelWeapon(p);
            StartProtection(pawn, slot);
            ApplyChaosOnSpawn(p, pawn);
            if (slot == _leaderSlot)
                CreateLeaderGlow(p);
        });
    }

    private void GiveLevelWeapon(CCSPlayerController player)
    {
        if (!player.PawnIsAlive)
            return;

        var weapon = WeaponAt(StatsOf(player).Level);
        player.RemoveWeapons();
        player.GiveNamedItem(Knife);
        if (weapon != Knife)
            player.GiveNamedItem(weapon);
    }

    // ---------------------------------------------------------------- kills

    private void OnDeath(CCSPlayerController? victim, CCSPlayerController? attacker, string weapon, bool headshot)
    {
        if (!Players.IsValid(victim) || _gameOver)
            return;

        var victimStats = StatsOf(victim!);
        victimStats.Deaths++;
        victimStats.Streak = 0;
        _speedUntil.Remove(victim!.Slot);
        if (victim.Slot == _leaderSlot)
            RemoveLeaderGlow();

        if (_chaos == ChaosEvent.Explosive)
            Explode(victim);

        if (!Players.IsValid(attacker) || attacker!.Slot == victim.Slot)
            return;

        var stats = StatsOf(attacker);
        stats.Kills++;
        stats.Streak++;
        stats.BestStreak = Math.Max(stats.BestStreak, stats.Streak);

        var knife = weapon.Contains("knife") || weapon.Contains("bayonet");
        if (knife && victimStats.Level > 0 && stats.Level < LevelCount - 1)
        {
            victimStats.Level--;
            victimStats.LevelReachedAt = Server.CurrentTime;
            Chat.All($"{{red}}HUMILIATION!{{default}} {attacker.PlayerName} knifed {victim.PlayerName} and stole a level.");
        }

        if (stats.Level >= LevelCount - 1)
        {
            // Last level: only the knife is held, so this kill wins.
            Win(attacker);
            return;
        }

        stats.Level++;
        stats.LevelReachedAt = Server.CurrentTime;
        var next = WeaponAt(stats.Level);
        Chat.To(attacker, stats.Level == LevelCount - 1
            ? "{red}KNIFE LEVEL!{default} Get a knife kill to win!"
            : $"Level {{green}}{stats.Level + 1}/{LevelCount}{{default}}: {WeaponName(next)}{(headshot ? " {gold}(headshot){default}" : "")}");
        if (stats.Level == LevelCount - 1)
            Chat.All($"{{red}}{attacker.PlayerName} is on the knife level!{{default}} One knife kill and they win.");

        StreakRewards(attacker, stats.Streak);

        var slot = attacker.Slot;
        Server.NextFrame(() =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p != null && Players.IsValid(p))
                GiveLevelWeapon(p);
        });

        UpdateLeader(announce: true);
    }

    private void StreakRewards(CCSPlayerController player, int streak)
    {
        if (player.PlayerPawn.Value is not { } pawn || !player.PawnIsAlive)
            return;

        if (Cfg.StreakHealKills > 0 && streak == Cfg.StreakHealKills)
        {
            SetHealth(pawn, Math.Max(pawn.Health, 100), Math.Max(pawn.MaxHealth, 100));
            player.GiveNamedItem("item_assaultsuit");
            Chat.All($"{{gold}}{player.PlayerName}{{default}} is on a {streak} kill streak: healed!");
        }
        else if (Cfg.StreakSpeedKills > 0 && streak == Cfg.StreakSpeedKills)
        {
            _speedUntil[player.Slot] = Server.CurrentTime + Cfg.StreakSpeedSeconds;
            Chat.All($"{{red}}RAMPAGE!{{default}} {player.PlayerName} has {streak} kills in a row and gets a speed boost.");
        }
        else if (streak == 10)
        {
            Chat.All($"{{red}}UNSTOPPABLE!{{default}} {player.PlayerName} has 10 kills in a row!");
        }
    }

    private void Win(CCSPlayerController winner)
    {
        _gameOver = true;
        EndChaos(announce: false);
        RemoveLeaderGlow();

        Chat.All($"{{gold}}{winner.PlayerName} WINS THE CHAOS GUN GAME!{{default}}");
        ShowTop(null, toAll: true);
        foreach (var player in Players.Connected())
            player.PrintToCenter($"{winner.PlayerName} wins!");

        _restartTimer = Plugin.AddTimer(Cfg.RestartSeconds, RestartGame, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- leader

    private void UpdateLeader(bool announce)
    {
        var leader = Players.Playing()
            .Where(p => _stats.ContainsKey(p.Slot))
            .OrderByDescending(p => _stats[p.Slot].Level)
            .ThenBy(p => _stats[p.Slot].LevelReachedAt)
            .FirstOrDefault();

        var slot = leader?.Slot ?? -1;
        if (slot == _leaderSlot)
            return;

        RemoveLeaderGlow();
        _leaderSlot = slot;
        if (leader == null)
            return;

        if (announce)
            Chat.All($"{{gold}}{leader.PlayerName}{{default}} takes the lead on level {_stats[slot].Level + 1}! {(Cfg.LeaderGlow ? "They glow now." : "")}");
        if (leader.PawnIsAlive)
            CreateLeaderGlow(leader);
    }

    // ---------------------------------------------------------------- tick

    private void Tick()
    {
        if (!IsActive)
            return;

        TickChaos();
        RecordSpawnCrumbs();
        CheckProtectionExpiry();
        ApplySpeeds();
        if (!_gameOver)
            ShowHud();
    }

    private void ApplySpeeds()
    {
        var now = Server.CurrentTime;
        foreach (var player in Players.Playing().Where(p => p.PawnIsAlive))
        {
            var target = 1f;
            if (_chaos == ChaosEvent.SpeedRush)
                target = 1.45f;
            if (_speedUntil.GetValueOrDefault(player.Slot) > now)
                target = Math.Max(target, 1.3f);
            if (target == 1f || player.PlayerPawn.Value is not { } pawn || pawn.VelocityModifier >= target)
                continue;
            pawn.VelocityModifier = target;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        }
    }

    private int _hudCounter;

    private void ShowHud()
    {
        // The tick runs 4 times a second; the HUD only needs 2.
        if (++_hudCounter % 2 != 0)
            return;

        var leader = _leaderSlot >= 0 ? Utilities.GetPlayerFromSlot(_leaderSlot) : null;
        var leaderText = leader != null && _stats.TryGetValue(_leaderSlot, out var ls) ? $"Leader: {leader.PlayerName} ({ls.Level + 1})" : "";
        var chaosText = ChaosHud();

        foreach (var player in Players.Humans().Where(p => Players.IsPlayingTeam(p.Team)))
        {
            if (Plugin.Menus.IsOpen(player))
                continue;
            var stats = StatsOf(player);
            var line = $"Level {stats.Level + 1}/{LevelCount}: {WeaponName(WeaponAt(stats.Level))}";
            if (stats.Level + 1 < LevelCount)
                line += $"  >  {WeaponName(WeaponAt(stats.Level + 1))}";
            var protectedText = IsProtected(player.Slot) ? "\nSPAWN PROTECTION (ends when you shoot)" : "";
        }
    }

    // ---------------------------------------------------------------- damage

    private HookResult OnDamagePre(CBaseEntity victimEntity, CTakeDamageInfo info)
    {
        if (!IsActive || victimEntity.DesignerName != "player")
            return HookResult.Continue;

        if (_gameOver)
        {
            info.Damage = 0;
            return HookResult.Continue;
        }

        var victim = new CCSPlayerPawn(victimEntity.Handle).OriginalController.Value;
        if (victim != null && IsProtected(victim.Slot))
        {
            info.Damage = 0;
            return HookResult.Continue;
        }

        ChaosDamage(info);
        return HookResult.Continue;
    }

    private void OnDamagePost(CBaseEntity victimEntity, CTakeDamageInfo info, CTakeDamageResult result)
    {
        if (!IsActive || _chaos != ChaosEvent.Vampire || victimEntity.DesignerName != "player" || result.HealthLost <= 0)
            return;

        // Vampire: the attacker heals the health they took.
        if (info.Attacker.Value is not { DesignerName: "player" } attackerEntity)
            return;
        var attackerPawn = new CCSPlayerPawn(attackerEntity.Handle);
        if (attackerPawn.Handle == victimEntity.Handle || attackerPawn.Health <= 0)
            return;
        var health = Math.Min(200, attackerPawn.Health + result.HealthLost);
        SetHealth(attackerPawn, health, Math.Max(attackerPawn.MaxHealth, health));
    }

    // ---------------------------------------------------------------- info commands

    private void ShowLevel(CCSPlayerController? player)
    {
        if (player == null)
            return;
        var stats = StatsOf(player);
        var next = Enumerable.Range(stats.Level + 1, 3).Where(l => l < LevelCount).Select(l => WeaponName(WeaponAt(l)));
        Chat.To(player, $"Level {{green}}{stats.Level + 1}/{LevelCount}{{default}}: {WeaponName(WeaponAt(stats.Level))}. " +
                        $"Next: {string.Join(", ", next)}. Kills {stats.Kills}, deaths {stats.Deaths}, best streak {stats.BestStreak}.");
    }

    private void ShowLadder(CCSPlayerController? player)
    {
        var levels = Enumerable.Range(0, LevelCount).Select(l => $"{l + 1}. {WeaponName(WeaponAt(l))}");
        foreach (var chunk in levels.Chunk(7))
            Chat.To(player, string.Join("  ", chunk));
    }

    private void ShowTop(CCSPlayerController? player, bool toAll = false)
    {
        var top = _stats
            .Select(s => (Player: Utilities.GetPlayerFromSlot(s.Key), Stats: s.Value))
            .Where(t => t.Player != null && Players.IsValid(t.Player))
            .OrderByDescending(t => t.Stats.Level).ThenByDescending(t => t.Stats.Kills)
            .Take(3).ToList();

        if (top.Count == 0)
        {
            Chat.To(player, "No kills yet.");
            return;
        }

        var place = 1;
        foreach (var (p, s) in top)
        {
            var line = $"{{gold}}#{place++}{{default}} {p!.PlayerName}: level {s.Level + 1}, {s.Kills} kills, {s.Deaths} deaths, best streak {s.BestStreak}";
            if (toAll)
                Chat.All(line);
            else
                Chat.To(player, line);
        }
    }

    private static string WeaponName(string weapon) => weapon switch
    {
        "weapon_knife" => "KNIFE",
        "weapon_m4a1_silencer" => "M4A1-S",
        "weapon_usp_silencer" => "USP-S",
        "weapon_galilar" => "GALIL",
        "weapon_sg556" => "SG 553",
        _ => weapon.Replace("weapon_", "").ToUpperInvariant()
    };

    private static void SetHealth(CCSPlayerPawn pawn, int health, int maxHealth)
    {
        pawn.MaxHealth = maxHealth;
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }
}
