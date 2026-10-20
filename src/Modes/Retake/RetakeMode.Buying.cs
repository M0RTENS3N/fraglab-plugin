using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Retake;

/// <summary>
/// Free buying limited by round type (the last purchase is remembered), and instant defuse.
/// </summary>
public sealed partial class RetakeMode
{
    private const int BuyMoney = 16000;

    private readonly Dictionary<int, float> _lastBuyMessage = new();
    private Func<DynamicHook, HookResult>? _canAcquireHook;
    private bool _molotovBlockAnnounced;
    private Timer? _defuseWatch;
    private Timer? _roundWatch;

    // ---------------------------------------------------------------- buying

    private void RegisterBuying()
    {
        _canAcquireHook = OnCanAcquire;
        VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(_canAcquireHook, HookMode.Pre);

        Plugin.RegisterEventHandler<EventItemPurchase>((e, _) =>
        {
            if (IsActive && !_editMode && !_warmup && Players.IsValid(e.Userid))
                OnItemPurchased(e.Userid!, e.Weapon);
            return HookResult.Continue;
        });
    }

    private void UnregisterBuying()
    {
        if (_canAcquireHook != null)
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(_canAcquireHook, HookMode.Pre);
        _canAcquireHook = null;
    }

    private HookResult OnCanAcquire(DynamicHook hook)
    {
        if (!IsActive || _editMode || _warmup || hook.GetParam<AcquireMethod>(2) != AcquireMethod.Buy)
            return HookResult.Continue;

        var pawn = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value;
        var controller = pawn?.Controller.Value;
        if (controller == null)
            return HookResult.Continue;
        var player = new CCSPlayerController(controller.Handle);

        int defindex = hook.GetParam<CEconItemView>(1).ItemDefinitionIndex;
        var reason = BuyBlockReason(player, defindex);
        if (reason == null)
            return HookResult.Continue;

        // CanAcquire can fire several times per click; don't spam chat.
        if (Server.CurrentTime - _lastBuyMessage.GetValueOrDefault(player.Slot, -10f) > 1f)
        {
            _lastBuyMessage[player.Slot] = Server.CurrentTime;
            Chat.To(player, $"{{red}}{reason}");
        }

        hook.SetReturn(AcquireResult.NotAllowedByProhibition);
        return HookResult.Stop;
    }

    private string? BuyBlockReason(CCSPlayerController player, int defindex)
    {
        if (!Cfg.AllowBuying)
            return "Buying is disabled in retakes.";

        if (_allocator.OnlyWeapon != null)
            return $"{WeaponRuleText()} this round: buying is off.";

        if (_funPerks && PerkBlocksBuying(player))
            return "Your perk doesn't allow buying this round.";

        if (Cfg.BuyOnlyInFreezeTime && Game.Rules()?.FreezePeriod != true)
            return "Buy time is over. You can only buy during freeze time.";

        if (!RetakeWeapons.TryGet(defindex, out var className, out var kind))
            return "Armor, kits and grenades are handed out automatically.";

        if (Cfg.DisabledWeapons.Contains(className))
            return "That weapon is disabled in retakes.";

        var round = _allocator.RoundType;
        if (!RetakeWeapons.IsAllowed(round, kind))
            return $"Not allowed. {RetakeWeapons.Describe(round, Cfg.AwpEnabled)}.";

        if (defindex == RetakeWeapons.Awp && AwpsOnTeam(player.Team) >= Cfg.MaxAwpsPerTeam)
            return $"Your team already has {Cfg.MaxAwpsPerTeam} AWP.";

        return null;
    }

    private static int AwpsOnTeam(CsTeam team) =>
        Players.OnTeam(team)
            .Where(p => p.PawnIsAlive)
            .Count(p => p.PlayerPawn.Value?.WeaponServices?.MyWeapons
                .Any(w => w.Value?.AttributeManager.Item.ItemDefinitionIndex == RetakeWeapons.Awp) == true);

    private void OnItemPurchased(CCSPlayerController player, string weapon)
    {
        // Buying is free: top the money back up.
        Server.NextFrame(() =>
        {
            if (Players.IsValid(player))
                SetMoney(player, BuyMoney);
        });

        if (RetakeWeapons.DefindexOf(weapon) is not { } defindex ||
            !RetakeWeapons.TryGet(defindex, out var className, out var kind))
            return;

        if (kind == WeaponKind.Pistol && _allocator.RoundType == RoundType.Pistol)
            ApplyPistolRoundArmor(player, className);

        if (player.IsBot)
            return;

        _allocator.Remember(_loadouts.Get(player.SteamID), player.Team, defindex, kind);
        _loadouts.Save(player.SteamID);
    }

    /// <summary>Buying an upgraded pistol in a pistol round costs the armor; switching back to the default gives it back.</summary>
    private void ApplyPistolRoundArmor(CCSPlayerController player, string pistol)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return;

        if (_allocator.GetsArmor(pistol))
        {
            if (pawn.ArmorValue <= 0)
                player.GiveNamedItem("item_kevlar");
            return;
        }

        if (pawn.ArmorValue <= 0)
            return;

        pawn.ArmorValue = 0;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        Chat.To(player, "Upgraded pistol: {red}no armor{default} this pistol round.");
    }

    private static void SetMoney(CCSPlayerController player, int amount)
    {
        var money = player.InGameMoneyServices;
        if (money == null)
            return;
        money.Account = amount;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    // ---------------------------------------------------------------- instant defuse

    private void RegisterInstantDefuse()
    {
        // Watch the defuse for as long as it runs: Ts dying or a molotov burning out mid-defuse
        // should still trigger the instant defuse.
        Plugin.RegisterEventHandler<EventBombBegindefuse>((_, _) =>
        {
            if (IsActive && Cfg.InstantDefuse)
                StartDefuseWatch();
            return HookResult.Continue;
        });
    }

    private void StartDefuseWatch()
    {
        if (_defuseWatch != null)
            return;

        _defuseWatch = Plugin.AddTimer(0.25f, () =>
        {
            if (TryInstantDefuse() is DefuseCheck.Done)
                StopDefuseWatch();
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Ends the round as a T win as soon as no living CT can finish a defuse before the bomb explodes,
    /// instead of making everyone wait for the explosion.
    /// </summary>
    private void StartRoundWatch()
    {
        StopRoundWatch();
        if (Cfg.EndRoundWhenDefuseImpossible)
            _roundWatch = Plugin.AddTimer(0.5f, CheckDefusePossible, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopRoundWatch()
    {
        _roundWatch?.Kill();
        _roundWatch = null;
    }

    private void CheckDefusePossible()
    {
        if (!IsActive || _editMode || _warmup)
        {
            StopRoundWatch();
            return;
        }

        var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        if (c4 == null)
            return; // not planted (yet)
        if (!c4.IsValid || c4.HasExploded || c4.BombDefused)
        {
            StopRoundWatch();
            return;
        }

        // A defuse already running that will finish in time is fine.
        if (c4.BeingDefused && c4.DefuseCountDown <= c4.C4Blow)
            return;

        var cts = Players.OnTeam(CsTeam.CounterTerrorist).Where(p => p.PawnIsAlive).ToList();
        if (cts.Count == 0)
            return; // the game ends the round itself

        var fastestDefuse = cts.Any(HasDefuseKit) ? 5f : 10f;
        if (c4.C4Blow - Server.CurrentTime >= fastestDefuse)
            return;

        StopRoundWatch();
        Game.Rules()?.TerminateRound(3f, RoundEndReason.TargetBombed);
        Chat.All("{red}No time left to defuse{default}, Terrorists win the round.");
    }

    private static bool HasDefuseKit(CCSPlayerController player)
    {
        var services = player.PlayerPawn.Value?.ItemServices;
        return services != null && new CCSPlayer_ItemServices(services.Handle).HasDefuser;
    }

    private void StopDefuseWatch()
    {
        _defuseWatch?.Kill();
        _defuseWatch = null;
    }

    private enum DefuseCheck
    {
        /// <summary>Nobody is defusing anymore (or it was made instant): stop watching.</summary>
        Done,

        /// <summary>Someone is defusing but it can't be instant yet.</summary>
        Waiting
    }

    /// <summary>
    /// Finishes a running defuse immediately when no Terrorist is alive, no molotov is burning
    /// near the bomb and the defuse would have made it in time anyway.
    /// </summary>
    private DefuseCheck TryInstantDefuse()
    {
        if (!IsActive || _editMode || !Cfg.InstantDefuse)
            return DefuseCheck.Done;

        var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        if (c4 == null || !c4.IsValid || !c4.BeingDefused || c4.HasExploded || c4.AbsOrigin == null)
            return DefuseCheck.Done;

        if (Players.OnTeam(CsTeam.Terrorist).Any(p => p.PawnIsAlive))
            return DefuseCheck.Waiting;

        if (c4.DefuseCountDown > c4.C4Blow)
            return DefuseCheck.Done; // would not have made it in time

        if (MolotovNear(c4.AbsOrigin))
        {
            if (!_molotovBlockAnnounced)
            {
                _molotovBlockAnnounced = true;
                Chat.All("{red}Molotov is burning near the bomb{default}, no instant defuse until it burns out.");
            }
            return DefuseCheck.Waiting;
        }

        c4.DefuseCountDown = 0;
        Utilities.SetStateChanged(c4, "CPlantedC4", "m_flDefuseCountDown");
        Chat.All("{lightblue}Instant defuse!");
        return DefuseCheck.Done;
    }

    private bool MolotovNear(Vector bomb)
    {
        // Molotovs still in the air count too: they're about to land.
        if (Utilities.FindAllEntitiesByDesignerName<CMolotovProjectile>("molotov_projectile").Any())
            return true;

        return Utilities.FindAllEntitiesByDesignerName<CInferno>("inferno")
            .Any(fire => fire.IsValid && fire.AbsOrigin != null && IsBurning(fire) &&
                         Distance(fire.AbsOrigin, bomb) <= Cfg.InstantDefuseMolotovRadius);
    }

    /// <summary>An inferno entity lingers a bit after the flames are gone; only count it while flames burn.</summary>
    private static bool IsBurning(CInferno fire)
    {
        var burning = fire.FireIsBurning;
        var count = Math.Min(fire.FireCount, burning.Length);
        for (var i = 0; i < count; i++)
        {
            if (burning[i])
                return true;
        }
        return false;
    }
    private static float Distance(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
