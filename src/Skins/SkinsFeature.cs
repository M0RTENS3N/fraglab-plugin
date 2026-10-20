using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using FragLab.Core.Menus;

namespace FragLab.Skins;

/// <summary>
/// Weapon skins, knives and gloves with a center-screen menu (!skins). Works in every game mode.
/// </summary>
public sealed class SkinsFeature
{
    private static readonly (string Name, float Wear)[] WearPresets =
    [
        ("Factory New", 0.0001f),
        ("Minimal Wear", 0.1f),
        ("Field-Tested", 0.25f),
        ("Well-Worn", 0.4f),
        ("Battle-Scarred", 0.6f)
    ];

    private static readonly (string Id, string Name)[] WeaponCategories =
    [
        ("pistol", "Pistols"),
        ("rifle", "Rifles"),
        ("smg", "SMGs"),
        ("heavy", "Heavy"),
        ("equipment", "Equipment")
    ];

    private const string Check = " [x]";

    private readonly FragLabPlugin _plugin;
    private readonly SkinCatalog _catalog = new();
    private readonly PlayerDataStore<PlayerSkins> _store;
    private readonly SkinApplier _applier;
    // Weapons we already skinned right when we gave them. Re-applying a frame later (after the client
    // has the weapon) can reset the paint, which is why knives sometimes showed the default color.
    // Entity index -> time. Entries expire, because indexes get reused by later weapons.
    private readonly Dictionary<uint, float> _alreadySkinned = new();

    public SkinsFeature(FragLabPlugin plugin)
    {
        _plugin = plugin;
        _store = new PlayerDataStore<PlayerSkins>(Path.Combine(plugin.ModuleDirectory, "playerdata", "skins"));
        _applier = new SkinApplier(plugin, _catalog);
    }

    private SkinsConfig Cfg => _plugin.Config.Skins;
    private MenuService Menus => _plugin.Menus;

    public bool Enabled { get; private set; }

    public void Load()
    {
        if (!Cfg.Enabled)
            return;

        var path = Path.Combine(_plugin.ModuleDirectory, "data", "skins.json");
        try
        {
            _catalog.Load(path);
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError("Skins disabled: could not load {Path}: {Message}", path, ex.Message);
            return;
        }

        _applier.Init(Cfg);
        Enabled = true;

        _plugin.RegisterListener<Listeners.OnEntityCreated>(OnEntityCreated);

        _plugin.RegisterEventHandler<EventPlayerSpawn>((e, _) =>
        {
            var player = e.Userid;
            if (Players.IsValid(player) && !player!.IsBot && CanApply(player))
                _plugin.AddTimer(0.1f, () =>
                {
                    if (Players.IsValid(player))
                        _applier.ApplyGloves(player, _store.Get(player.SteamID));
                });
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid is { IsValid: true, IsBot: false })
                _store.Forget(e.Userid.SteamID);
            return HookResult.Continue;
        });

        _plugin.AddCommand("css_skins", "Open the skins menu", (p, _) => OpenSkins(p));
        _plugin.AddCommand("css_ws", "Open the skins menu", (p, _) => OpenSkins(p));
        _plugin.AddCommand("css_knife", "Choose your knife", (p, _) => WithPlayer(p, OpenKnifeMenu));
        _plugin.AddCommand("css_gloves", "Choose your gloves", (p, _) => WithPlayer(p, OpenGlovesMenu));
        _plugin.AddCommand("css_skin", "Search skins for the weapon in your hand: !skin [name]", OnSearchCommand);
        _plugin.AddCommand("css_wear", "Set wear of the weapon in your hand: !wear <0-1|fn|mw|ft|ww|bs>", OnWearCommand);
        _plugin.AddCommand("css_seed", "Set pattern seed of the weapon in your hand: !seed <0-1000>", OnSeedCommand);

        _plugin.AddHelp("Skins", ["skins", "ws"], "Open the skins menu");
        _plugin.AddHelp("Skins", ["knife"], "Choose your knife");
        _plugin.AddHelp("Skins", ["gloves"], "Choose your gloves");
        _plugin.AddHelp("Skins", ["skin"], "Skins for the weapon in your hand: !skin [name]");
        _plugin.AddHelp("Skins", ["wear"], "Wear of the weapon in your hand: !wear <fn|mw|ft|ww|bs|0-1>");
        _plugin.AddHelp("Skins", ["seed"], "Pattern of the weapon in your hand: !seed <0-1000>");

        _plugin.Logger.LogInformation("Skins loaded: {Count} paint kits", _catalog.SkinCount);
    }

    // ---------------------------------------------------------------- applying

    private void OnEntityCreated(CEntityInstance entity)
    {
        if (!entity.DesignerName.StartsWith("weapon_"))
            return;

        // Ownership is only known a frame after creation.
        Server.NextFrame(() =>
        {
            if (!entity.IsValid)
                return;

            var weapon = new CBasePlayerWeapon(entity.Handle);
            if (_alreadySkinned.Remove(weapon.Index, out var skinnedAt) && Server.CurrentTime - skinnedAt < 1f)
                return;

            var player = FindOwner(weapon);
            if (player == null || player.IsBot || !CanApply(player))
                return;

            // Knives: the default knife is turned into the chosen one (ChangeSubclass), the way WeaponPaints does it.
            // Giving the real knife class instead isn't reliable in CS2.
            _applier.ApplyWeapon(player, weapon, _store.Get(player.SteamID));
        });
    }

    private static bool IsDefaultKnife(string designerName) => designerName is "weapon_knife" or "weapon_knife_t";

    private static string DefaultKnifeClass(CCSPlayerController player) =>
        player.Team == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife";

    public void Unload()
    {
    }

    private static CCSPlayerController? FindOwner(CBasePlayerWeapon weapon)
    {
        var xuid = weapon.OriginalOwnerXuidLow;
        if (xuid != 0)
            return Players.Humans().FirstOrDefault(p => (uint)p.SteamID == xuid);

        // Only trust the owner handle when it really is a player pawn.
        var owner = weapon.OwnerEntity.Value;
        if (owner == null || !owner.IsValid || owner.DesignerName != "player")
            return null;
        var controller = new CCSPlayerPawn(owner.Handle).OriginalController.Value;
        return Players.IsValid(controller) ? controller : null;
    }

    /// <summary>Can hand-pick any skin (admins by default).</summary>
    private bool CanCustomize(CCSPlayerController player) =>
        string.IsNullOrEmpty(Cfg.RequiredFlag) || AdminManager.PlayerHasPermissions(player, Cfg.RequiredFlag);

    /// <summary>Gets skins applied: either allowed to customize, or using one of the ready-made loadouts.</summary>
    private bool CanApply(CCSPlayerController player) =>
        CanCustomize(player) || FindPreset(_store.Get(player.SteamID).Preset) != null;

    private SkinPreset? FindPreset(string? name) =>
        name == null ? null : Cfg.Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>!skins: the full menu for admins, the loadout picker for everyone else.</summary>
    private void OpenSkins(CCSPlayerController? player)
    {
        if (player == null || !Players.IsValid(player))
            return;

        if (CanCustomize(player))
            OpenMainMenu(player);
        else if (Cfg.Presets.Count > 0)
            OpenPresetMenu(player);
        else
            Chat.To(player, "{red}You don't have access to skins.");
    }

    private void OpenPresetMenu(CCSPlayerController player)
    {
        Menus.Open(player, new Menu("Skins > Loadouts", menu =>
        {
            var current = _store.Get(player.SteamID).Preset;
            foreach (var preset in Cfg.Presets)
            {
                var marker = preset.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? " [x]" : "";
                menu.Add($"{preset.Name} loadout{marker}", p => ApplyPreset(p, preset), preset.Color);
            }
            menu.Add("No loadout (default skins)" + (current == null ? " [x]" : ""), p => ApplyPreset(p, null), "#888888");
        }));
    }

    /// <summary>Replaces the player's skins with a ready-made loadout (or clears them) and shows it right away.</summary>
    private void ApplyPreset(CCSPlayerController player, SkinPreset? preset)
    {
        var prefs = _store.Get(player.SteamID);
        prefs.Items.Clear();
        prefs.Gloves = null;
        prefs.Knife = null;
        prefs.Preset = preset?.Name;

        if (preset != null)
        {
            foreach (var (className, paint) in preset.Weapons)
            {
                if (_catalog.ItemByClass(className) is { } item)
                    prefs.Items[item.Defindex] = NewChoice(prefs, item.Defindex, paint);
            }

            if (preset.Knife.Length > 0 && _catalog.ItemByClass(preset.Knife) is { } knife)
            {
                prefs.Knife = knife.Defindex;
                if (preset.KnifePaint > 0)
                    prefs.Items[knife.Defindex] = NewChoice(prefs, knife.Defindex, preset.KnifePaint);
            }
        }

        _store.Save(player.SteamID);
        Chat.To(player, preset == null ? "Loadout removed." : $"{{green}}{preset.Name} loadout{{default}} equipped.");
        RefreshAll(player);
    }

    private SkinChoice NewChoice(PlayerSkins prefs, int defindex, int paint) => new()
    {
        Paint = paint,
        Wear = ClampWear(prefs.PreferredWear, _catalog.Skin(defindex, paint)),
        Seed = 0
    };

    /// <summary>Re-gives every weapon the player carries so all new skins show at once.</summary>
    private void RefreshAll(CCSPlayerController player)
    {
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons
            .Select(h => h.Value)
            .Where(w => w != null && w.IsValid && CanRegive(w))
            .ToList();
        if (weapons == null || !player.PawnIsAlive)
            return;

        foreach (var weapon in weapons)
            Regive(player, weapon!);
    }
    private void WithPlayer(CCSPlayerController? player, Action<CCSPlayerController> action)
    {
        if (player == null || !Players.IsValid(player))
            return;
        if (!CanCustomize(player))
        {
            Chat.To(player, Cfg.Presets.Count > 0
                ? "Only admins can pick single skins. Type {green}!skins{default} to choose a loadout."
                : "{red}You don't have access to skins.");
            return;
        }
        action(player);
    }

    /// <summary>Saves a selection and shows it right away where possible.</summary>
    private void Select(CCSPlayerController player, int defindex, int paint)
    {
        var prefs = _store.Get(player.SteamID);
        prefs.Preset = null; // hand-picked from now on
        if (paint == 0)
        {
            prefs.Items.Remove(defindex);
        }
        else
        {
            var skin = _catalog.Skin(defindex, paint);
            var seed = prefs.Items.TryGetValue(defindex, out var old) ? old.Seed : 0;
            prefs.Items[defindex] = new SkinChoice
            {
                Paint = paint,
                Wear = ClampWear(prefs.PreferredWear, skin),
                Seed = seed
            };
        }

        _store.Save(player.SteamID);
        Refresh(player, defindex);
    }

    private void Refresh(CCSPlayerController player, int defindex)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null || !player.PawnIsAlive)
            return;

        var item = _catalog.Item(defindex);
        if (item?.Category == "gloves")
        {
            _applier.ApplyGloves(player, _store.Get(player.SteamID));
            // Redrawing the held weapon makes the first-person gloves update.
            _plugin.AddTimer(0.4f, () =>
            {
                var active = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
                if (Players.IsValid(player) && player.PawnIsAlive && active != null && active.IsValid && CanRegive(active))
                    Regive(player, active);
            });
            return;
        }

        var isKnife = item?.Category == "knife";
        var weapon = pawn.WeaponServices.MyWeapons
            .Select(h => h.Value)
            .FirstOrDefault(w => w != null && w.IsValid &&
                                 (isKnife ? SkinCatalog.IsKnifeClass(w.DesignerName) : w.AttributeManager.Item.ItemDefinitionIndex == defindex));
        if (weapon == null)
            return;

        if (!Cfg.RefreshWeaponOnSelect)
        {
            Chat.To(player, "Your new skin will show the next time you get this weapon.");
            return;
        }

        Regive(player, weapon);
    }

    private void RefreshKnife(CCSPlayerController player)
    {
        var knife = player.PlayerPawn.Value?.WeaponServices?.MyWeapons
            .Select(h => h.Value)
            .FirstOrDefault(w => w != null && w.IsValid && SkinCatalog.IsKnifeClass(w.DesignerName));
        if (knife != null && player.PawnIsAlive)
            Regive(player, knife);
    }

    private static bool CanRegive(CBasePlayerWeapon weapon) =>
        weapon.DesignerName != "weapon_c4" && !weapon.DesignerName.Contains("grenade") &&
        weapon.DesignerName is not ("weapon_flashbang" or "weapon_molotov" or "weapon_decoy");

    /// <summary>
    /// Replaces a weapon with a fresh copy so the new skin shows immediately. Ammo is carried over,
    /// so this can't be used to refill mid-round.
    /// </summary>
    private void Regive(CCSPlayerController player, CBasePlayerWeapon weapon)
    {
        var isKnife = SkinCatalog.IsKnifeClass(weapon.DesignerName);
        var className = isKnife
            ? DefaultKnifeClass(player)
            : _catalog.Item(weapon.AttributeManager.Item.ItemDefinitionIndex)?.ClassName ?? weapon.DesignerName;

        var wasActive = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.Handle == weapon.Handle;
        var clip = weapon.Clip1;
        var reserve = weapon.ReserveAmmo.Length > 0 ? weapon.ReserveAmmo[0] : 0;

        player.RemoveItemByDesignerName(weapon.DesignerName, true);
        Server.NextFrame(() =>
        {
            if (!Players.IsValid(player) || !player.PawnIsAlive)
                return;

            var given = player.GiveNamedItem<CBasePlayerWeapon>(className);
            if (given == null || !given.IsValid)
                return;

            // Apply right away instead of waiting for ownership to resolve.
            _applier.ApplyWeapon(player, given, _store.Get(player.SteamID));
            _alreadySkinned[given.Index] = Server.CurrentTime;

            if (!isKnife)
            {
                Server.NextFrame(() =>
                {
                    if (!given.IsValid)
                        return;
                    given.Clip1 = clip;
                    if (given.ReserveAmmo.Length > 0)
                        given.ReserveAmmo[0] = reserve;
                    Utilities.SetStateChanged(given, "CBasePlayerWeapon", "m_iClip1");
                    Utilities.SetStateChanged(given, "CBasePlayerWeapon", "m_pReserveAmmo");
                });
            }

            if (wasActive)
                player.ExecuteClientCommandFromServer($"use {className}");
        });
    }

    private static float ClampWear(float wear, SkinPaintDef? skin) =>
        skin == null ? wear : Math.Clamp(wear, skin.MinFloat, Math.Max(skin.MinFloat, skin.MaxFloat));

    private static string WearName(float wear) => wear switch
    {
        < 0.07f => "Factory New",
        < 0.15f => "Minimal Wear",
        < 0.38f => "Field-Tested",
        < 0.45f => "Well-Worn",
        _ => "Battle-Scarred"
    };

    // ---------------------------------------------------------------- menus

    private void OpenMainMenu(CCSPlayerController player)
    {
        Menus.Open(player, new Menu("Skins", menu =>
        {
            var prefs = _store.Get(player.SteamID);
            var knife = prefs.Knife is { } k ? _catalog.Item(k)?.Name ?? "Custom" : "Default";
            var gloves = prefs.Gloves is { } g ? _catalog.Item(g)?.Name ?? "Custom" : "Default";

            menu.Add("Weapon skins", OpenCategoryMenu);
            if (Cfg.Presets.Count > 0)
                menu.Add($"Loadouts{(prefs.Preset != null ? $": {prefs.Preset}" : "")}", OpenPresetMenu, "#FFD700");
            menu.Add($"Knife: {knife}", OpenKnifeMenu);
            menu.Add($"Gloves: {gloves}", OpenGlovesMenu);
            menu.Add($"Wear: {WearName(prefs.PreferredWear)}", OpenWearMenu);
            menu.Add("Reset everything", p =>
            {
                var all = _store.Get(p.SteamID);
                all.Items.Clear();
                all.Knife = null;
                all.Gloves = null;
                all.Preset = null;
                _store.Save(p.SteamID);
                Chat.To(p, "All skins reset. Changes show next round.");
            }, "#eb4b4b");
        }));
    }

    private void OpenCategoryMenu(CCSPlayerController player)
    {
        Menus.Open(player, new Menu("Skins > Weapons", menu =>
        {
            foreach (var (id, name) in WeaponCategories)
                menu.Add(name, p => OpenWeaponMenu(p, id, name));
        }));
    }

    private void OpenWeaponMenu(CCSPlayerController player, string category, string title)
    {
        Menus.Open(player, new Menu($"Skins > {title}", menu =>
        {
            var prefs = _store.Get(player.SteamID);
            foreach (var weapon in _catalog.ItemsIn(category))
            {
                var marker = prefs.Items.ContainsKey(weapon.Defindex) ? Check : "";
                menu.Add(weapon.Name + marker, p => OpenSkinMenu(p, weapon, allowDefault: true));
            }
        }));
    }

    private void OpenKnifeMenu(CCSPlayerController player)
    {
        Menus.Open(player, new Menu("Skins > Knife", menu =>
        {
            var prefs = _store.Get(player.SteamID);
            menu.Add("Default knife" + (prefs.Knife == null ? Check : ""), p =>
            {
                _store.Get(p.SteamID).Knife = null;
                _store.Save(p.SteamID);
                RefreshKnife(p);
            });

            foreach (var knife in _catalog.ItemsIn("knife"))
            {
                var marker = prefs.Knife == knife.Defindex ? Check : "";
                menu.Add(knife.Name + marker, p =>
                {
                    _store.Get(p.SteamID).Knife = knife.Defindex;
                    _store.Save(p.SteamID);
                    Refresh(p, knife.Defindex);
                    OpenSkinMenu(p, knife, allowDefault: true);
                });
            }
        }));
    }

    private void OpenGlovesMenu(CCSPlayerController player)
    {
        Menus.Open(player, new Menu("Skins > Gloves", menu =>
        {
            var prefs = _store.Get(player.SteamID);
            menu.Add("Default gloves" + (prefs.Gloves == null ? Check : ""), p =>
            {
                _store.Get(p.SteamID).Gloves = null;
                _store.Save(p.SteamID);
                Chat.To(p, "Default gloves selected. They return next round.");
            });

            foreach (var glove in _catalog.ItemsIn("gloves"))
            {
                var marker = prefs.Gloves == glove.Defindex ? Check : "";
                menu.Add(glove.Name + marker, p => OpenSkinMenu(p, glove, allowDefault: false));
            }
        }));
    }

    private void OpenSkinMenu(CCSPlayerController player, SkinItemDef item, bool allowDefault, IReadOnlyList<SkinPaintDef>? skins = null)
    {
        skins ??= _catalog.SkinsFor(item.Defindex);
        var isGloves = item.Category == "gloves";

        Menus.Open(player, new Menu($"Skins > {item.Name}", menu =>
        {
            var prefs = _store.Get(player.SteamID);
            var current = prefs.Items.TryGetValue(item.Defindex, out var choice) ? choice.Paint : 0;

            if (allowDefault)
            {
                var label = item.Category == "knife" ? "Vanilla" : "Default";
                menu.Add(label + (current == 0 ? Check : ""), p => Select(p, item.Defindex, 0));
            }

            foreach (var skin in skins)
            {
                var marker = current == skin.Paint && (!isGloves || prefs.Gloves == item.Defindex) ? Check : "";
                menu.Add(skin.Name + marker, p =>
                {
                    if (isGloves)
                        _store.Get(p.SteamID).Gloves = item.Defindex;
                    Select(p, item.Defindex, skin.Paint);
                }, skin.Rarity);
            }

            if (skins.Count == 0 && !allowDefault)
                menu.Add("No skins available");
        }));
    }

    private void OpenWearMenu(CCSPlayerController player)
    {
        Menus.Open(player, new Menu("Skins > Wear", menu =>
        {
            var prefs = _store.Get(player.SteamID);
            menu.Add("Applies to skins you pick from now on");
            foreach (var (name, wear) in WearPresets)
            {
                var marker = WearName(prefs.PreferredWear) == name ? Check : "";
                menu.Add(name + marker, p =>
                {
                    _store.Get(p.SteamID).PreferredWear = wear;
                    _store.Save(p.SteamID);
                });
            }
        }));
    }

    // ---------------------------------------------------------------- chat commands

    private CBasePlayerWeapon? HeldWeapon(CCSPlayerController player) =>
        player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

    private void OnSearchCommand(CCSPlayerController? player, CommandInfo info)
    {
        WithPlayer(player, p =>
        {
            var weapon = HeldWeapon(p);
            var item = weapon == null ? null : _catalog.Item(weapon.AttributeManager.Item.ItemDefinitionIndex);
            if (item == null)
            {
                Chat.To(p, "Hold the weapon you want to search skins for.");
                return;
            }

            var query = info.ArgString.Trim().Trim('"');
            var matches = _catalog.SkinsFor(item.Defindex)
                .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                Chat.To(p, $"No {item.Name} skins match '{query}'.");
                return;
            }

            OpenSkinMenu(p, item, allowDefault: query.Length == 0, matches);
        });
    }

    private void OnWearCommand(CCSPlayerController? player, CommandInfo info)
    {
        WithPlayer(player, p =>
        {
            var arg = info.ArgCount > 1 ? info.GetArg(1).ToLowerInvariant() : "";
            float? wear = arg switch
            {
                "fn" => WearPresets[0].Wear,
                "mw" => WearPresets[1].Wear,
                "ft" => WearPresets[2].Wear,
                "ww" => WearPresets[3].Wear,
                "bs" => WearPresets[4].Wear,
                _ when float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is >= 0 and <= 1 => f,
                _ => null
            };

            if (wear == null)
            {
                Chat.To(p, "Usage: !wear <0-1|fn|mw|ft|ww|bs>");
                return;
            }

            UpdateHeldChoice(p, (choice, skin) => choice.Wear = ClampWear(wear.Value, skin),
                choice => $"Wear set to {{green}}{choice.Wear:0.####}{{default}} ({WearName(choice.Wear)}).");
        });
    }

    private void OnSeedCommand(CCSPlayerController? player, CommandInfo info)
    {
        WithPlayer(player, p =>
        {
            if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out var seed) || seed is < 0 or > 1000)
            {
                Chat.To(p, "Usage: !seed <0-1000>");
                return;
            }

            UpdateHeldChoice(p, (choice, _) => choice.Seed = seed,
                choice => $"Pattern seed set to {{green}}{choice.Seed}{{default}}.");
        });
    }

    private void UpdateHeldChoice(CCSPlayerController player, Action<SkinChoice, SkinPaintDef?> update, Func<SkinChoice, string> message)
    {
        var weapon = HeldWeapon(player);
        if (weapon == null)
        {
            Chat.To(player, "Hold the weapon you want to change.");
            return;
        }

        int defindex = weapon.AttributeManager.Item.ItemDefinitionIndex;
        var prefs = _store.Get(player.SteamID);
        if (!prefs.Items.TryGetValue(defindex, out var choice))
        {
            Chat.To(player, "This weapon has no skin selected. Pick one with !skins first.");
            return;
        }

        update(choice, _catalog.Skin(defindex, choice.Paint));
        _store.Save(player.SteamID);
        Chat.To(player, message(choice));
        Refresh(player, defindex);
    }
}
