using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Retake;

/// <summary>
/// Retakes: the bomb is already planted on a random site, Ts defend it and CTs retake.
/// Teams are managed by the plugin: new players queue and join at the next round,
/// winning CTs rotate to T and the teams scramble after a long T win streak.
/// </summary>
public sealed partial class RetakeMode : GameMode
{
    private readonly List<int> _queue = new(); // player slots
    private readonly HashSet<RetakeSpawn> _usedSpawns = new();
    private readonly HashSet<int> _onSiteSpawn = new();
    private readonly Dictionary<int, int> _roundKills = new();

    private readonly SpawnStore _spawns;
    private readonly WeaponAllocator _allocator;
    private readonly PlayerDataStore<RetakeLoadoutPrefs> _loadouts;
    private Bombsite _site;
    private int? _planterSlot;
    private RetakeSpawn? _planterSpawn;
    private CsTeam _lastWinner = CsTeam.None;
    private int _roundsPlayed;
    private int _tWinStreak;
    private bool _editMode;
    private bool _warnedNoSpawns;

    // Short warmup after the mode starts or the map changes, so everyone can pick a team.
    private bool _warmup;
    private Timer? _warmupTimer;

    // Fun retake: the same retakes plus a random perk per player each round (see RetakeMode.Perks.cs).
    private readonly bool _funPerks;

    public RetakeMode(FragLabPlugin plugin, bool funPerks = false) : base(plugin)
    {
        _funPerks = funPerks;
        _spawns = new SpawnStore(plugin.ModuleDirectory);
        _allocator = new WeaponAllocator(plugin.Config.Retake);
        _loadouts = new PlayerDataStore<RetakeLoadoutPrefs>(Path.Combine(plugin.ModuleDirectory, "playerdata", "retake"));
    }

    public override string Name => _funPerks ? "funretake" : "retake";
    public override string Description => _funPerks ? "Retakes with a random perk for every player each round" : "Bomb is planted, CTs retake the site";
    public override string Title => _funPerks ? "Fun Retake" : "Retake";
    public override IEnumerable<string> Aliases => _funPerks ? ["fun", "funretakes"] : [];

    private RetakeConfig Cfg => Plugin.Config.Retake;

    public override bool ShowDamageReport => !_editMode && !_warmup;

    public override void Register()
    {
        RegisterEditor();
        RegisterBuying();
        RegisterInstantDefuse();
        RegisterWeaponRules();
        RegisterScrimWarmup();
        if (_funPerks)
            RegisterPerks();
    }

    public override void Unregister()
    {
        UnregisterBuying();
        if (_funPerks)
            UnregisterPerks();
    }

    public override void Activate()
    {
        // Reload unless there are unsaved edits: retake and fun retake share the spawn files.
        if (_spawns.Map != Server.MapName || !_spawns.IsDirty)
        {
            _spawns.Load(Server.MapName);
            _warnedNoSpawns = false;
        }

        ResetRoundState();
        _editMode = false;
        _roundsPlayed = 0;
        _tWinStreak = 0;
        _lastWinner = CsTeam.None;

        ExecConfig(Cfg.Config);
        StartWarmup();
        WarnIfNoSpawns();
    }

    private void StartWarmup()
    {
        StopWarmupTimer();
        var seconds = Math.Max(0, Cfg.WarmupSeconds);
        if (seconds == 0)
        {
            _warmup = false;
            Server.ExecuteCommand("mp_warmup_end");
            Server.ExecuteCommand("mp_restartgame 1");
            Chat.All("{green}Retakes{default} enabled.");
            return;
        }

        _warmup = true;
        Server.ExecuteCommand($"mp_warmuptime {seconds}; mp_warmup_pausetimer 0; mp_warmup_start");
        Chat.All($"{{green}}Retakes{{default}} start in {seconds} seconds. Pick a team now!");

        _warmupTimer = Plugin.AddTimer(seconds, () =>
        {
            _warmupTimer = null;
            if (!IsActive || _editMode)
                return;
            _warmup = false;
            Server.ExecuteCommand("mp_warmup_end");
            Chat.All("{green}Retakes are live!");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopWarmupTimer()
    {
        _warmupTimer?.Kill();
        _warmupTimer = null;
    }

    public override void Deactivate()
    {
        _queue.Clear();
        ResetRoundState();
        StopWarmupTimer();
        _warmup = false;
        _editMode = false;
        ClearWeaponRules();
        if (_funPerks)
            StopPerks();
    }

    public override void OnMapStart(string mapName)
    {
        _queue.Clear();
        // Timers are gone after a map change.
        _warmupTimer = null;
        _defuseWatch = null;
        _roundWatch = null;
    }

    public override void PrintStatus(CCSPlayerController? player)
    {
        Chat.To(player, $"Retake site: {{green}}{_site}{{default}}, round type: {{green}}{_allocator.RoundType}{{default}}, rounds: {_roundsPlayed}");
        Chat.To(player, $"T win streak: {_tWinStreak}, queue: {_queue.Count}, edit mode: {_editMode}");
    }

    // ---------------------------------------------------------------- queue / teams

    public override HookResult OnJoinTeamCommand(CCSPlayerController player, CommandInfo info)
    {
        // Free team choice during warmup; the first retake round balances the teams.
        if (_editMode || _warmup)
            return HookResult.Continue;

        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out var requested))
            return HookResult.Handled;

        var requestedTeam = (CsTeam)requested;

        if (requestedTeam == CsTeam.Spectator)
        {
            _queue.Remove(player.Slot);
            return HookResult.Continue;
        }

        if (Players.IsPlayingTeam(player.Team))
        {
            Chat.To(player, "Teams are managed automatically in retakes.");
            return HookResult.Handled;
        }

        // With nobody playing there is no round to wait for, so join right away.
        if (Players.Playing().Count() < 2)
        {
            player.ChangeTeam(Players.OnTeam(CsTeam.Terrorist).Any() ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
            return HookResult.Handled;
        }

        if (!_queue.Contains(player.Slot))
            _queue.Add(player.Slot);
        Chat.To(player, $"You are in the queue (position {_queue.IndexOf(player.Slot) + 1}) and will join next round.");
        return HookResult.Handled;
    }

    public override void OnPlayerDisconnect(CCSPlayerController player)
    {
        _queue.Remove(player.Slot);
        if (_planterSlot == player.Slot)
            _planterSlot = null;
        if (!player.IsBot)
            _loadouts.Forget(player.SteamID);
    }

    public override void OnRoundPrestart()
    {
        ResetRoundState();

        if (!_editMode && !_warmup)
        {
            UpdateTeams();
            PickSite();
            _allocator.StartRound(_roundsPlayed);
            _allocator.PickAwpers(Players.Playing(), p => p.IsBot ? new RetakeLoadoutPrefs() : _loadouts.Get(p.SteamID));
            if (_funPerks)
                RollPerks();
        }

        _roundKills.Clear();
    }

    private void UpdateTeams()
    {
        _queue.RemoveAll(slot =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            return !Players.IsValid(p) || Players.IsPlayingTeam(p!.Team);
        });

        var ts = Players.OnTeam(CsTeam.Terrorist).ToList();
        var cts = Players.OnTeam(CsTeam.CounterTerrorist).ToList();

        // Rotate based on the last round's result.
        if (_lastWinner == CsTeam.Terrorist)
        {
            _tWinStreak++;
            if (Cfg.ScrambleAfterTWinStreak > 0 && _tWinStreak >= Cfg.ScrambleAfterTWinStreak)
            {
                Chat.All($"Terrorists won {_tWinStreak} in a row, {{green}}scrambling teams{{default}}!");
                _tWinStreak = 0;
                var everyone = ts.Concat(cts).OrderBy(_ => Random.Shared.Next()).ToList();
                ts = everyone.Take(everyone.Count / 2).ToList();
                cts = everyone.Skip(everyone.Count / 2).ToList();
            }
        }
        else if (_lastWinner == CsTeam.CounterTerrorist)
        {
            _tWinStreak = 0;
            // The best CT of the round swaps with a random T.
            var bestCt = cts.OrderByDescending(Kills).ThenBy(_ => Random.Shared.Next()).FirstOrDefault();
            var randomT = ts.OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
            if (bestCt != null && randomT != null)
            {
                cts.Remove(bestCt);
                ts.Remove(randomT);
                ts.Add(bestCt);
                cts.Add(randomT);
            }
        }

        // Fill free slots from the queue.
        while (_queue.Count > 0 && ts.Count + cts.Count < Cfg.MaxPlayers)
        {
            var next = Utilities.GetPlayerFromSlot(_queue[0]);
            _queue.RemoveAt(0);
            if (Players.IsValid(next))
                cts.Add(next!);
        }

        // Balance to the configured T/CT ratio. The lowest scorers move first.
        var total = ts.Count + cts.Count;
        if (total >= 2)
        {
            var desiredTs = Math.Clamp((int)Math.Round(total * Cfg.TerroristRatio), 1, total - 1);
            while (ts.Count > desiredTs)
            {
                var move = ts.OrderBy(Kills).First();
                ts.Remove(move);
                cts.Add(move);
            }
            while (ts.Count < desiredTs)
            {
                var move = cts.OrderByDescending(Kills).First();
                cts.Remove(move);
                ts.Add(move);
            }
        }

        foreach (var p in ts)
            MoveTo(p, CsTeam.Terrorist);
        foreach (var p in cts)
            MoveTo(p, CsTeam.CounterTerrorist);

        for (var i = 0; i < _queue.Count; i++)
            Chat.To(Utilities.GetPlayerFromSlot(_queue[i]), $"Server is full. You are position {i + 1} in the queue.");

        _lastWinner = CsTeam.None;
    }

    private int Kills(CCSPlayerController player) => _roundKills.GetValueOrDefault(player.Slot);

    private static void MoveTo(CCSPlayerController player, CsTeam team)
    {
        if (player.Team == team)
            return;
        if (Players.IsPlayingTeam(player.Team))
            player.SwitchTeam(team);
        else
            player.ChangeTeam(team);
    }

    // ---------------------------------------------------------------- round flow

    private void PickSite()
    {
        var playable = Enum.GetValues<Bombsite>().Where(_spawns.IsPlayable).ToList();
        if (playable.Count == 0)
            playable = Enum.GetValues<Bombsite>().ToList();
        _site = playable[Random.Shared.Next(playable.Count)];
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (_editMode || _warmup || !Players.IsPlayingTeam(player.Team))
            return;

        // Teleport and equip a frame later so default spawn logic doesn't override us.
        Server.NextFrame(() =>
        {
            if (!Players.IsValid(player) || !player.PawnIsAlive)
                return;

            var spawn = ClaimSpawn(player);
            if (spawn != null)
            {
                player.PlayerPawn.Value?.Teleport(spawn.Position, spawn.Angles, new Vector(0, 0, 0));
                _onSiteSpawn.Add(player.Slot);
            }

            var givesBomb = !Cfg.AutoPlant && _planterSlot == player.Slot;
            var prefs = player.IsBot ? new RetakeLoadoutPrefs() : _loadouts.Get(player.SteamID);
            _allocator.Equip(player, givesBomb, prefs);
            Plugin.Skins.ReapplyLater(player);
            SetMoney(player, Cfg.AllowBuying ? BuyMoney : 0);
            if (_funPerks)
                ApplyPerk(player);
        });
    }

    private RetakeSpawn? ClaimSpawn(CCSPlayerController player)
    {
        var team = player.Team;
        var free = _spawns.For(_site, team).Where(s => !_usedSpawns.Contains(s)).ToList();
        if (free.Count == 0)
            return null;

        // Keep CTs away from the bomb when enough spawns allow it.
        if (team == CsTeam.CounterTerrorist && Cfg.CtMinDistanceFromBomb > 0 && _spawns.SiteCenter(_site) is { } bomb)
        {
            var far = free.Where(s => s.DistanceTo(bomb) >= Cfg.CtMinDistanceFromBomb).ToList();
            free = far.Count > 0 ? far : [free.MaxBy(s => s.DistanceTo(bomb))!];
        }

        RetakeSpawn spawn;
        var planterSpawns = free.Where(s => s.IsPlanter).ToList();
        if (team == CsTeam.Terrorist && _planterSlot == null && planterSpawns.Count > 0)
        {
            spawn = planterSpawns[Random.Shared.Next(planterSpawns.Count)];
            _planterSlot = player.Slot;
            _planterSpawn = spawn;
        }
        else if (team == CsTeam.Terrorist && _planterSlot == null && !_spawns.For(_site, team).Any(s => s.IsPlanter))
        {
            // No planter spawns defined for this site: the first T on site plants where they stand.
            spawn = free[Random.Shared.Next(free.Count)];
            _planterSlot = player.Slot;
        }
        else
        {
            // Keep planter spots free for the planter when possible.
            var normal = free.Where(s => !s.IsPlanter).ToList();
            var pool = normal.Count > 0 ? normal : free;
            spawn = pool[Random.Shared.Next(pool.Count)];
        }

        _usedSpawns.Add(spawn);
        return spawn;
    }

    public override void OnRoundStart()
    {
        if (_editMode || _warmup)
            return;

        OpenDoorsAndWindows();

        var ts = Players.OnTeam(CsTeam.Terrorist).Count();
        var cts = Players.OnTeam(CsTeam.CounterTerrorist).Count();
        Chat.All($"Retake {{green}}{_site}{{default}}: {{gold}}{ts} T{{default}} vs {{lightblue}}{cts} CT{{default}} - round {_roundsPlayed + 1}");
        var rule = WeaponRuleText();
        if (rule != null)
            Chat.All($"{{gold}}{rule}{{default}} this round.");
        else if (Cfg.AllowBuying)
            Chat.All($"{RetakeWeapons.Describe(_allocator.RoundType, Cfg.AwpEnabled)}. Buy for free during freeze time, your last buy is saved.");

        if (_allocator.OnlyWeapon == null && _allocator.RoundType == RoundType.FullBuy && Cfg.AwpEnabled)
        {
            var awpers = Players.Playing().Where(_allocator.IsAwper).ToList();
            Chat.All(awpers.Count > 0
                ? $"AWP this round: {string.Join(", ", awpers.Select(p => $"{Players.TeamName(p.Team)} {p.PlayerName}"))}"
                : "Nobody has the AWP this round. Buy one in a full-buy round to get it next time.");
        }
        else if (_allocator.OnlyWeapon == null && Cfg.AwpEnabled && _roundsPlayed + 1 == Cfg.PistolRounds + Cfg.ForceBuyRounds)
        {
            Chat.All("Full buys (and AWPs) start next round.");
        }
        Chat.CenterAll($"Retake {_site} - {ts} T vs {cts} CT\n{rule ?? RetakeWeapons.Describe(_allocator.RoundType, Cfg.AwpEnabled)}" + (_funPerks ? EventCenterText() : ""));
        if (_funPerks)
            AnnouncePerks();
        WarnIfNoSpawns();
    }

    public override void OnRoundFreezeEnd()
    {
        if (_editMode || _warmup)
            return;

        StartRoundWatch();
        if (!Cfg.AutoPlant)
            return;

        // Prefer the dedicated planter; otherwise any T that was placed on the site.
        var planter = _planterSlot is { } slot ? Utilities.GetPlayerFromSlot(slot) : null;
        if (!Players.IsValid(planter) || !planter!.PawnIsAlive || planter.Team != CsTeam.Terrorist)
        {
            planter = Players.OnTeam(CsTeam.Terrorist)
                .FirstOrDefault(p => p.PawnIsAlive && _onSiteSpawn.Contains(p.Slot));
        }

        var pawn = planter?.PlayerPawn.Value;
        if (planter == null || pawn?.AbsOrigin == null)
            return;

        var position = _planterSpawn?.Position ?? new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);

        if (!BombPlanter.Plant(planter, position, new QAngle(0, pawn.EyeAngles.Y, 0), _site))
            Plugin.Logger.LogWarning("Retake: failed to create planted_c4 entity");
    }

    public override void OnPlayerDeath(CCSPlayerController victim, CCSPlayerController? attacker)
    {
        if (_funPerks)
            OnPerkKill(attacker, victim);

        if (attacker == null || attacker.Slot == victim.Slot)
            return;
        _roundKills[attacker.Slot] = _roundKills.GetValueOrDefault(attacker.Slot) + 1;
    }

    public override void OnRoundEnd(int winner, int reason)
    {
        StopRoundWatch();
        StopDefuseWatch();
        if (_funPerks && !_editMode && !_warmup)
            PrintPerkSummary();
        if (_funPerks)
            ResetRoundEvent();
        if (_editMode || _warmup || !Players.IsPlayingTeam((CsTeam)winner))
            return;

        _lastWinner = (CsTeam)winner;
        _roundsPlayed++;

        if (_lastWinner == CsTeam.CounterTerrorist)
            Chat.All("{lightblue}CTs{default} retook the site!");
        else if (_lastWinner == CsTeam.Terrorist)
            Chat.All("{gold}Terrorists{default} held the site!");
    }

    private void ResetRoundState()
    {
        _usedSpawns.Clear();
        _onSiteSpawn.Clear();
        _planterSlot = null;
        _planterSpawn = null;
        _molotovBlockAnnounced = false;
        StopDefuseWatch();
        StopRoundWatch();
    }

    private static readonly string[] BreakablePropClasses =
    [
        "prop_dynamic", "prop_dynamic_override", "prop_physics", "prop_physics_multiplayer",
        "prop_physics_override", "func_physbox", "func_physbox_multiplayer"
    ];

    /// <summary>Opens (and locks open) every door and breaks windows/breakables so every route onto the site is open.</summary>
    private void OpenDoorsAndWindows()
    {
        if (Cfg.OpenDoors)
        {
            foreach (var className in new[] { "prop_door_rotating", "func_door_rotating", "func_door" })
            {
                foreach (var door in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(className))
                {
                    if (!door.IsValid)
                        continue;
                    door.AcceptInput("Open");
                    door.AcceptInput("Lock"); // stays open: players can't close it again
                }
            }
        }

        if (Cfg.BreakWindows)
        {
            foreach (var glass in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_breakable_surf"))
            {
                if (glass.IsValid)
                    glass.AcceptInput("Shatter", value: "0.5 0.5 0");
            }

            foreach (var breakable in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_breakable"))
            {
                if (breakable.IsValid)
                    breakable.AcceptInput("Break");
            }

            // Vents and similar are often breakable props. Only break props that can take damage (have health);
            // decoration props have none and are left alone.
            foreach (var className in BreakablePropClasses)
            {
                foreach (var prop in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(className))
                {
                    if (prop.IsValid && prop.Health > 0 && prop.TakesDamage)
                        prop.AcceptInput("Break");
                }
            }
        }
    }

    private void WarnIfNoSpawns()
    {
        if (_warnedNoSpawns || Enum.GetValues<Bombsite>().Any(_spawns.IsPlayable))
            return;
        _warnedNoSpawns = true;
        Chat.All($"{{red}}No retake spawns for {Server.MapName}.{{default}} An admin can create them with !retakeedit.");
    }
}
