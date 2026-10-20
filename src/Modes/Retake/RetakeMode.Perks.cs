using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using FragLab.Skins;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Retake;

public enum PerkRarity
{
    Common,
    Rare,
    Epic,
    Legendary
}

public enum Perk
{
    Vampire,
    Regeneration,
    LowGravity,
    Fireman,
    Gunslinger,
    Pyromaniac,
    Glasses,
    Speed,
    Jetpack,
    MCHammer,
    Bulletproof,
    Bhop,
    GlassCannon,
    Tank,
    Ghost,
    Ninja,
    Enderman,
    LuckyCharm,
    Creeper,
    Clone,
    Wallhack,
    HSKing,
    Radar,
    Aimbot,
    Phoenix,
    SuperZeus
}

/// <summary>
/// Fun retake perks: every round each player rolls a rarity (Common 40%, Rare 30%, Epic 20%, Legendary 10%)
/// and then a perk of that rarity, shown with a roulette during freeze time. Rarely (DoublePerkChance) a player
/// gets two perks at once. Everything a perk changes on a player is undone before the next round and when the
/// mode is switched off. Only used when the mode was created with funPerks.
/// </summary>
public sealed partial class RetakeMode
{
    private const float TickSeconds = 1f / 64f;

    private static readonly Dictionary<Perk, (PerkRarity Rarity, string Name)> PerkInfo = new()
    {
        [Perk.Vampire] = (PerkRarity.Common, "Vampire"),
        [Perk.Regeneration] = (PerkRarity.Common, "Regeneration"),
        [Perk.LowGravity] = (PerkRarity.Common, "Low Gravity"),
        [Perk.Fireman] = (PerkRarity.Common, "Fireman"),
        [Perk.Gunslinger] = (PerkRarity.Common, "Gunslinger"),
        [Perk.Pyromaniac] = (PerkRarity.Common, "Pyromaniac"),
        [Perk.Glasses] = (PerkRarity.Common, "Sunglasses"),
        [Perk.Speed] = (PerkRarity.Rare, "Speed"),
        [Perk.Jetpack] = (PerkRarity.Rare, "Jetpack"),
        [Perk.MCHammer] = (PerkRarity.Rare, "M.C. Hammer"),
        [Perk.Bulletproof] = (PerkRarity.Rare, "Bulletproof"),
        [Perk.Bhop] = (PerkRarity.Rare, "Bhop"),
        [Perk.GlassCannon] = (PerkRarity.Rare, "Glass Cannon"),
        [Perk.Tank] = (PerkRarity.Epic, "Tank"),
        [Perk.Ghost] = (PerkRarity.Epic, "Ghost"),
        [Perk.Ninja] = (PerkRarity.Epic, "Ninja"),
        [Perk.Enderman] = (PerkRarity.Epic, "Enderman"),
        [Perk.LuckyCharm] = (PerkRarity.Epic, "Totem of Undying"),
        [Perk.Creeper] = (PerkRarity.Epic, "Creeper"),
        [Perk.Clone] = (PerkRarity.Epic, "Clone"),
        [Perk.Wallhack] = (PerkRarity.Legendary, "Wallhack"),
        [Perk.HSKing] = (PerkRarity.Legendary, "HS King"),
        [Perk.Radar] = (PerkRarity.Legendary, "Radar"),
        [Perk.Aimbot] = (PerkRarity.Legendary, "Golden Shot"),
        [Perk.Phoenix] = (PerkRarity.Legendary, "Phoenix"),
        [Perk.SuperZeus] = (PerkRarity.Legendary, "Super Zeus")
    };

    /// <summary>Perks only one side can roll.</summary>
    private static readonly Dictionary<Perk, CsTeam> TeamOnly = new()
    {
        [Perk.Ninja] = CsTeam.CounterTerrorist,
        [Perk.Clone] = CsTeam.Terrorist
    };

    private readonly Dictionary<int, Perk> _perks = new();        // first perk of each player (by slot)
    private readonly Dictionary<int, Perk> _secondPerks = new();  // the second perk of a DOUBLE PERK player
    private readonly HashSet<int> _revealed = new();              // players whose roulette has stopped
    private readonly Dictionary<int, Roulette> _roulettes = new();
    private readonly Dictionary<int, float> _speeds = new();
    private readonly Dictionary<int, float> _jetFuel = new();
    private readonly Dictionary<int, float> _hammerDamage = new();
    private readonly HashSet<int> _totemUsed = new();
    private readonly HashSet<int> _phoenixUsed = new();
    private readonly List<WallhackGlow> _glows = new();
    private readonly Dictionary<int, CBaseModelEntity> _clones = new(); // Clone perk: the decoy body of each player
    private readonly HashSet<int> _cloneUsed = new();
    private readonly Dictionary<int, float> _wallhackUntil = new();     // short wallhack from a shot clone
    private Timer? _perkTimer;
    private int _perkTicks;
    private ConVar? _gravityCvar;
    private float _jumpImpulse = 301.993f;

    private sealed class Roulette
    {
        public float Start;
        public float SpinEnd;
        public float NextSwitch;
        public Perk Shown;
    }

    private sealed record WallhackGlow(CBaseModelEntity Glow, CBaseModelEntity Relay, int Slot, CsTeam Team);

    private FunRetakeConfig PerkCfg => Plugin.Config.FunRetake;

    private static string RarityColor(PerkRarity rarity) => rarity switch
    {
        PerkRarity.Legendary => "#FFD700",
        PerkRarity.Epic => "#B45CFF",
        PerkRarity.Rare => "#4B69FF",
        _ => "#4CAF50"
    };

    private static string RarityChatColor(PerkRarity rarity) => rarity switch
    {
        PerkRarity.Legendary => "{gold}",
        PerkRarity.Epic => "{purple}",
        PerkRarity.Rare => "{blue}",
        _ => "{green}"
    };

    private string Describe(Perk perk, int slot) => perk switch
    {
        Perk.Tank => $"{PerkCfg.TankHealth} HP",
        Perk.Vampire => $"+{PerkCfg.VampireHealPerKill} HP per kill (max {PerkCfg.VampireMaxHealth})",
        Perk.Regeneration => $"heal {PerkCfg.RegenPerSecond} HP per second, up to 100",
        Perk.Speed => $"{_speeds.GetValueOrDefault(slot, PerkCfg.SpeedMin):0.0}x movement speed",
        Perk.LowGravity => "low gravity, jump higher",
        Perk.Ghost => "nearly invisible",
        Perk.Wallhack => "see enemies through walls",
        Perk.Ninja => "invisible, with a knife, smoke and kit (CT only)",
        Perk.Glasses => "flashbangs can't blind you, and you get a flashbang",
        Perk.Clone => $"press E to place a clone of yourself. Enemy shoots it: you see them through walls for {PerkCfg.CloneWallhackSeconds:0} s (T only)",
        Perk.Jetpack => "hold JUMP in the air to fly",
        Perk.Fireman => "no fire damage, and a molotov (T) or incendiary (CT) every round",
        Perk.Gunslinger => "Revolver only",
        Perk.Pyromaniac => $"a molotov (T) or incendiary (CT), and fire damage you deal x{PerkCfg.PyromaniacMultiplier:0}",
        Perk.MCHammer => "can't stand still: you take damage when you don't move",
        Perk.Bulletproof => $"takes {PerkCfg.BulletproofMultiplier * 100:0}% of bullet damage",
        Perk.Bhop => "auto bunny hop, no fall damage",
        Perk.GlassCannon => "2x damage dealt and taken",
        Perk.HSKing => "only headshots can hurt you",
        Perk.Enderman => "your decoy is an ender pearl: you teleport where it lands",
        Perk.Radar => "enemies show on your radar",
        Perk.Aimbot => $"x{PerkCfg.AimbotDamageMultiplier:0} damage on every bullet",
        Perk.LuckyCharm => "a deadly hit leaves you at 1 HP once per round, then you're healed",
        Perk.Creeper => $"explodes when you die ({PerkCfg.CreeperDamage:0} damage in {PerkCfg.CreeperRadius:0} units)",
        Perk.SuperZeus => "Zeus only, unlimited range, recharges instantly on a kill",
        Perk.Phoenix => $"respawns {PerkCfg.PhoenixRespawnSeconds:0} seconds after dying (once per round)",
        _ => ""
    };

    private string Label(Perk perk) => PerkInfo[perk].Name;

    // ---------------------------------------------------------------- registration

    private void RegisterPerks()
    {
        AddCommand(["perk"], "Show your perk(s) this round", (p, _) => ShowPerks(p));
        RegisterEvents();

        _gravityCvar = ConVar.Find("sv_gravity");
        Plugin.RegisterListener<Listeners.OnTick>(PerkTick);
        Plugin.RegisterListener<Listeners.CheckTransmit>(HideGlowsFromOthers);
        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(OnTakeDamage);

        Plugin.RegisterEventHandler<EventDecoyStarted>((e, _) =>
        {
            // Read everything now: the event object is gone after this handler.
            var thrower = e.Userid;
            var landing = new Vector(e.X, e.Y, e.Z + 4f);
            var decoyIndex = e.Entityid;
            if (IsActive && Players.IsValid(thrower))
                EnderPearl(thrower!, landing, decoyIndex);
            return HookResult.Continue;
        });

        Plugin.RegisterEventHandler<EventWeaponFire>((e, _) =>
        {
            // Read everything now: the event object is gone after this handler.
            var shooter = e.Userid;
            var weapon = e.Weapon;
            if (!IsActive || !Players.IsValid(shooter))
                return HookResult.Continue;
            if (weapon.Contains("taser") && HasRevealedPerk(shooter!.Slot, Perk.SuperZeus))
                ZeusShot(shooter);
            if (_clones.Count > 0 && !IsNotABullet(weapon))
                CheckCloneShot(shooter!);
            return HookResult.Continue;
        });

        Plugin.RegisterEventHandler<EventPlayerBlind>((e, _) =>
        {
            var victim = e.Userid;
            if (IsActive && Players.IsValid(victim) && HasRevealedPerk(victim!.Slot, Perk.Glasses) && victim.PlayerPawn.Value is { } pawn)
            {
                pawn.FlashDuration = 0;
                pawn.FlashMaxAlpha = 0;
                pawn.BlindUntilTime = Server.CurrentTime;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
                Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashMaxAlpha");
            }
            return HookResult.Continue;
        });

        // Ninja hides the custom gloves too, so the skins plugin must not put them back on.
        GloveRules.Suppress = p => HasRevealedPerk(p.Slot, Perk.Ninja);
    }

    private void UnregisterPerks()
    {
        RemoveGlows();
        RemoveClones();
        GloveRules.Suppress = _ => false;
    }

    private void ShowPerks(CCSPlayerController? player)
    {
        if (player == null)
            return;

        Chat.To(player, _revealed.Contains(player.Slot) && _perks.ContainsKey(player.Slot)
            ? $"Your perk{(_secondPerks.ContainsKey(player.Slot) ? "s" : "")}: {PerkLine(player.Slot)}"
            : "You don't have a perk yet this round.");
    }

    /// <summary>"Vampire + Glass Cannon", with the rarity colours.</summary>
    private string PerkLine(int slot) =>
        string.Join(" + ", PerksOf(slot).Select(p => $"{RarityChatColor(PerkInfo[p].Rarity)}{Label(p)}{{default}}"));

    // ---------------------------------------------------------------- rolling

    private IEnumerable<Perk> PerksOf(int slot)
    {
        if (_perks.TryGetValue(slot, out var first))
            yield return first;
        if (_secondPerks.TryGetValue(slot, out var second))
            yield return second;
    }

    /// <summary>True when the player has this perk and its roulette has stopped (effects are active).</summary>
    private bool HasRevealedPerk(int slot, Perk perk) => _revealed.Contains(slot) && PerksOf(slot).Contains(perk);

    private List<Perk> EnabledPerks() =>
        PerkCfg.Perks.Select(name => Enum.TryParse<Perk>(name.Replace(" ", "").Replace(".", ""), true, out var perk) ? (Perk?)perk : null)
            .OfType<Perk>()
            .Distinct()
            .ToList();

    /// <summary>Rolls a rarity by its chance (only rarities that have a perk in the pool), then a perk of that rarity.</summary>
    private Perk? RollPerk(List<Perk> pool)
    {
        if (pool.Count == 0)
            return null;

        var byRarity = pool.GroupBy(p => PerkInfo[p].Rarity).ToDictionary(g => g.Key, g => g.ToList());
        var weights = byRarity.Keys.Select(r => (Rarity: r, Weight: Math.Max(0, r switch
        {
            PerkRarity.Legendary => PerkCfg.LegendaryChance,
            PerkRarity.Epic => PerkCfg.EpicChance,
            PerkRarity.Rare => PerkCfg.RareChance,
            _ => PerkCfg.CommonChance
        }))).ToList();

        var total = weights.Sum(w => w.Weight);
        var rarity = weights[0].Rarity;
        if (total > 0)
        {
            var roll = Random.Shared.Next(total);
            foreach (var (r, weight) in weights)
            {
                if (roll < weight)
                {
                    rarity = r;
                    break;
                }
                roll -= weight;
            }
        }

        var options = byRarity[rarity];
        return options[Random.Shared.Next(options.Count)];
    }

    /// <summary>Round prestart: undo last round's perks and events, then roll new ones.</summary>
    private void RollPerks()
    {
        ResetPerkEffects();
        ResetRoundEvent();
        _jumpImpulse = ConVar.Find("sv_jump_impulse")?.GetPrimitiveValue<float>() ?? 301.993f;
        RollRoundEvent();

        var enabled = EnabledPerks();
        foreach (var player in Players.Playing())
            AssignPerks(player.Slot, enabled);

        StartPerkTimer();
    }

    /// <summary>The enabled perks this player's side can roll (Ninja is CT only, Clone is T only).</summary>
    private static List<Perk> PoolFor(int slot, List<Perk> enabled)
    {
        var team = Utilities.GetPlayerFromSlot(slot)?.Team ?? CsTeam.None;
        return enabled.Where(p => !TeamOnly.TryGetValue(p, out var only) || only == team).ToList();
    }

    private void AssignPerks(int slot, List<Perk> enabled)
    {
        enabled = PoolFor(slot, enabled);
        if (RollPerk(enabled) is not { } first)
            return;

        _perks[slot] = first;
        if (first == Perk.Speed)
            RollSpeed(slot);

        if (Random.Shared.Next(100) < PerkCfg.DoublePerkChance &&
            RollPerk(enabled.Where(p => p != first).ToList()) is { } second)
        {
            _secondPerks[slot] = second;
            if (second == Perk.Speed)
                RollSpeed(slot);
        }
    }

    private void RollSpeed(int slot) =>
        _speeds[slot] = PerkCfg.SpeedMin + (float)Random.Shared.NextDouble() * Math.Max(0, PerkCfg.SpeedMax - PerkCfg.SpeedMin);

    /// <summary>Spawn (after the loadout is handed out): start the roulette; the perks apply when it stops.</summary>
    private void ApplyPerk(CCSPlayerController player)
    {
        // Already rolled this round (e.g. a Phoenix respawn): nothing to roll again.
        if (_revealed.Contains(player.Slot))
            return;

        if (!_perks.ContainsKey(player.Slot))
            AssignPerks(player.Slot, EnabledPerks()); // joined after the roll
        if (!_perks.ContainsKey(player.Slot))
            return;

        if (player.IsBot || PerkCfg.RouletteSeconds <= 0)
        {
            Reveal(player);
            return;
        }

        var now = Server.CurrentTime;
        _roulettes[player.Slot] = new Roulette
        {
            Start = now,
            SpinEnd = now + PerkCfg.RouletteSeconds,
            NextSwitch = now,
            Shown = _perks[player.Slot]
        };
    }

    /// <summary>The roulette stopped: give the perks' effects and tell the player.</summary>
    private void Reveal(CCSPlayerController player)
    {
        if (!_perks.ContainsKey(player.Slot) || !_revealed.Add(player.Slot))
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
        {
            _revealed.Remove(player.Slot);
            return;
        }

        // Weapon-removing perks go first, so perks that hand out items afterwards keep them.
        var perks = PerksOf(player.Slot).OrderBy(p => p is Perk.Ninja or Perk.Gunslinger or Perk.SuperZeus ? 0 : 1).ToList();
        foreach (var perk in perks)
            ApplyPerkEffects(player, pawn, perk);

        // Removing the weapons also took the kit (and the bomb): give them back.
        if (perks.Any(p => p is Perk.Ninja or Perk.Gunslinger or Perk.SuperZeus))
        {
            if (player.Team == CsTeam.CounterTerrorist)
                WeaponAllocator.GiveDefuser(player);
            if (player.Team == CsTeam.Terrorist && !Cfg.AutoPlant && _planterSlot == player.Slot)
                player.GiveNamedItem("weapon_c4");
        }

        if (!player.IsBot)
        {
            if (perks.Count > 1)
                Chat.To(player, "{gold}DOUBLE PERK!{default} You got two perks this round:");
            foreach (var perk in perks)
            {
                var rarity = PerkInfo[perk].Rarity;
                Chat.To(player, $"{RarityChatColor(rarity)}{rarity.ToString().ToUpperInvariant()} {Label(perk)}{{default}} - {Describe(perk, player.Slot)}");
            }
        }
    }

    private void ApplyPerkEffects(CCSPlayerController player, CCSPlayerPawn pawn, Perk perk)
    {
        switch (perk)
        {
            case Perk.Tank:
                SetHealth(pawn, PerkCfg.TankHealth, PerkCfg.TankHealth);
                break;
            case Perk.Ghost:
                SetAlpha(pawn, PerkCfg.GhostAlpha);
                break;
            case Perk.Ninja:
                player.RemoveWeapons();
                player.GiveNamedItem("weapon_knife");
                player.GiveNamedItem("weapon_smokegrenade");
                ShowDefaultGloves(player, pawn);
                SetAlpha(pawn, 0);
                // Weapons are handed out this frame: hide them once they exist.
                Server.NextFrame(() =>
                {
                    if (!pawn.IsValid)
                        return;
                    foreach (var weapon in pawn.WeaponServices?.MyWeapons.Select(h => h.Value).OfType<CBasePlayerWeapon>() ?? Enumerable.Empty<CBasePlayerWeapon>())
                        SetAlphaOn(new CBaseModelEntity(weapon.Handle), 0);
                });
                break;
            case Perk.SuperZeus:
                player.RemoveWeapons();
                player.GiveNamedItem("weapon_knife");
                player.GiveNamedItem("weapon_taser");
                break;
            case Perk.Gunslinger:
                player.RemoveWeapons();
                player.GiveNamedItem("weapon_knife");
                player.GiveNamedItem("weapon_revolver");
                break;
            case Perk.Fireman:
            case Perk.Pyromaniac:
                player.GiveNamedItem(player.Team == CsTeam.CounterTerrorist ? "weapon_incgrenade" : "weapon_molotov");
                break;
            case Perk.Enderman:
                player.GiveNamedItem("weapon_decoy");
                break;
            case Perk.Glasses:
                player.GiveNamedItem("weapon_flashbang");
                break;
            case Perk.Jetpack:
                _jetFuel[player.Slot] = PerkCfg.JetpackFuelSeconds;
                break;
            case Perk.Wallhack:
                CreateGlowsForEnemiesOf(player.Team);
                break;
        }
    }

    // ---------------------------------------------------------------- every tick

    private void PerkTick()
    {
        if (!IsActive || (_perks.Count == 0 && _roulettes.Count == 0))
            return;

        var now = Server.CurrentTime;
        foreach (var (slot, roulette) in _roulettes.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (!Players.IsValid(player) || !_perks.TryGetValue(slot, out var perk))
            {
                _roulettes.Remove(slot);
                continue;
            }
            SpinRoulette(player!, roulette, perk, now);
        }

        foreach (var slot in _perks.Keys.ToList())
        {
            if (!_revealed.Contains(slot))
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (!Players.IsValid(player) || !player!.PawnIsAlive || pawn?.AbsVelocity == null)
                continue;

            foreach (var perk in PerksOf(slot).ToList())
            {
                switch (perk)
                {
                    case Perk.LowGravity:
                        LowGravityTick(pawn);
                        break;
                    case Perk.Jetpack:
                        JetpackTick(player, pawn, slot);
                        break;
                    case Perk.Bhop:
                        BhopTick(player, pawn);
                        break;
                    case Perk.Clone when !_cloneUsed.Contains(slot) && (player.Buttons & PlayerButtons.Use) != 0 && OnGround(pawn):
                        _cloneUsed.Add(slot);
                        PlaceClone(player, pawn);
                        break;
                }
            }
        }
    }

    private void SpinRoulette(CCSPlayerController player, Roulette roulette, Perk perk, float now)
    {
        if (now < roulette.SpinEnd)
        {
            if (now >= roulette.NextSwitch)
            {
                // Slows down towards the end, like a real roulette.
                var progress = (now - roulette.Start) / Math.Max(0.1f, roulette.SpinEnd - roulette.Start);
                roulette.Shown = RollPerk(PoolFor(player.Slot, EnabledPerks())) ?? perk;
                roulette.NextSwitch = now + 0.05f + 0.3f * progress * progress;
            }

            var shown = PerkInfo[roulette.Shown];
            player.PrintToCenterHtml(
                $"<font class='fontSize-s' color='#AAAAAA'>Rolling your perk...</font><br>" +
                $"<font class='fontSize-xl' color='{RarityColor(shown.Rarity)}'>{shown.Name}</font><br>" +
                $"<font class='fontSize-s' color='{RarityColor(shown.Rarity)}'>{shown.Rarity.ToString().ToUpperInvariant()}</font>" + EventHtml());
            return;
        }

        if (!_revealed.Contains(player.Slot))
            Reveal(player);

        if (now > roulette.SpinEnd + 6f)
        {
            _roulettes.Remove(player.Slot);
            return;
        }

        var info = PerkInfo[perk];
        var title = _secondPerks.ContainsKey(player.Slot) ? "DOUBLE PERK" : info.Rarity.ToString().ToUpperInvariant();
        var html = $"<font class='fontSize-s' color='{RarityColor(info.Rarity)}'>{title}</font><br>" +
                   $"<font class='fontSize-l' color='{RarityColor(info.Rarity)}'>{PerkNames(player.Slot)}</font>";
        foreach (var p in PerksOf(player.Slot))
            html += $"<br><font class='fontSize-s' color='#FFFFFF'>{(_secondPerks.ContainsKey(player.Slot) ? Label(p) + ": " : "")}{Describe(p, player.Slot)}</font>";
        player.PrintToCenterHtml(html + EventHtml());
    }

    /// <summary>The map event line for the perk screen; empty when there's no event this round.</summary>
    private string EventHtml() => _roundEvent == RoundEvent.None
        ? ""
        : $"<br><font class='fontSize-s' color='#FFA500'>MAP EVENT: {EventInfo[_roundEvent].Name} - {EventInfo[_roundEvent].Description}</font>";

    private string PerkNames(int slot) => string.Join(" + ", PerksOf(slot).Select(Label));

    /// <summary>Cancels part of gravity while in the air. Uses the live sv_gravity, so it stacks correctly with the moon gravity event.</summary>
    private void LowGravityTick(CCSPlayerPawn pawn)
    {
        if (OnGround(pawn))
            return;

        var v = pawn.AbsVelocity!;
        var lift = (1f - Math.Clamp(PerkCfg.LowGravity, 0f, 1f)) * CurrentGravity() * TickSeconds;
        pawn.Teleport(null, null, new Vector(v.X, v.Y, v.Z + lift));
    }

    private float CurrentGravity() => _gravityCvar?.GetPrimitiveValue<float>() ?? 800f;

    private void JetpackTick(CCSPlayerController player, CCSPlayerPawn pawn, int slot)
    {
        if (OnGround(pawn))
        {
            _jetFuel[slot] = PerkCfg.JetpackFuelSeconds;
            return;
        }

        var fuel = _jetFuel.GetValueOrDefault(slot);
        if ((player.Buttons & PlayerButtons.Jump) == 0 || fuel <= 0)
            return;

        _jetFuel[slot] = fuel - TickSeconds;
        var v = pawn.AbsVelocity!;
        var up = Math.Min(PerkCfg.JetpackMaxUpSpeed, v.Z + CurrentGravity() * 2f * TickSeconds);
        pawn.Teleport(null, null, new Vector(v.X, v.Y, up));
    }

    /// <summary>Bunny hop: holding jump on the ground bounces you straight back up, keeping your speed.</summary>
    private void BhopTick(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (!OnGround(pawn) || (player.Buttons & PlayerButtons.Jump) == 0)
            return;

        var v = pawn.AbsVelocity!;
        pawn.Teleport(null, null, new Vector(v.X, v.Y, _jumpImpulse));
    }

    private static bool OnGround(CCSPlayerPawn pawn) => (pawn.Flags & 1) != 0; // FL_ONGROUND

    // ---------------------------------------------------------------- every 0.25 seconds

    private void StartPerkTimer()
    {
        if (_perkTimer != null)
            return;

        _perkTicks = 0;
        _perkTimer = Plugin.AddTimer(0.25f, () =>
        {
            if (!IsActive || _perks.Count == 0)
                return;

            var everySecond = ++_perkTicks % 4 == 0;
            var frozen = Game.Rules()?.FreezePeriod == true;
            foreach (var slot in _perks.Keys.ToList())
            {
                if (!_revealed.Contains(slot))
                    continue;

                var player = Utilities.GetPlayerFromSlot(slot);
                var pawn = player?.PlayerPawn.Value;
                if (!Players.IsValid(player) || !player!.PawnIsAlive || pawn == null)
                    continue;

                foreach (var perk in PerksOf(slot).ToList())
                {
                    switch (perk)
                    {
                        case Perk.Speed:
                            var speed = _speeds.GetValueOrDefault(slot, PerkCfg.SpeedMin);
                            if (Math.Abs(pawn.VelocityModifier - speed) > 0.01f)
                            {
                                pawn.VelocityModifier = speed;
                                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
                            }
                            break;
                        case Perk.Regeneration when everySecond && pawn.Health < 100:
                            SetHealth(pawn, Math.Min(100, pawn.Health + PerkCfg.RegenPerSecond), Math.Max(pawn.MaxHealth, 100));
                            break;
                        case Perk.MCHammer when !frozen:
                            HammerTick(player, pawn, slot);
                            break;
                        case Perk.Radar:
                            ShowEnemiesOnRadar(player);
                            break;
                    }
                }
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void HammerTick(CCSPlayerController player, CCSPlayerPawn pawn, int slot)
    {
        var v = pawn.AbsVelocity;
        var moving = v != null && v.X * v.X + v.Y * v.Y > 20f * 20f;
        if (moving)
            return;

        var pending = _hammerDamage.GetValueOrDefault(slot) + PerkCfg.HammerDamagePerSecond * 0.25f;
        var damage = (int)pending;
        _hammerDamage[slot] = pending - damage;
        if (damage <= 0)
            return;

        player.PrintToCenter("Can't touch this! KEEP MOVING");
        if (pawn.Health <= damage)
            pawn.CommitSuicide(false, true);
        else
            SetHealth(pawn, pawn.Health - damage, pawn.MaxHealth);
    }

    /// <summary>Marks every living enemy as spotted by the player, so they show on the radar.</summary>
    private static void ShowEnemiesOnRadar(CCSPlayerController viewer)
    {
        foreach (var enemy in Players.OnTeam(Players.Opposite(viewer.Team)).Where(p => p.PawnIsAlive))
        {
            var pawn = enemy.PlayerPawn.Value;
            if (pawn == null)
                continue;

            var state = pawn.EntitySpottedState;
            state.Spotted = true;
            var mask = state.SpottedByMask;
            var index = viewer.Slot / 32;
            if (index < mask.Length)
                mask[index] |= 1u << (viewer.Slot % 32);

            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState",
                Schema.GetSchemaOffset("EntitySpottedState_t", "m_bSpotted"));
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState",
                Schema.GetSchemaOffset("EntitySpottedState_t", "m_bSpottedByMask"));
        }
    }

    // ---------------------------------------------------------------- damage

    private HookResult OnTakeDamage(CBaseEntity victimEntity, CTakeDamageInfo info)
    {
        if (!IsActive || _perks.Count == 0 || victimEntity.DesignerName != "player")
            return HookResult.Continue;

        var head = info.GetHitGroup() == HitGroup_t.HITGROUP_HEAD;
        var bullet = (info.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0;
        var burn = (info.BitsDamageType & DamageTypes_t.DMG_BURN) != 0;
        var fall = (info.BitsDamageType & DamageTypes_t.DMG_FALL) != 0;

        var victim = new CCSPlayerPawn(victimEntity.Handle).OriginalController.Value;
        if (victim != null && _revealed.Contains(victim.Slot))
        {
            foreach (var perk in PerksOf(victim.Slot))
            {
                switch (perk)
                {
                    case Perk.Fireman when burn:
                        info.Damage = 0;
                        break;
                    case Perk.HSKing when !head:
                        info.Damage = 0;
                        break;
                    case Perk.Bhop when fall:
                        info.Damage = 0;
                        break;
                    case Perk.Bulletproof when bullet:
                        info.Damage *= PerkCfg.BulletproofMultiplier;
                        break;
                    case Perk.GlassCannon:
                        info.Damage *= 2;
                        break;
                }
            }

            // Totem of Undying: a deadly hit leaves you at 1 HP, once per round, and you're healed a moment later.
            var pawn = new CCSPlayerPawn(victimEntity.Handle);
            if (HasRevealedPerk(victim.Slot, Perk.LuckyCharm) && !_totemUsed.Contains(victim.Slot) &&
                info.Damage >= pawn.Health && pawn.Health > 1)
            {
                _totemUsed.Add(victim.Slot);
                info.Damage = pawn.Health - 1;
                var slot = victim.Slot;
                Server.NextFrame(() =>
                {
                    var player = Utilities.GetPlayerFromSlot(slot);
                    var alive = player?.PlayerPawn.Value;
                    if (alive == null || !player!.PawnIsAlive)
                        return;
                    SetHealth(alive, PerkCfg.TotemHealth, Math.Max(alive.MaxHealth, PerkCfg.TotemHealth));
                    Chat.To(player, "{gold}TOTEM OF UNDYING!{default} You survived with 1 HP.");
                });
            }
        }

        var attackerEntity = info.Attacker.Value;
        if (info.Damage > 0 && attackerEntity is { DesignerName: "player" })
        {
            var attacker = new CCSPlayerPawn(attackerEntity.Handle).OriginalController.Value;
            if (attacker != null && _revealed.Contains(attacker.Slot))
            {
                foreach (var perk in PerksOf(attacker.Slot))
                {
                    switch (perk)
                    {
                        case Perk.GlassCannon:
                            info.Damage *= 2;
                            break;
                        case Perk.Pyromaniac when burn:
                            info.Damage *= PerkCfg.PyromaniacMultiplier;
                            break;
                        case Perk.Aimbot when bullet:
                            info.Damage *= PerkCfg.AimbotDamageMultiplier;
                            break;
                    }
                }
            }
        }

        if (_roundEvent == RoundEvent.QuadDamage && info.Damage > 0)
            info.Damage *= 2;

        return HookResult.Continue;
    }

    // ---------------------------------------------------------------- deaths

    /// <summary>A player died: Vampire heals the killer, Creeper explodes, Phoenix comes back.</summary>
    private void OnPerkKill(CCSPlayerController? attacker, CCSPlayerController victim)
    {
        RemoveGlowsOf(victim.Slot);

        if (HasRevealedPerk(victim.Slot, Perk.Creeper))
            Explode(victim);

        if (HasRevealedPerk(victim.Slot, Perk.Phoenix) && _phoenixUsed.Add(victim.Slot))
            SchedulePhoenix(victim.Slot);

        if (attacker != null && attacker.Slot != victim.Slot && attacker.PawnIsAlive && HasRevealedPerk(attacker.Slot, Perk.SuperZeus))
            RechargeZeus(attacker);

        if (attacker == null || attacker.Slot == victim.Slot || !attacker.PawnIsAlive || !HasRevealedPerk(attacker.Slot, Perk.Vampire))
            return;

        var pawn = attacker.PlayerPawn.Value;
        if (pawn == null)
            return;

        var health = Math.Min(PerkCfg.VampireMaxHealth, pawn.Health + PerkCfg.VampireHealPerKill);
        SetHealth(pawn, health, Math.Max(pawn.MaxHealth, health));
        Chat.To(attacker, $"Vampire: {{green}}+{PerkCfg.VampireHealPerKill} HP");
    }

    /// <summary>Creeper: the explosion hits every living enemy in the radius, less the further away they are.</summary>
    private void Explode(CCSPlayerController victim)
    {
        var origin = victim.PlayerPawn.Value?.AbsOrigin;
        if (origin == null)
            return;

        var center = new Vector(origin.X, origin.Y, origin.Z);
        var hit = 0;
        foreach (var enemy in Players.Connected().Where(p => Players.IsPlayingTeam(p.Team) && p.Team != victim.Team && p.PawnIsAlive))
        {
            var pawn = enemy.PlayerPawn.Value;
            var at = pawn?.AbsOrigin;
            if (pawn == null || at == null)
                continue;

            var distance = Distance3(center, new Vector(at.X, at.Y, at.Z));
            if (distance > PerkCfg.CreeperRadius)
                continue;

            var damage = (int)(PerkCfg.CreeperDamage * (1f - distance / PerkCfg.CreeperRadius));
            if (damage <= 0)
                continue;

            hit++;
            if (pawn.Health <= damage)
                pawn.CommitSuicide(false, true);
            else
                SetHealth(pawn, pawn.Health - damage, pawn.MaxHealth);
        }

        Chat.All($"{{red}}KABOOM!{{default}} {victim.PlayerName} exploded ({hit} hit).");
    }

    private static float Distance3(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Phoenix: after a short delay the player comes back and gets their perks again.</summary>
    private void SchedulePhoenix(int slot)
    {
        Plugin.AddTimer(PerkCfg.PhoenixRespawnSeconds, () =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (!IsActive || !Players.IsValid(player) || player!.PawnIsAlive || !Players.IsPlayingTeam(player.Team))
                return;

            player.Respawn();
            Chat.To(player, "{gold}PHOENIX!{default} Back in the fight.");
            Server.NextFrame(() =>
            {
                if (!Players.IsValid(player) || !player.PawnIsAlive)
                    return;
                _revealed.Remove(slot);
                Reveal(player);
            });
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- Super Zeus

    /// <summary>Zeus range in CS2; closer targets are hit by the real Zeus, further ones by this perk.</summary>
    private const float ZeusRange = 190f;

    /// <summary>
    /// Super Zeus: the shot hits the enemy closest to the crosshair line (within a body's width),
    /// at any distance, as long as no wall is in the way.
    /// </summary>
    private void ZeusShot(CCSPlayerController shooter)
    {
        var pawn = shooter.PlayerPawn.Value;
        if (pawn == null || AimRay(pawn) is not var (eye, dir))
            return;

        CCSPlayerController? best = null;
        var bestAlong = float.MaxValue;
        foreach (var enemy in Players.OnTeam(Players.Opposite(shooter.Team)).Where(p => p.PawnIsAlive))
        {
            var at = enemy.PlayerPawn.Value?.AbsOrigin;
            if (at == null || RayHitDistance(pawn, eye, dir, at, ZeusRange) is not { } along || along >= bestAlong)
                continue;

            best = enemy;
            bestAlong = along;
        }

        if (best == null)
            return;

        var victim = best;
        victim.PlayerPawn.Value?.CommitSuicide(false, true);
        Chat.All($"{{gold}}ZAP!{{default}} {shooter.PlayerName} zeused {victim.PlayerName} from {bestAlong / 52.5f:0} m.");
        RechargeZeus(shooter);
    }

    /// <summary>The player's eye position and aim direction.</summary>
    private static (Vector Eye, Vector Dir)? AimRay(CCSPlayerPawn pawn)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null)
            return null;

        var eye = new Vector(origin.X, origin.Y, origin.Z + 64f);
        var pitch = pawn.EyeAngles.X * MathF.PI / 180f;
        var yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        return (eye, new Vector(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch)));
    }

    /// <summary>
    /// Distance along the aim line to a body standing at feet, when the line passes within a body's width of its
    /// chest or head (so crouching and headshots both count) further than minAlong and no wall is in the way; otherwise null.
    /// </summary>
    private static float? RayHitDistance(CCSPlayerPawn shooter, Vector eye, Vector dir, Vector feet, float minAlong)
    {
        foreach (var height in new[] { 40f, 62f })
        {
            var target = new Vector(feet.X, feet.Y, feet.Z + height);
            var to = new Vector(target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z);
            var along = to.X * dir.X + to.Y * dir.Y + to.Z * dir.Z;
            if (along <= minAlong)
                continue;

            var offX = to.X - dir.X * along;
            var offY = to.Y - dir.Y * along;
            var offZ = to.Z - dir.Z * along;
            if (offX * offX + offY * offY + offZ * offZ <= 20f * 20f && ClearLine(shooter, eye, target))
                return along;
        }
        return null;
    }

    private static bool ClearLine(CCSPlayerPawn ignore, Vector from, Vector to)
    {
        var options = new TraceOptions
        {
            InteractsWith = Contents.Solid | Contents.WorldGeometry | Contents.StaticLevel | Contents.Window,
            InteractsExclude = Contents.Player | Contents.Npc | Contents.Hitbox | Contents.Trigger
        };
        var zero = new Vector(0, 0, 0);
        var result = Trace.TraceHullShape(from, to, zero, zero, ignore, options);
        // A hit right at the target is the target itself (a clone is a solid prop), not a wall.
        return !result.DidHit() || result.Fraction >= 0.98f || Distance3(result.EndPos, to) < 32f;
    }

    /// <summary>A kill recharges the Zeus on the spot: the same weapon gets its shot back, nothing is removed or given.</summary>
    private void RechargeZeus(CCSPlayerController player)
    {
        Server.NextFrame(() =>
        {
            if (!Players.IsValid(player) || !player.PawnIsAlive)
                return;

            var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons.Select(h => h.Value).OfType<CBasePlayerWeapon>().ToList() ?? [];
            var taser = weapons.FirstOrDefault(w => w.IsValid && w.DesignerName == "weapon_taser");
            if (taser == null)
            {
                player.GiveNamedItem("weapon_taser");
                return;
            }

            var zeus = new CWeaponTaser(taser.Handle);
            zeus.Clip1 = 1;
            zeus.FireTime = 0;
            zeus.NextPrimaryAttackTick = Server.TickCount;
            Utilities.SetStateChanged(zeus, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(zeus, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
        });
    }
    // ---------------------------------------------------------------- Enderman

    private void EnderPearl(CCSPlayerController thrower, Vector landing, int decoyIndex)
    {
        if (!HasRevealedPerk(thrower.Slot, Perk.Enderman) || !thrower.PawnIsAlive)
            return;

        var safe = FindSafeSpot(thrower, landing);
        var decoy = Utilities.GetEntityFromIndex<CBaseEntity>(decoyIndex);
        if (decoy != null && decoy.IsValid)
            decoy.AcceptInput("Kill");

        if (safe is { } spot)
        {
            thrower.PlayerPawn.Value?.Teleport(spot, null, new Vector(0, 0, 0));
            Chat.To(thrower, "{purple}Whoosh!{default} New pearl in a few seconds.");
        }
        else
        {
            Chat.To(thrower, "{red}No room there:{default} the pearl landed in a wall. New pearl in a few seconds.");
        }

        Plugin.AddTimer(PerkCfg.EndermanCooldownSeconds, () =>
        {
            if (IsActive && Players.IsValid(thrower) && thrower.PawnIsAlive && HasRevealedPerk(thrower.Slot, Perk.Enderman))
                thrower.GiveNamedItem("weapon_decoy");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// A spot near the landing point where a player fits (standing on the floor, not inside a wall).
    /// Returns null when none is found, so an Enderman never teleports into geometry.
    /// </summary>
    private static Vector? FindSafeSpot(CCSPlayerController owner, Vector landing)
    {
        var pawn = owner.PlayerPawn.Value;
        if (pawn == null)
            return null;

        var options = new TraceOptions
        {
            InteractsWith = Contents.Solid | Contents.WorldGeometry | Contents.StaticLevel | Contents.PlayerClip,
            InteractsExclude = Contents.Player | Contents.Npc | Contents.Hitbox | Contents.Trigger
        };
        var mins = new Vector(-16, -16, 0);
        var maxs = new Vector(16, 16, 72);

        var candidates = new List<Vector> { landing };
        foreach (var height in new[] { 0f, 24f, 56f })
            foreach (var radius in new[] { 24f, 48f })
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * MathF.PI / 4f;
                    candidates.Add(new Vector(landing.X + MathF.Cos(angle) * radius, landing.Y + MathF.Sin(angle) * radius, landing.Z + height));
                }

        foreach (var candidate in candidates)
        {
            // Drop onto the floor below the candidate, then check that the player's body fits there.
            var floor = candidate;
            var down = Trace.TraceHullShape(candidate, new Vector(candidate.X, candidate.Y, candidate.Z - 300f), mins, maxs, pawn, options);
            if (down.DidHit())
                floor = new Vector(down.EndPos.X, down.EndPos.Y, down.EndPos.Z + 2f);

            if (!Trace.TraceHullShape(floor, floor, mins, maxs, pawn, options).DidHit())
                return floor;
        }

        return null;
    }

    // ---------------------------------------------------------------- Wallhack

    /// <summary>
    /// Puts a glowing copy of the player model on every living enemy of the given team.
    /// HideGlowsFromOthers makes sure only that team's Wallhack players can see them.
    /// </summary>
    private void CreateGlowsForEnemiesOf(CsTeam team)
    {
        foreach (var enemy in Players.OnTeam(Players.Opposite(team)).Where(p => p.PawnIsAlive))
        {
            if (_glows.Any(g => g.Slot == enemy.Slot))
                continue;

            var pawn = enemy.PlayerPawn.Value;
            var model = pawn?.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
            if (pawn == null || string.IsNullOrEmpty(model))
                continue;

            var relay = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            var glow = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (relay == null || glow == null)
                continue;

            relay.SetModel(model);
            relay.Spawnflags = 256u;
            relay.RenderMode = RenderMode_t.kRenderNone;
            relay.DispatchSpawn();

            glow.SetModel(model);
            glow.Spawnflags = 256u;
            glow.DispatchSpawn();
            glow.Glow.GlowColorOverride = Color.FromArgb(255, 255, 40, 40);
            glow.Glow.GlowRange = 5000;
            glow.Glow.GlowRangeMin = 20;
            glow.Glow.GlowTeam = -1;
            glow.Glow.GlowType = 3;

            // The relay follows the player's animation; the glow copy follows the relay.
            relay.AcceptInput("FollowEntity", pawn, relay, "!activator");
            glow.AcceptInput("FollowEntity", relay, glow, "!activator");

            _glows.Add(new WallhackGlow(glow, relay, enemy.Slot, enemy.Team));
        }
    }

    private void HideGlowsFromOthers(CCheckTransmitInfoList infoList)
    {
        if (_glows.Count == 0)
            return;

        foreach (var (info, viewer) in infoList)
        {
            var hasWallhack = viewer != null &&
                              (HasRevealedPerk(viewer.Slot, Perk.Wallhack) || _wallhackUntil.GetValueOrDefault(viewer.Slot) > Server.CurrentTime);

            foreach (var glow in _glows)
            {
                if (hasWallhack && viewer!.Team != glow.Team)
                    continue;
                if (glow.Glow.IsValid)
                    info.TransmitEntities.Remove(glow.Glow);
                if (glow.Relay.IsValid)
                    info.TransmitEntities.Remove(glow.Relay);
            }
        }
    }

    private void RemoveGlowsOf(int slot)
    {
        foreach (var glow in _glows.Where(g => g.Slot == slot).ToList())
        {
            KillGlow(glow);
            _glows.Remove(glow);
        }
    }

    private void RemoveGlows()
    {
        foreach (var glow in _glows)
            KillGlow(glow);
        _glows.Clear();
    }

    private static void KillGlow(WallhackGlow glow)
    {
        if (glow.Glow.IsValid)
            glow.Glow.AcceptInput("Kill");
        if (glow.Relay.IsValid)
            glow.Relay.AcceptInput("Kill");
    }

    /// <summary>
    /// Ninja: custom gloves go back to the default gloves. Same refresh steps as the skins plugin
    /// (switch weapon, change the item a moment later, switch back, flip the body group), otherwise the old gloves stay drawn.
    /// </summary>
    private void ShowDefaultGloves(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        var item = pawn.EconGloves;
        if (item.ItemDefinitionIndex == 0 && !item.Initialized)
            return;

        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();
        player.ExecuteClientCommand("lastinv");

        Plugin.AddTimer(0.08f, () =>
        {
            if (!player.IsValid || !player.PawnIsAlive || pawn.Handle != player.PlayerPawn.Value?.Handle)
                return;

            item.ItemDefinitionIndex = 0;
            item.Initialized = false;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");

            player.ExecuteClientCommand("lastinv");
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
            Plugin.AddTimer(0.2f, () =>
            {
                if (pawn.IsValid)
                    pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- Clone

    private static bool IsNotABullet(string weapon) =>
        weapon.Contains("knife") || weapon.Contains("bayonet") || weapon.Contains("grenade") || weapon.Contains("molotov") ||
        weapon.Contains("flashbang") || weapon.Contains("decoy") || weapon.Contains("taser") || weapon.Contains("c4");

    /// <summary>Clone: a copy of the player's model where they stand. It doesn't move or shoot.</summary>
    private void PlaceClone(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        var model = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        var origin = pawn.AbsOrigin;
        if (string.IsNullOrEmpty(model) || origin == null)
            return;

        var clone = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (clone == null)
            return;

        clone.SetModel(model);
        clone.UseAnimGraph = true;
        clone.DispatchSpawn();
        clone.Teleport(new Vector(origin.X, origin.Y, origin.Z), new QAngle(0, pawn.EyeAngles.Y, 0), new Vector(0, 0, 0));
        _clones[player.Slot] = clone;
        Chat.To(player, $"{{purple}}Clone placed!{{default}} If an enemy shoots it, you see the enemies through walls for {PerkCfg.CloneWallhackSeconds:0} seconds.");
    }

    /// <summary>An enemy fired: when the shot goes through a clone, the clone pops and its owner gets a short wallhack.</summary>
    private void CheckCloneShot(CCSPlayerController shooter)
    {
        var pawn = shooter.PlayerPawn.Value;
        if (pawn == null || AimRay(pawn) is not var (eye, dir))
            return;

        foreach (var (slot, clone) in _clones.ToList())
        {
            if (!clone.IsValid || clone.AbsOrigin == null)
            {
                _clones.Remove(slot);
                continue;
            }

            var owner = Utilities.GetPlayerFromSlot(slot);
            if (!Players.IsValid(owner) || owner!.Team == shooter.Team || RayHitDistance(pawn, eye, dir, clone.AbsOrigin, 0f) == null)
                continue;

            _clones.Remove(slot);
            clone.AcceptInput("Kill");
            Chat.To(shooter, "{red}That was a clone!");
            if (!owner.PawnIsAlive)
                continue;

            Chat.To(owner, $"{{purple}}{shooter.PlayerName} shot your clone!{{default}} You see the enemies through walls for {PerkCfg.CloneWallhackSeconds:0} seconds.");
            GiveTimedWallhack(owner);
        }
    }

    private void GiveTimedWallhack(CCSPlayerController owner)
    {
        var slot = owner.Slot;
        var team = owner.Team;
        _wallhackUntil[slot] = Server.CurrentTime + PerkCfg.CloneWallhackSeconds;
        CreateGlowsForEnemiesOf(team);

        Plugin.AddTimer(PerkCfg.CloneWallhackSeconds + 0.1f, () =>
        {
            if (!IsActive || _wallhackUntil.GetValueOrDefault(slot) > Server.CurrentTime)
                return;
            _wallhackUntil.Remove(slot);

            // Keep the glows while a teammate still needs them (Wallhack perk or another clone wallhack).
            var stillNeeded = Players.OnTeam(team).Any(p => p.PawnIsAlive &&
                (HasRevealedPerk(p.Slot, Perk.Wallhack) || _wallhackUntil.GetValueOrDefault(p.Slot) > Server.CurrentTime));
            if (stillNeeded)
                return;

            foreach (var glow in _glows.Where(g => g.Team != team).ToList())
            {
                KillGlow(glow);
                _glows.Remove(glow);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RemoveClones()
    {
        foreach (var clone in _clones.Values)
        {
            if (clone.IsValid)
                clone.AcceptInput("Kill");
        }
        _clones.Clear();
    }

    // ---------------------------------------------------------------- reset

    /// <summary>Puts every player back to normal: speed, visibility and glows persist across rounds otherwise.</summary>
    private void ResetPerkEffects()
    {
        foreach (var player in Players.Connected())
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            if (pawn.VelocityModifier > 1f)
            {
                pawn.VelocityModifier = 1f;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            }
            if (pawn.Render.A != 255)
                SetAlpha(pawn, 255);
        }

        RemoveGlows();
        RemoveClones();
        _cloneUsed.Clear();
        _wallhackUntil.Clear();
        _perks.Clear();
        _secondPerks.Clear();
        _revealed.Clear();
        _roulettes.Clear();
        _speeds.Clear();
        _jetFuel.Clear();
        _hammerDamage.Clear();
        _totemUsed.Clear();
        _phoenixUsed.Clear();
    }

    private void StopPerks()
    {
        _perkTimer?.Kill();
        _perkTimer = null;
        ResetPerkEffects();
        ResetRoundEvent();
    }

    /// <summary>Ninja and Gunslinger keep their perk weapons: no buying.</summary>
    private bool PerkBlocksBuying(CCSPlayerController player) =>
        PerksOf(player.Slot).Any(p => p is Perk.Ninja or Perk.Gunslinger or Perk.SuperZeus);

    private void AnnouncePerks()
    {
        if (_perks.Count > 0)
            Chat.All("{gold}Fun retake:{default} everyone rolls a random perk. Type {green}!perk{default} to see yours.");
    }

    /// <summary>Round end: who had which perk(s).</summary>
    private void PrintPerkSummary()
    {
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var entries = Players.OnTeam(team)
                .Where(p => _perks.ContainsKey(p.Slot))
                .Select(p => $"{p.PlayerName} ({PerkLine(p.Slot)}{{default}})")
                .ToList();
            if (entries.Count > 0)
                Chat.All($"{Players.TeamName(team)} perks: {string.Join(", ", entries)}");
        }
    }

    private static void SetHealth(CCSPlayerPawn pawn, int health, int maxHealth)
    {
        pawn.MaxHealth = maxHealth;
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private static void SetAlpha(CCSPlayerPawn pawn, int alpha) => SetAlphaOn(pawn, alpha);

    private static void SetAlphaOn(CBaseModelEntity entity, int alpha)
    {
        entity.RenderMode = alpha < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
        entity.Render = Color.FromArgb(Math.Clamp(alpha, 0, 255), entity.Render.R, entity.Render.G, entity.Render.B);
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_clrRender");
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_nRenderMode");
    }
}
