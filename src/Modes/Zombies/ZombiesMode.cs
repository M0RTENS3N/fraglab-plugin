using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Zombies;

/// <summary>
/// COD-style zombies: humans (CT) survive waves of knife bots (T). Every wave has more and stronger zombies.
/// Hits and kills give points (shown as money) to spend in !shop on weapons, ammo, armor and perks.
/// Dead players come back at the start of the next wave; when everyone is down the game restarts at wave 1.
/// One CS round lasts the whole game: the plugin runs the waves itself.
/// </summary>
public sealed partial class ZombiesMode : GameMode
{
    private Timer? _tick;
    private Timer? _phaseTimer;
    private int _wave;
    private bool _waveActive;
    private bool _gameOver;
    private float _waveStartedAt;
    private int _waveTotal;
    private int _killed;
    private readonly Dictionary<int, float> _respawnAt = new(); // zombie bot slot -> time it may come back

    public ZombiesMode(FragLabPlugin plugin) : base(plugin) { }

    public override string Name => "zombies";
    public override string Description => "COD-style zombie waves with points, a shop and perks";
    public override IEnumerable<string> Aliases => ["zombie", "zm"];

    private ZombiesConfig Cfg => Plugin.Config.Zombies;

    public override void Register()
    {
        AddCommand(["wave"], "Show the current wave and zombies left", (p, _) => ShowWave(p));
        AddCommand(["zrestart"], "Restart zombies from wave 1", (_, _) => RestartGame(), adminOnly: true);
        RegisterShop();
        RegisterDoors();
        RegisterEditor();

        Plugin.RegisterEventHandler<EventPlayerHurt>((e, _) =>
        {
            var attacker = e.Attacker;
            var victim = e.Userid;
            if (IsActive && _waveActive && IsHuman(attacker) && IsZombie(victim))
                AddPoints(attacker!, Cfg.PointsPerHit);
            if (IsActive && IsHuman(victim))
                OnHumanHurt(victim!);
            return HookResult.Continue;
        });

        // Our own handler: the points need the headshot flag and the weapon.
        Plugin.RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (IsActive)
                OnDeath(e.Userid, e.Attacker, e.Headshot, e.Weapon ?? "");
            return HookResult.Continue;
        });

        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(OnDamagePre);
        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePost>(OnDamagePost);
    }

    public override void Activate()
    {
        StopTimers();
        ResetState();

        ExecConfig(Cfg.Config);
        Server.ExecuteCommand($"sv_jump_impulse {Cfg.JumpImpulse.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Server.ExecuteCommand($"mp_startmoney {Cfg.StartPoints}");
        Plugin.AddTimer(0.5f, () =>
        {
            if (!IsActive)
                return;
            Server.ExecuteCommand("bot_kick");
            Server.ExecuteCommand("mp_warmup_end");
            Server.ExecuteCommand($"bot_quota {Cfg.MaxZombiesAlive}");
            foreach (var player in Players.Humans().Where(p => p.Team == CsTeam.Terrorist))
                player.ChangeTeam(CsTeam.CounterTerrorist);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        Plugin.AddTimer(1.0f, () =>
        {
            if (IsActive)
                RestartGame();
        }, TimerFlags.STOP_ON_MAPCHANGE);

        _tick = Plugin.AddTimer(0.5f, Tick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        LoadDoors();

        Chat.All("{green}Zombies{default} enabled. Survive the waves! Type {green}!shop{default} to spend your points.");
    }

    public override void Deactivate()
    {
        StopTimers();
        ResetState();
        RemoveDoorBeams();
        foreach (var player in Players.Connected())
        {
            if (player.PlayerPawn.Value is { } pawn)
                pawn.VelocityModifier = 1f;
        }

        Server.ExecuteCommand("bot_kick; bot_quota 0; bot_knives_only 0; bot_join_team any; sv_jump_impulse 301.993377");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 0; mp_respawn_on_death_ct 0; mp_respawn_on_death_t 0");
    }

    public override void OnMapStart(string mapName)
    {
        StopTimers();
        ResetState();
        // The map change removed the door lasers; Activate loads the new map's doors.
        _doorBeams.Clear();
        _doorsMap = null;
    }

    public override void PrintStatus(CCSPlayerController? player) => ShowWave(player);

    private void ResetState()
    {
        _wave = 0;
        _waveActive = false;
        _gameOver = false;
        _waveTotal = 0;
        _killed = 0;
        _respawnAt.Clear();
        ResetShop();
        ResetExtras();
        ResetAi();
        _editMode = false;
    }

    private void StopTimers()
    {
        _tick?.Kill();
        _tick = null;
        _phaseTimer?.Kill();
        _phaseTimer = null;
    }

    // ---------------------------------------------------------------- teams

    public override HookResult OnJoinTeamCommand(CCSPlayerController player, CommandInfo info)
    {
        if (player.IsBot || info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out var requested))
            return HookResult.Continue;

        // Humans can't join the zombies.
        if ((CsTeam)requested == CsTeam.Terrorist)
        {
            player.ChangeTeam(CsTeam.CounterTerrorist);
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    public override void OnPlayerConnectFull(CCSPlayerController player)
    {
        if (player.IsBot)
            return;

        Plugin.AddTimer(1.0f, () =>
        {
            if (!IsActive || !Players.IsValid(player))
                return;
            if (player.Team != CsTeam.CounterTerrorist)
                player.ChangeTeam(CsTeam.CounterTerrorist);
            SetPoints(player, Cfg.StartPoints);
            Chat.To(player, $"{{green}}Zombies{{default}}: wave {_wave}. You join at the start of the next wave. Type {{green}}!shop{{default}} to buy.");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    public override void OnPlayerDisconnect(CCSPlayerController player) => ClearPerks(player.Slot);

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (IsZombie(player))
        {
            var slot = player.Slot;
            Server.NextFrame(() =>
            {
                var bot = Utilities.GetPlayerFromSlot(slot);
                if (bot == null || !bot.PawnIsAlive || bot.PlayerPawn.Value is not { } pawn)
                    return;

                // Zombies only exist during a wave (the round start respawns everyone).
                if (!_waveActive)
                {
                    bot.CommitSuicide(false, true);
                    return;
                }
                SetupZombie(bot, pawn);
            });
            return;
        }

        if (IsHuman(player))
            ClearPerks(player.Slot);
    }

    // ---------------------------------------------------------------- waves

    private void RestartGame()
    {
        _phaseTimer?.Kill();
        ResetState();
        ResetDoors();
        Server.ExecuteCommand($"bot_quota {Cfg.MaxZombiesAlive}");
        Server.ExecuteCommand($"mp_startmoney {Cfg.StartPoints}");
        Server.ExecuteCommand("mp_restartgame 1");
        Chat.All("{green}Zombies{default}: the first wave starts in a few seconds. Buy with {green}!shop{default}.");
        _phaseTimer = Plugin.AddTimer(5f, StartNextWave, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StartNextWave()
    {
        if (!IsActive)
            return;

        _wave++;
        _killed = 0;
        _respawnAt.Clear();
        var humans = Humans().Count();
        _waveTotal = Cfg.ZombiesFirstWave + Cfg.ZombiesPerWave * (_wave - 1) + Cfg.ZombiesPerExtraPlayer * Math.Max(0, humans - 1);
        _waveActive = true;
        _gameOver = false;
        _waveStartedAt = Server.CurrentTime;
        StartWaveExtras();

        // Everyone who went down comes back for the new wave.
        foreach (var player in Humans().Where(p => !p.PawnIsAlive))
            player.Respawn();

        Chat.All($"{{red}}WAVE {_wave}{{default}}: {_waveTotal} zombies with {ZombieHealth()} HP.");
    }

    private void EndWave()
    {
        _waveActive = false;
        foreach (var bot in Zombies().Where(z => z.PawnIsAlive))
            bot.CommitSuicide(false, true);

        Chat.All($"{{green}}Wave {_wave} cleared!{{default}} Next wave in {Cfg.WaveBreakSeconds:0} seconds. Fallen players come back then.");
        _phaseTimer = Plugin.AddTimer(Cfg.WaveBreakSeconds, StartNextWave, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void GameOver()
    {
        _waveActive = false;
        _gameOver = true;
        Chat.All($"{{red}}GAME OVER{{default}}: you survived {{green}}{Math.Max(0, _wave - 1)}{{default}} waves. Restarting in 8 seconds.");
        _phaseTimer = Plugin.AddTimer(8f, RestartGame, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void Tick()
    {
        if (!IsActive)
            return;

        if (_editMode)
        {
            EditorTick();
            return;
        }

        ApplySpeeds();
        RecordCrumbs();
        CheckPowerUps();
        RegenHealth();
        ShowHud();

        if (!_waveActive)
            return;

        ZombieAi();

        // Everyone down (a grace period after the wave start lets the respawns go through).
        var humans = Humans().ToList();
        if (humans.Count > 0 && humans.All(h => !h.PawnIsAlive) && Server.CurrentTime - _waveStartedAt > 2f)
        {
            GameOver();
            return;
        }

        if (_killed >= _waveTotal)
        {
            EndWave();
            return;
        }

        var zombies = Zombies().ToList();
        var alive = zombies.Count(z => z.PawnIsAlive);
        var left = _waveTotal - _killed - alive;
        foreach (var bot in zombies.Where(z => !z.PawnIsAlive))
        {
            if (left <= 0 || alive >= Cfg.MaxZombiesAlive)
                break;
            if (_respawnAt.GetValueOrDefault(bot.Slot) > Server.CurrentTime)
                continue;
            bot.Respawn();
            alive++;
            left--;
        }
    }

    private void ShowHud()
    {
        var text = _waveActive
            ? $"Wave {_wave}  |  Zombies left: {Math.Max(0, _waveTotal - _killed)}{PowerUpHud()}"
            : _gameOver ? "GAME OVER" : $"Break - wave {_wave + 1} is coming{PowerUpHud()}";
        foreach (var player in Humans())
        {
            if (!Plugin.Menus.IsOpen(player) && !_boxRolls.ContainsKey(player.Slot))
                player.PrintToCenter(text + DoorHint(player));
        }
    }

    private void ShowWave(CCSPlayerController? player)
    {
        if (_gameOver)
            Chat.To(player, "Game over, restarting soon.");
        else if (!_waveActive)
            Chat.To(player, $"Break after wave {_wave}. The next wave starts soon.");
        else
            Chat.To(player, $"Wave {{red}}{_wave}{{default}}: {Math.Max(0, _waveTotal - _killed)} of {_waveTotal} zombies left.");
    }

    // ---------------------------------------------------------------- zombies

    private int ZombieHealth() => Cfg.ZombieHealthFirstWave + Cfg.ZombieHealthPerWave * Math.Max(0, _wave - 1);

    private float ZombieSpeed() => Math.Min(Cfg.MaxZombieSpeed, 1f + Cfg.ZombieSpeedPerWave * Math.Max(0, _wave - 1));

    private void SetupZombie(CCSPlayerController bot, CCSPlayerPawn pawn)
    {
        var health = ZombieHealth();
        SetHealth(pawn, health, health);
        pawn.ArmorValue = 0;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        SetupBossOrNormal(bot, pawn);
        PlaceZombie(pawn);
    }

    /// <summary>Zombies get faster every wave, Stamin-Up players are faster too. Re-applied because hits slow you down.</summary>
    private void ApplySpeeds()
    {
        var zombieSpeed = ZombieSpeed();
        foreach (var player in Players.Playing().Where(p => p.PawnIsAlive))
        {
            var target = IsZombie(player) ? zombieSpeed : HasPerk(player.Slot, ZombiePerk.StaminUp) ? StaminUpSpeed(player) : 1f;
            if (target == 1f || player.PlayerPawn.Value is not { } pawn || pawn.VelocityModifier >= target)
                continue;
            pawn.VelocityModifier = target;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        }
    }

    /// <summary>
    /// Stamin-Up also cancels the weight of heavy guns: with a Negev (150 speed) you run as fast as with a knife (250),
    /// then the perk's bonus on top.
    /// </summary>
    private float StaminUpSpeed(CCSPlayerController player)
    {
        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        var maxSpeed = 250f;
        if (weapon != null && new CCSWeaponBase(weapon.Handle).VData is { } data && data.MaxSpeed.Values.Length > 0 && data.MaxSpeed.Values[0] > 50f)
            maxSpeed = data.MaxSpeed.Values[0];
        return Math.Min(2f, Cfg.StaminUpSpeed * 250f / maxSpeed);
    }

    private void OnDeath(CCSPlayerController? victim, CCSPlayerController? attacker, bool headshot, string weapon)
    {
        if (victim == null || !victim.IsValid)
            return;

        if (IsZombie(victim))
        {
            if (!_waveActive)
                return;
            _killed++;
            _respawnAt[victim.Slot] = Server.CurrentTime + Cfg.ZombieRespawnSeconds;

            if (IsHuman(attacker))
            {
                var knife = weapon.Contains("knife") || weapon.Contains("bayonet");
                AddPoints(attacker!, knife ? Cfg.PointsPerKnifeKill : headshot ? Cfg.PointsPerHeadshotKill : Cfg.PointsPerKill);
            }
            OnZombieKilled(victim, attacker);
            return;
        }

        if (IsHuman(victim))
        {
            ClearPerks(victim.Slot);
            if (_waveActive)
                Chat.All($"{{red}}{victim.PlayerName} is down!{{default}} They come back next wave.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsZombie(CCSPlayerController? player) =>
        Players.IsValid(player) && player!.IsBot && player.Team == CsTeam.Terrorist;

    private static bool IsHuman(CCSPlayerController? player) =>
        Players.IsValid(player) && !player!.IsBot && player.Team == CsTeam.CounterTerrorist;

    private static IEnumerable<CCSPlayerController> Humans() => Players.Connected().Where(IsHuman);

    private static IEnumerable<CCSPlayerController> Zombies() => Players.Connected().Where(IsZombie);

    private static void SetHealth(CCSPlayerPawn pawn, int health, int maxHealth)
    {
        pawn.MaxHealth = maxHealth;
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }
}
