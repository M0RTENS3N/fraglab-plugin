using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using FragLab.Core.Menus;

namespace FragLab.Modes.Retake;

/// <summary>
/// Spawn editor: !retakeedit enters edit mode and opens a menu you can keep open while walking
/// (R next, Shift+R previous, E select). !sm opens/closes it. "Exit edit mode" (or !retakeedit again) leaves.
/// </summary>
public sealed partial class RetakeMode
{
    private Bombsite _editorSite = Bombsite.A;

    private void RegisterEditor()
    {
        AddCommand(["retakeedit"], "Enter/exit spawn edit mode", (p, _) => ToggleEditMode(p), adminOnly: true);
        AddCommand(["spawnmenu", "sm"], "Open/close the spawn editor menu", (p, _) => ToggleEditorMenu(p), adminOnly: true);
        AddCommand(["addspawn"], "Add a spawn at your position: !addspawn <t|ct> <a|b> [planter]", OnAddSpawn, adminOnly: true);
        AddCommand(["delspawn"], "Delete the spawn closest to you", (p, _) => DeleteNearestSpawn(p), adminOnly: true);
        AddCommand(["spawns"], "Show spawn counts for this map", (p, _) => PrintSpawnCounts(p), adminOnly: true);
        AddCommand(["savespawns"], "Save spawns for this map", (p, _) => SaveSpawns(p), adminOnly: true);
    }

    private void ToggleEditMode(CCSPlayerController? admin)
    {
        if (_editMode)
            ExitEditMode(admin);
        else
            EnterEditMode(admin);
    }

    private void EnterEditMode(CCSPlayerController? admin)
    {
        StopWarmupTimer();
        _warmup = false;
        _editMode = true;
        Server.ExecuteCommand("mp_warmup_start");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Chat.All("{red}Retake edit mode enabled.{default} Rounds are paused while spawns are edited.");
        Chat.To(admin, "Walk around with the menu open: {green}R{default} next, {green}Shift+R{default} previous, {green}E{default} select. {green}!sm{default} opens/closes it.");
        PrintSpawnCounts(admin);
        OpenEditorMenu(admin);
    }

    private void ExitEditMode(CCSPlayerController? admin, bool discardChanges = false)
    {
        if (!_editMode)
            return;

        if (discardChanges && _spawns.Map != null)
        {
            _spawns.Load(_spawns.Map);
            Chat.To(admin, "Unsaved spawn changes discarded.");
        }

        _editMode = false;
        if (admin != null && Plugin.Menus.IsOpen(admin))
            Plugin.Menus.Close(admin);

        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        Chat.All("Retake edit mode disabled. Retakes are starting.");
    }

    private void ToggleEditorMenu(CCSPlayerController? admin)
    {
        if (admin != null && Plugin.Menus.IsOpen(admin))
            Plugin.Menus.Close(admin);
        else
            OpenEditorMenu(admin);
    }

    private void OpenEditorMenu(CCSPlayerController? admin)
    {
        if (admin == null)
            return;

        if (!_editMode)
        {
            Chat.To(admin, "Enter edit mode first with !retakeedit.");
            return;
        }

        Plugin.Menus.Open(admin, new Menu("Spawn editor", menu =>
        {
            var site = _editorSite;
            menu.Add($"Site: {site}  (select to switch)", _ => _editorSite = site == Bombsite.A ? Bombsite.B : Bombsite.A, "#00FF7F");
            menu.Add($"Add T spawn ({CountLabel(site, CsTeam.Terrorist, planter: false)})", p => AddSpawnHere(p, CsTeam.Terrorist, site, false), "#EAD18A");
            menu.Add($"Add T planter spawn ({CountLabel(site, CsTeam.Terrorist, planter: true)})", p => AddSpawnHere(p, CsTeam.Terrorist, site, true), "#EAD18A");
            menu.Add($"Add CT spawn ({CountLabel(site, CsTeam.CounterTerrorist, planter: false)})", p => AddSpawnHere(p, CsTeam.CounterTerrorist, site, false), "#5EB8FF");
            menu.Add("Delete nearest spawn", DeleteNearestSpawn, "#eb4b4b");
            menu.Add(_spawns.IsDirty ? "Save spawns (unsaved changes!)" : "Save spawns", p => SaveSpawns(p), _spawns.IsDirty ? "#FFA500" : null);
            menu.Add("Reset to default spawns", OpenResetMenu, "#888888");
            menu.Add("Exit edit mode", OpenExitMenu);
            menu.Add("Close menu (!sm reopens)", p => Plugin.Menus.Close(p), "#888888");
        }) { AllowMovement = true });
    }

    private void OpenExitMenu(CCSPlayerController admin)
    {
        if (!_spawns.IsDirty)
        {
            ExitEditMode(admin);
            return;
        }

        Plugin.Menus.Open(admin, new Menu("You have unsaved spawns", menu =>
        {
            menu.Add("Save and exit", p =>
            {
                if (SaveSpawns(p))
                    ExitEditMode(p);
            }, "#00FF7F");
            menu.Add("Exit without saving", p => ExitEditMode(p, discardChanges: true), "#eb4b4b");
            menu.Add("Cancel", p => Plugin.Menus.Close(p));
        }) { AllowMovement = true });
    }

    private void OpenResetMenu(CCSPlayerController admin)
    {
        Plugin.Menus.Open(admin, new Menu("Delete your custom spawns for this map?", menu =>
        {
            menu.Add("Yes, use the built-in default spawns", p =>
            {
                try
                {
                    var hasDefaults = _spawns.ResetToDefaults();
                    Chat.To(p, hasDefaults
                        ? $"{{green}}Back to the default spawns{{default}} ({_spawns.Spawns.Count})."
                        : "Custom spawns deleted. There are no default spawns for this map.");
                    PrintSpawnCounts(p);
                }
                catch (Exception ex)
                {
                    Chat.To(p, $"{{red}}Reset failed: {ex.Message}");
                }
                Plugin.Menus.Close(p);
                OpenEditorMenu(p);
            }, "#eb4b4b");
            menu.Add("Cancel", p =>
            {
                Plugin.Menus.Close(p);
                OpenEditorMenu(p);
            });
        }) { AllowMovement = true });
    }

    private string CountLabel(Bombsite site, CsTeam team, bool planter)
    {
        var spawns = _spawns.For(site, team);
        return team == CsTeam.Terrorist
            ? $"{spawns.Count(s => s.IsPlanter == planter)} {(planter ? "planter" : "normal")}"
            : $"{spawns.Count()} total";
    }

    private void OnAddSpawn(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 3 || !TryParseTeam(info.GetArg(1), out var team) || !Enum.TryParse<Bombsite>(info.GetArg(2), true, out var site))
        {
            Chat.To(player, "Usage: !addspawn <t|ct> <a|b> [planter]");
            return;
        }

        var isPlanter = info.ArgCount > 3 && info.GetArg(3).Equals("planter", StringComparison.OrdinalIgnoreCase);
        AddSpawnHere(player, team, site, isPlanter);
    }

    private void AddSpawnHere(CCSPlayerController? player, CsTeam team, Bombsite site, bool isPlanter)
    {
        if (!RequireEditor(player, out var pawn))
            return;

        if (isPlanter && team != CsTeam.Terrorist)
        {
            Chat.To(player, "Only T spawns can be planter spawns.");
            return;
        }

        var origin = pawn.AbsOrigin!;
        _spawns.Add(new RetakeSpawn
        {
            Team = team,
            Site = site,
            IsPlanter = isPlanter,
            X = origin.X,
            Y = origin.Y,
            Z = origin.Z,
            Yaw = pawn.EyeAngles.Y
        });

        Chat.To(player, $"Added {(isPlanter ? "planter " : "")}{Players.TeamName(team)} spawn for site {{green}}{site}{{default}}. " +
                        $"({_spawns.For(site, team).Count()} total, {{red}}not saved yet{{default}})");
    }

    private void DeleteNearestSpawn(CCSPlayerController? player)
    {
        if (!RequireEditor(player, out var pawn))
            return;

        var nearest = _spawns.Nearest(pawn.AbsOrigin!, 128f);
        if (nearest == null)
        {
            Chat.To(player, "No spawn within 128 units.");
            return;
        }

        _spawns.Remove(nearest);
        Chat.To(player, $"Deleted {Players.TeamName(nearest.Team)} spawn on site {{green}}{nearest.Site}{{default}}. ({{red}}not saved yet{{default}})");
    }

    private bool SaveSpawns(CCSPlayerController? player)
    {
        if (_spawns.Map == null)
        {
            Chat.To(player, "{red}Nothing to save: no map loaded.");
            return false;
        }

        try
        {
            var path = _spawns.Save();
            var reloaded = new SpawnStore(Plugin.ModuleDirectory);
            reloaded.Load(_spawns.Map);

            Chat.To(player, $"{{green}}Saved {reloaded.Spawns.Count} spawns for {_spawns.Map}.");
            PrintSpawnCounts(player);
            player?.PrintToCenter($"Spawns saved ({reloaded.Spawns.Count})");
            Plugin.Logger.LogInformation("Retake spawns saved to {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            Chat.To(player, $"{{red}}Saving spawns failed: {ex.Message}");
            Plugin.Logger.LogError("Saving retake spawns failed: {Message}", ex.Message);
            return false;
        }
    }

    private bool RequireEditor(CCSPlayerController? player, out CCSPlayerPawn pawn)
    {
        pawn = null!;
        if (player == null)
        {
            Chat.To(player, "This command must be used in-game.");
            return false;
        }

        if (!_editMode)
        {
            Chat.To(player, "Enter edit mode first with !retakeedit.");
            return false;
        }

        var p = player.PlayerPawn.Value;
        if (p?.AbsOrigin == null || !player.PawnIsAlive)
        {
            Chat.To(player, "You must be alive to edit spawns.");
            return false;
        }

        pawn = p;
        return true;
    }

    private void PrintSpawnCounts(CCSPlayerController? player)
    {
        Chat.To(player, _spawns.UsingDefaults
            ? $"{Server.MapName}: using the {{green}}built-in default spawns{{default}}."
            : $"{Server.MapName}: using {{green}}custom spawns{{default}}.");
        foreach (var site in Enum.GetValues<Bombsite>())
        {
            var t = _spawns.For(site, CsTeam.Terrorist).ToList();
            var ct = _spawns.For(site, CsTeam.CounterTerrorist).Count();
            Chat.To(player, $"Site {{green}}{site}{{default}}: {t.Count} T ({t.Count(s => s.IsPlanter)} planter), {ct} CT");
        }
    }

    private static bool TryParseTeam(string value, out CsTeam team)
    {
        team = value.ToLowerInvariant() switch
        {
            "t" => CsTeam.Terrorist,
            "ct" => CsTeam.CounterTerrorist,
            _ => CsTeam.None
        };
        return team != CsTeam.None;
    }
}
