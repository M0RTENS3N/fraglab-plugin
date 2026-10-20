using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace FragLab.Skins;

/// <summary>Writes paint kits, knife types and gloves onto game entities.</summary>
public sealed class SkinApplier
{
    private readonly BasePlugin _plugin;
    private readonly SkinCatalog _catalog;
    private MemoryFunctionVoid<nint, string, float>? _setAttribute;
    private ulong _nextItemId = 68000;

    public SkinApplier(BasePlugin plugin, SkinCatalog catalog)
    {
        _plugin = plugin;
        _catalog = catalog;
    }

    public void Init(SkinsConfig config)
    {
        var signature = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? config.SignatureLinux : config.SignatureWindows;
        _setAttribute = null;
        try
        {
            if (NativeAPI.FindSignature(Addresses.ServerPath, signature) != IntPtr.Zero)
                _setAttribute = new MemoryFunctionVoid<nint, string, float>(signature);
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError("Skins: signature lookup failed: {Message}", ex.Message);
        }

        if (_setAttribute == null)
            _plugin.Logger.LogError("Skins: CAttributeList_SetOrAddAttributeValueByName signature not found. Skins may not show; " +
                                    "update SignatureLinux/SignatureWindows in the config.");
    }

    public void ApplyWeapon(CCSPlayerController player, CBasePlayerWeapon weapon, PlayerSkins prefs)
    {
        var item = weapon.AttributeManager.Item;
        int defindex = item.ItemDefinitionIndex;
        var isKnife = SkinCatalog.IsKnifeClass(weapon.DesignerName);

        if (isKnife && prefs.Knife is { } knife)
        {
            if (knife != defindex)
            {
                weapon.AcceptInput("ChangeSubclass", value: knife.ToString());
                RedeployIfActive(player, weapon);
            }
            item.ItemDefinitionIndex = (ushort)knife;
            defindex = knife;
        }

        var hasChoice = prefs.Items.TryGetValue(defindex, out var choice) && choice.Paint > 0;
        if (!hasChoice && !(isKnife && prefs.Knife != null))
            return;

        // Quality 3 shows the ★ on knives.
        item.EntityQuality = isKnife ? 3 : 0;
        SetItemIds(item, player);
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();

        if (!hasChoice)
            return;

        weapon.FallbackPaintKit = choice!.Paint;
        weapon.FallbackSeed = choice.Seed;
        weapon.FallbackWear = choice.Wear;
        SetPaintAttributes(item, choice);

        // Older skins use the legacy weapon mesh.
        var legacy = _catalog.Skin(defindex, choice.Paint)?.Legacy ?? false;
        weapon.AcceptInput("SetBodygroup", value: $"body,{(legacy ? 1 : 0)}");
    }

    public void ApplyGloves(CCSPlayerController player, PlayerSkins prefs)
    {
        if (GloveRules.Suppress(player))
            return;

        if (prefs.Gloves is not { } gloves || !prefs.Items.TryGetValue(gloves, out var choice) || choice.Paint == 0)
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return;

        // Same steps and timing as WeaponPaints: switch weapon, reset, set the gloves a moment later,
        // switch back, then flip the first/third-person body group so the new gloves get drawn.
        var item = pawn.EconGloves;
        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
        item.AttributeList.Attributes.RemoveAll();
        player.ExecuteClientCommand("lastinv");

        // The glove model only refreshes when the change lands a little after the reset.
        _plugin.AddTimer(0.08f, () =>
        {
            if (!player.IsValid || !player.PawnIsAlive || pawn.Handle != player.PlayerPawn.Value?.Handle)
                return;

            item.ItemDefinitionIndex = (ushort)gloves;
            SetItemIds(item, player);
            SetPaintAttributes(item, choice);
            item.Initialized = true;

            player.ExecuteClientCommand("lastinv");
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
            _plugin.AddTimer(0.2f, () =>
            {
                if (pawn.IsValid)
                    pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// A knife that changes type while held keeps the old knife's hand animation (the blade goes through the hand).
    /// Switching to another weapon and back draws it again with the right animation.
    /// </summary>
    private void RedeployIfActive(CCSPlayerController player, CBasePlayerWeapon knife)
    {
        var services = player.PlayerPawn.Value?.WeaponServices;
        if (services?.ActiveWeapon.Value?.Index != knife.Index)
            return;

        var other = services.MyWeapons.Select(h => h.Value)
            .FirstOrDefault(w => w != null && w.IsValid && w.Index != knife.Index);
        if (other == null)
            return;

        var otherName = other.DesignerName;
        var knifeName = knife.DesignerName;
        player.ExecuteClientCommand($"use {otherName}");
        _plugin.AddTimer(0.15f, () =>
        {
            if (player.IsValid && player.PawnIsAlive)
                player.ExecuteClientCommand($"use {knifeName}");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void SetPaintAttributes(CEconItemView item, SkinChoice choice)
    {
        if (_setAttribute == null)
            return;

        foreach (var list in new[] { item.NetworkedDynamicAttributes.Handle, item.AttributeList.Handle })
        {
            _setAttribute.Invoke(list, "set item texture prefab", choice.Paint);
            _setAttribute.Invoke(list, "set item texture seed", choice.Seed);
            _setAttribute.Invoke(list, "set item texture wear", choice.Wear);
        }
    }

    private void SetItemIds(CEconItemView item, CCSPlayerController player)
    {
        var id = _nextItemId++;
        item.ItemID = id;
        item.ItemIDLow = (uint)(id & 0xFFFFFFFF);
        item.ItemIDHigh = (uint)(id >> 32);
        item.AccountID = (uint)player.SteamID;
    }
}

/// <summary>Lets a game mode stop gloves from being put on a player (the Ninja perk hides them).</summary>
public static class GloveRules
{
    public static Func<CCSPlayerController, bool> Suppress { get; set; } = _ => false;
}
