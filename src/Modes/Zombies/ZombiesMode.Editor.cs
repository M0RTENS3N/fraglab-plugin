using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using FragLab.Core.Menus;

namespace FragLab.Modes.Zombies;

/// <summary>
/// Zombies edit mode (!zedit): no waves and no zombies, players can't get hurt, every door is shown and nothing
/// blocks you. A menu (you can walk with it open) places doors corner by corner, deletes them and toggles noclip.
/// !zedit again starts a new game.
/// </summary>
public sealed partial class ZombiesMode
{
    private static readonly int[] DoorPrices = [500, 750, 1000, 1250, 1500, 2000];

    private bool _editMode;

    private void RegisterEditor()
    {
        AddCommand(["zedit"], "Zombies edit mode: place doors with a menu (no waves while editing)", (p, _) => ToggleEditMode(p), adminOnly: true);
        AddCommand(["zem"], "Open the zombies edit menu again", (p, _) =>
        {
            if (p != null && _editMode)
                OpenEditMenu(p);
        }, adminOnly: true);
    }

    private void ToggleEditMode(CCSPlayerController? player)
    {
        if (_editMode)
        {
            ExitEditMode();
            return;
        }

        _editMode = true;
        _phaseTimer?.Kill();
        _phaseTimer = null;
        _waveActive = false;
        Server.ExecuteCommand("bot_quota 0; bot_kick");
        ResetDoors(); // every door closed and drawn

        Chat.All("{gold}Zombies edit mode{default}: no waves. Place doors with the menu ({green}!zem{default} opens it again), " +
                 "{green}!zedit{default} again to play.");
        if (player != null)
            OpenEditMenu(player);
    }

    private void ExitEditMode()
    {
        _editMode = false;
        foreach (var human in Humans())
        {
            Players.LeaveNoclipSafely(human);
            if (human.PlayerPawn.Value is { } pawn)
                pawn.TakesDamage = true;
        }
        Chat.All("Edit mode off. Starting a new game.");
        RestartGame();
    }

    /// <summary>Runs instead of the wave logic while editing.</summary>
    private void EditorTick()
    {
        foreach (var human in Humans())
        {
            if (!human.PawnIsAlive)
            {
                human.Respawn();
                continue;
            }
            if (human.PlayerPawn.Value is { } pawn)
                pawn.TakesDamage = false;

            if (Plugin.Menus.IsOpen(human))
                continue;
            var corner = _doorCorner.ContainsKey(human.Slot) ? "corner 1 set" : "no corner set";
            var nearest = human.PlayerPawn.Value?.AbsOrigin is { } origin
                ? _doors.OrderBy(d => DistanceToDoor(d, origin)).FirstOrDefault(d => DistanceToDoor(d, origin) < 300f)
                : null;
            human.PrintToCenter($"EDIT MODE  |  {_doors.Count} doors  |  {corner}" +
                                (nearest != null ? $"\nNearest: {nearest.Name} ({nearest.Price})" : "") + "\n!zem: edit menu");
        }
    }

    private void OpenEditMenu(CCSPlayerController player)
    {
        Plugin.Menus.Open(player, new Menu("Zombies edit", menu =>
        {
            var hasCorner = _doorCorner.ContainsKey(player.Slot);
            menu.Add(hasCorner ? "Corner 1: set again here" : "Corner 1 here (one side of the doorway)", SetDoorCorner);
            foreach (var price in DoorPrices)
                menu.Add($"Corner 2 here: door for {price}", p => FinishDoor(p, price, $"Door {_doors.Count + 1}"),
                    hasCorner ? null : "#888888");
            menu.Add("Delete the nearest door", DeleteNearestDoor, "#eb4b4b");
            menu.Add("List doors", p => ListDoors(p));
            var noclip = player.PlayerPawn.Value?.MoveType == MoveType_t.MOVETYPE_NOCLIP;
            menu.Add(noclip ? "Noclip: on" : "Noclip: off", ToggleEditNoclip);
            menu.Add("Exit edit mode (start a game)", p =>
            {
                Plugin.Menus.Close(p);
                ExitEditMode();
            });
        }) { AllowMovement = true });
    }

    private static void ToggleEditNoclip(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return;

        if (pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP)
        {
            Players.ForceWalk(player);
            return;
        }
        pawn.MoveType = MoveType_t.MOVETYPE_NOCLIP;
        pawn.ActualMoveType = MoveType_t.MOVETYPE_NOCLIP;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    // ---------------------------------------------------------------- door editing (menu and !zdoor)

    private void SetDoorCorner(CCSPlayerController player)
    {
        if (player.PlayerPawn.Value?.AbsOrigin is not { } origin)
            return;
        _doorCorner[player.Slot] = new Vector(origin.X, origin.Y, origin.Z);
        Chat.To(player, "Corner 1 set. Walk to the other side of the doorway and set corner 2.");
    }

    private void FinishDoor(CCSPlayerController player, int price, string name)
    {
        if (player.PlayerPawn.Value?.AbsOrigin is not { } origin)
            return;
        if (!_doorCorner.TryGetValue(player.Slot, out var a))
        {
            Chat.To(player, "Set corner 1 first, on one side of the doorway.");
            return;
        }

        var door = MakeDoor(a, new Vector(origin.X, origin.Y, origin.Z), price, name);
        _doors.Add(door);
        _doorCorner.Remove(player.Slot);
        SaveDoors();
        DrawDoor(door);
        Chat.To(player, $"Door {{green}}{name}{{default}} ({price}) saved for {Server.MapName}.");
    }

    private void DeleteNearestDoor(CCSPlayerController player)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        var nearest = origin == null ? null : _doors.OrderBy(d => DistanceToDoor(d, origin)).FirstOrDefault();
        if (nearest == null || DistanceToDoor(nearest, origin!) > 300f)
        {
            Chat.To(player, "No door within 300 units.");
            return;
        }

        _doors.Remove(nearest);
        _openDoors.Remove(nearest);
        RemoveDoorBeams(nearest);
        SaveDoors();
        Chat.To(player, $"Deleted door {{green}}{nearest.Name}{{default}}.");
    }

    private void ListDoors(CCSPlayerController? player)
    {
        if (_doors.Count == 0)
            Chat.To(player, $"No doors on {Server.MapName} yet.");
        foreach (var d in _doors)
            Chat.To(player, $"{d.Name} - {d.Price}{(_openDoors.Contains(d) ? " (open)" : "")}");
    }
}
