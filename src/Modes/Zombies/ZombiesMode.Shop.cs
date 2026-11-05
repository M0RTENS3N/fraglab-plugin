using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using FragLab.Core;
using FragLab.Core.Menus;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Zombies;

public enum ZombiePerk
{
    Juggernog,
    DoubleTap,
    StaminUp,
    QuickRevive
}

/// <summary>Points (the player's money), the !shop menu and the perks. Perks are lost on death.</summary>
public sealed partial class ZombiesMode
{
    private readonly Dictionary<int, HashSet<ZombiePerk>> _perks = new();

    private void RegisterShop()
    {
        AddCommand(["shop", "zshop"], "Open the zombies shop (weapons, ammo, armor, perks)", (p, _) => OpenShop(p));
        AddCommand(["ammo"], "Buy a full ammo refill", (p, _) => WithAlive(p, BuyAmmo));
        AddCommand(["armor"], "Buy kevlar and helmet", (p, _) => WithAlive(p, BuyArmor));
        AddCommand(["pap", "packapunch"], "Pack-a-Punch the gun in your hands (more damage)", (p, _) => WithAlive(p, BuyPackAPunch));
        AddCommand(["box", "mysterybox"], "Buy a random weapon from the mystery box", (p, _) => WithAlive(p, BuyMysteryBox));
    }

    private void ResetShop()
    {
        _perks.Clear();
        StopAllBoxRolls();
    }

    private void ClearPerks(int slot) => _perks.Remove(slot);

    private bool HasPerk(int slot, ZombiePerk perk) => _perks.TryGetValue(slot, out var set) && set.Contains(perk);

    private (int Price, string Name, string Description) PerkInfo(ZombiePerk perk) => perk switch
    {
        ZombiePerk.Juggernog => (Cfg.JuggernogPrice, "Juggernog", $"{Cfg.JuggernogHealth} HP"),
        ZombiePerk.DoubleTap => (Cfg.DoubleTapPrice, "Double Tap", $"x{Cfg.DoubleTapDamage:0.#} bullet damage"),
        ZombiePerk.StaminUp => (Cfg.StaminUpPrice, "Stamin-Up", "run faster"),
        _ => (Cfg.QuickRevivePrice, "Quick Revive", "survive one deadly hit")
    };

    // ---------------------------------------------------------------- menu

    private void OpenShop(CCSPlayerController? player)
    {
        if (player == null || !IsHuman(player))
        {
            Chat.To(player, "Only survivors (CT) can use the shop.");
            return;
        }

        Plugin.Menus.Open(player, new Menu("Zombies shop", menu =>
        {
            menu.Add($"Points: {GetPoints(player)}");
            menu.Add($"Ammo refill - {Cfg.AmmoPrice}", p => WithAlive(p, BuyAmmo));
            menu.Add($"Armor - {Cfg.ArmorPrice}", p => WithAlive(p, BuyArmor));
            menu.Add($"Mystery box - {Cfg.MysteryBoxPrice}", p => WithAlive(p, BuyMysteryBox), "#ffd700");
            menu.Add($"Pack-a-Punch (gun in hand) - {Cfg.PackAPunchPrice}", p => WithAlive(p, BuyPackAPunch), "#ffd700");
            menu.Add("Weapons >", OpenWeapons);
            menu.Add("Perks >", OpenPerks);
        }));
    }

    private void OpenWeapons(CCSPlayerController player)
    {
        Plugin.Menus.Open(player, new Menu("Shop > Weapons", menu =>
        {
            menu.Add($"Points: {GetPoints(player)}");
            foreach (var (weapon, price) in Cfg.WeaponPrices.OrderBy(w => w.Value))
                menu.Add($"{WeaponName(weapon)} - {price}", p => WithAlive(p, q => BuyWeapon(q, weapon, price)));
        }));
    }

    private void OpenPerks(CCSPlayerController player)
    {
        Plugin.Menus.Open(player, new Menu("Shop > Perks", menu =>
        {
            menu.Add($"Points: {GetPoints(player)}");
            foreach (var perk in Enum.GetValues<ZombiePerk>())
            {
                var (price, name, description) = PerkInfo(perk);
                var owned = HasPerk(player.Slot, perk);
                menu.Add(owned ? $"[x] {name} ({description})" : $"{name} - {price} ({description})",
                    owned ? null : p => WithAlive(p, q => BuyPerk(q, perk)), owned ? "#7cfc00" : null);
            }
        }));
    }

    private void WithAlive(CCSPlayerController? player, Action<CCSPlayerController> action)
    {
        if (player == null || !IsHuman(player))
        {
            Chat.To(player, "Only survivors (CT) can buy.");
            return;
        }
        if (!player.PawnIsAlive)
        {
            Chat.To(player, "{red}You're down.{default} You can buy again next wave.");
            return;
        }
        action(player);
    }

    // ---------------------------------------------------------------- buying

    private void BuyWeapon(CCSPlayerController player, string weapon, int price)
    {
        if (!TrySpend(player, price))
            return;
        GiveWeapon(player, weapon);
        Chat.To(player, $"Bought {{green}}{WeaponName(weapon)}{{default}}.");
    }

    private void BuyAmmo(CCSPlayerController player)
    {
        if (!TrySpend(player, Cfg.AmmoPrice))
            return;
        RefillAmmo(player);
        Chat.To(player, "Ammo refilled.");
    }

    private void BuyArmor(CCSPlayerController player)
    {
        if (player.PlayerPawn.Value is { ArmorValue: >= 100 })
        {
            Chat.To(player, "You already have full armor.");
            return;
        }
        if (!TrySpend(player, Cfg.ArmorPrice))
            return;
        player.GiveNamedItem("item_assaultsuit");
        Chat.To(player, "Bought armor.");
    }

    private sealed class BoxRoll
    {
        public required string Prize;
        public required float Start;
        public required float SpinEnd;
        public float NextSwitch;
        public string Shown = "";
        public Timer? Timer;
    }

    private readonly Dictionary<int, BoxRoll> _boxRolls = new();

    /// <summary>The mystery box spins through weapons (slowing down like the perk roulette) and then gives the prize.</summary>
    private void BuyMysteryBox(CCSPlayerController player)
    {
        if (_boxRolls.ContainsKey(player.Slot))
        {
            Chat.To(player, "The mystery box is still rolling for you.");
            return;
        }
        if (Cfg.MysteryBoxWeapons.Count == 0 || !TrySpend(player, Cfg.MysteryBoxPrice))
            return;

        Plugin.Menus.Close(player);
        var now = Server.CurrentTime;
        var roll = new BoxRoll
        {
            Prize = RandomBoxWeapon(),
            Start = now,
            SpinEnd = now + Math.Max(0.5f, Cfg.MysteryBoxRollSeconds)
        };
        _boxRolls[player.Slot] = roll;

        var slot = player.Slot;
        roll.Timer = Plugin.AddTimer(0.05f, () => TickBoxRoll(slot, roll),
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT | CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    private string RandomBoxWeapon() => Cfg.MysteryBoxWeapons[Random.Shared.Next(Cfg.MysteryBoxWeapons.Count)];

    private void TickBoxRoll(int slot, BoxRoll roll)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        var now = Server.CurrentTime;
        if (!IsActive || player == null || !Players.IsValid(player))
        {
            StopBoxRoll(slot);
            return;
        }

        if (now < roll.SpinEnd)
        {
            if (now >= roll.NextSwitch)
            {
                var progress = (now - roll.Start) / Math.Max(0.1f, roll.SpinEnd - roll.Start);
                roll.Shown = RandomBoxWeapon();
                roll.NextSwitch = now + 0.05f + 0.3f * progress * progress;
            }
            var (tier, color) = BoxTier(roll.Shown);
            player.PrintToCenterHtml(
                "<font class='fontSize-s' color='#AAAAAA'>Mystery box...</font><br>" +
                $"<font class='fontSize-xl' color='{color}'>{WeaponName(roll.Shown)}</font><br>" +
                $"<font class='fontSize-s' color='{color}'>{tier}</font>");
            return;
        }

        if (roll.Shown != "DONE")
        {
            roll.Shown = "DONE";
            if (player.PawnIsAlive && IsHuman(player))
            {
                GiveWeapon(player, roll.Prize);
                var (tier, _) = BoxTier(roll.Prize);
                Chat.All($"{{gold}}{player.PlayerName}{{default}} got a {{green}}{WeaponName(roll.Prize)}{{default}} ({tier.ToLowerInvariant()}) from the mystery box!");
            }
            else
            {
                AddPoints(player, Cfg.MysteryBoxPrice);
                Chat.To(player, "You went down before the box finished: points refunded.");
                StopBoxRoll(slot);
                return;
            }
        }

        // Keep the prize on screen for a moment.
        if (now > roll.SpinEnd + 2.5f)
        {
            StopBoxRoll(slot);
            return;
        }
        var (prizeTier, prizeColor) = BoxTier(roll.Prize);
        player.PrintToCenterHtml(
            $"<font class='fontSize-s' color='{prizeColor}'>{prizeTier}</font><br>" +
            $"<font class='fontSize-xl' color='{prizeColor}'>{WeaponName(roll.Prize)}</font>");
    }

    private void StopBoxRoll(int slot)
    {
        if (_boxRolls.Remove(slot, out var roll))
            roll.Timer?.Kill();
    }

    private void StopAllBoxRolls()
    {
        foreach (var slot in _boxRolls.Keys.ToList())
            StopBoxRoll(slot);
    }

    private static (string Tier, string Color) BoxTier(string weapon) => weapon switch
    {
        "weapon_awp" or "weapon_m249" or "weapon_negev" or "weapon_ak47" or "weapon_m4a1_silencer" or "weapon_m4a1"
            or "weapon_scar20" or "weapon_g3sg1" => ("LEGENDARY", "#FFD700"),
        "weapon_aug" or "weapon_sg556" or "weapon_famas" or "weapon_galilar" or "weapon_xm1014" or "weapon_p90"
            or "weapon_ssg08" or "weapon_deagle" or "weapon_revolver" => ("RARE", "#B44CFF"),
        _ => ("COMMON", "#5EA8FF")
    };

    private void BuyPerk(CCSPlayerController player, ZombiePerk perk)
    {
        if (HasPerk(player.Slot, perk))
            return;
        var (price, name, description) = PerkInfo(perk);
        if (!TrySpend(player, price))
            return;

        if (!_perks.TryGetValue(player.Slot, out var set))
            _perks[player.Slot] = set = new HashSet<ZombiePerk>();
        set.Add(perk);

        if (perk == ZombiePerk.Juggernog && player.PlayerPawn.Value is { } pawn)
            SetHealth(pawn, Cfg.JuggernogHealth, Cfg.JuggernogHealth);

        Chat.To(player, $"Perk: {{green}}{name}{{default}} ({description}). You lose it if you go down.");
    }

    /// <summary>Gives a weapon, replacing the one in the same slot (primary or pistol).</summary>
    private void GiveWeapon(CCSPlayerController player, string weapon)
    {
        var pawn = player.PlayerPawn.Value;
        var services = pawn?.WeaponServices;
        var slot = weapon is "weapon_deagle" or "weapon_revolver" or "weapon_usp_silencer" or "weapon_hkp2000" or "weapon_glock"
            or "weapon_p250" or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" or "weapon_elite"
            ? gear_slot_t.GEAR_SLOT_PISTOL
            : gear_slot_t.GEAR_SLOT_RIFLE;

        if (services != null)
        {
            foreach (var handle in services.MyWeapons.ToList())
            {
                if (handle.Value is not { IsValid: true } owned)
                    continue;
                if (new CCSWeaponBase(owned.Handle).VData?.GearSlot != slot)
                    continue;
                services.ActiveWeapon.Raw = handle.Raw;
                player.DropActiveWeapon();
                _papWeapons.Remove(owned.Index);
                owned.Remove();
            }
        }

        player.GiveNamedItem(weapon);
    }

    private static void RefillAmmo(CCSPlayerController player)
    {
        var services = player.PlayerPawn.Value?.WeaponServices;
        if (services == null)
            return;

        foreach (var handle in services.MyWeapons)
        {
            if (handle.Value is not { IsValid: true } weapon)
                continue;
            var data = new CCSWeaponBase(weapon.Handle).VData;
            if (data == null || data.GearSlot is not (gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL))
                continue;

            weapon.Clip1 = data.MaxClip1;
            weapon.ReserveAmmo[0] = data.PrimaryReserveAmmoMax;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
        }
    }

    private static string WeaponName(string weapon) => weapon.Replace("weapon_", "").Replace("_silencer", "-S").ToUpperInvariant();

    // ---------------------------------------------------------------- points

    private static int GetPoints(CCSPlayerController player) => player.InGameMoneyServices?.Account ?? 0;

    private static void SetPoints(CCSPlayerController player, int points)
    {
        var money = player.InGameMoneyServices;
        if (money == null)
            return;
        money.Account = Math.Clamp(points, 0, 65535);
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private void AddPoints(CCSPlayerController player, int points) => SetPoints(player, GetPoints(player) + (DoublePoints ? points * 2 : points));

    private static bool TrySpend(CCSPlayerController player, int price)
    {
        var points = GetPoints(player);
        if (points < price)
        {
            Chat.To(player, $"{{red}}Not enough points{{default}}: {price} needed, you have {points}.");
            return false;
        }
        SetPoints(player, points - price);
        return true;
    }

    // ---------------------------------------------------------------- perk damage

    private HookResult OnDamagePre(CBaseEntity victimEntity, CTakeDamageInfo info)
    {
        if (!IsActive || victimEntity.DesignerName != "player")
            return HookResult.Continue;

        var victim = new CCSPlayerPawn(victimEntity.Handle).OriginalController.Value;
        if (victim != null && HasPerk(victim.Slot, ZombiePerk.QuickRevive))
            info.DamageFlags |= TakeDamageFlags_t.DFLAG_PREVENT_DEATH;

        if (info.Attacker.Value is { DesignerName: "player" } attackerEntity &&
            (info.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0 &&
            new CCSPlayerPawn(attackerEntity.Handle).OriginalController.Value is { } attacker && IsHuman(attacker))
        {
            if (HasPerk(attacker.Slot, ZombiePerk.DoubleTap))
                info.Damage *= Cfg.DoubleTapDamage;
            info.Damage *= PapDamage(attacker);
        }

        // Insta-Kill: any hit from a survivor kills a zombie.
        if (InstaKill && victim != null && IsZombie(victim) && info.Attacker.Value is { DesignerName: "player" } killerEntity &&
            IsHuman(new CCSPlayerPawn(killerEntity.Handle).OriginalController.Value))
            info.Damage = 99999f;

        return HookResult.Continue;
    }

    /// <summary>Quick Revive: a deadly hit left you at 1 HP, so the perk is used up and you're healed.</summary>
    private void OnDamagePost(CBaseEntity victimEntity, CTakeDamageInfo info, CTakeDamageResult result)
    {
        if (!IsActive || _perks.Count == 0 || victimEntity.DesignerName != "player")
            return;

        var pawn = new CCSPlayerPawn(victimEntity.Handle);
        var victim = pawn.OriginalController.Value;
        if (victim == null || !HasPerk(victim.Slot, ZombiePerk.QuickRevive) || pawn.Health > 1 || result.DamageDealt < result.HealthBefore)
            return;

        _perks[victim.Slot].Remove(ZombiePerk.QuickRevive);
        var slot = victim.Slot;
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player?.PlayerPawn.Value is not { } alive || !player.PawnIsAlive)
                return;
            SetHealth(alive, alive.MaxHealth, alive.MaxHealth);
            Chat.To(player, "{gold}QUICK REVIVE!{default} A deadly hit was blocked. Buy the perk again to keep it.");
        });
    }
}
