using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using FragLab.Core;

namespace FragLab.Modes.Retake;

/// <summary>
/// Admin round rules for retake and fun retake: Deagle only, AWP only and headshots only.
/// They last until turned off or the mode is switched. Perks that hand out their own weapons
/// (Ninja, Gunslinger, Super Zeus) still replace the weapon.
/// </summary>
public sealed partial class RetakeMode
{
    private bool _hsOnly;

    private void RegisterWeaponRules()
    {
        AddCommand(["deagleonly", "deagle"], "Toggle Deagle only rounds", (p, _) => ToggleOnlyWeapon(p, "weapon_deagle", "Deagle"), adminOnly: true);
        AddCommand(["awponly", "awp"], "Toggle AWP only rounds", (p, _) => ToggleOnlyWeapon(p, "weapon_awp", "AWP"), adminOnly: true);
        AddCommand(["hsonly", "headshotonly"], "Toggle headshots only (body shots do no damage)", (p, _) => ToggleHsOnly(p), adminOnly: true);
        AddCommand(["normalweapons", "allweapons"], "Back to the normal weapons and damage", (p, _) => ResetWeaponRules(p), adminOnly: true);

        Plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(HsOnlyDamage);
    }

    private void ToggleOnlyWeapon(CCSPlayerController? player, string weapon, string name)
    {
        _allocator.OnlyWeapon = _allocator.OnlyWeapon == weapon ? null : weapon;
        Chat.All(_allocator.OnlyWeapon == null
            ? $"{{green}}{name} only{{default}} is off from the next round."
            : $"{{green}}{name} only{{default}} from the next round. Buying is off.");
    }

    private void ToggleHsOnly(CCSPlayerController? player)
    {
        _hsOnly = !_hsOnly;
        Chat.All(_hsOnly
            ? "{green}Headshots only{default} is on: body shots do no damage."
            : "{green}Headshots only{default} is off.");
    }

    private void ResetWeaponRules(CCSPlayerController? player)
    {
        _allocator.OnlyWeapon = null;
        _hsOnly = false;
        Chat.All("Normal weapons and damage from the next round.");
    }

    private void ClearWeaponRules()
    {
        _allocator.OnlyWeapon = null;
        _hsOnly = false;
    }

    /// <summary>"Deagle only" etc. for the round start messages; null when no rule is on.</summary>
    private string? WeaponRuleText()
    {
        var parts = new List<string>();
        if (_allocator.OnlyWeapon == "weapon_deagle")
            parts.Add("Deagle only");
        else if (_allocator.OnlyWeapon == "weapon_awp")
            parts.Add("AWP only");
        if (_hsOnly)
            parts.Add("Headshots only");
        return parts.Count == 0 ? null : string.Join(" + ", parts);
    }

    private HookResult HsOnlyDamage(CBaseEntity victim, CTakeDamageInfo info)
    {
        if (!IsActive || !_hsOnly || _editMode || _warmup || victim.DesignerName != "player")
            return HookResult.Continue;

        if ((info.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0 && info.GetHitGroup() != HitGroup_t.HITGROUP_HEAD)
            info.Damage = 0;
        return HookResult.Continue;
    }
}
