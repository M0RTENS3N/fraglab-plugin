using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Practice;

/// <summary>
/// Lineup markers: the lineup's grenade floating above its spot, glowing through walls in the grenade's color, so you can see where
/// the lineups are. !markers hides or shows them. Also !t, !ct and !spec change your side in practice.
/// </summary>
public sealed partial class PracticeMode
{
    private const float MarkerHeight = 40f;
    private const float MarkerScale = 2f;

    /// <summary>World model and glow color per grenade (models as named in the game files).</summary>
    private static readonly Dictionary<string, (string Model, Color Glow)> MarkerStyles = new()
    {
        ["weapon_smokegrenade"] = ("weapons/models/grenade/smokegrenade/weapon_smokegrenade.vmdl", Color.FromArgb(255, 200, 230, 255)),
        ["weapon_flashbang"] = ("weapons/models/grenade/flashbang/weapon_flashbang.vmdl", Color.FromArgb(255, 255, 240, 120)),
        ["weapon_hegrenade"] = ("weapons/models/grenade/hegrenade/weapon_hegrenade.vmdl", Color.FromArgb(255, 255, 70, 70)),
        ["weapon_molotov"] = ("weapons/models/grenade/molotov/weapon_molotov.vmdl", Color.FromArgb(255, 255, 140, 0)),
        ["weapon_incgrenade"] = ("weapons/models/grenade/incendiary/weapon_incendiarygrenade.vmdl", Color.FromArgb(255, 255, 90, 20)),
        ["weapon_decoy"] = ("weapons/models/grenade/decoy/weapon_decoy.vmdl", Color.FromArgb(255, 170, 170, 170))
    };

    private readonly List<CBaseModelEntity> _markers = new();
    private bool _markersOn = true;

    private void RegisterMarkers()
    {
        Plugin.RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            foreach (var (model, _) in MarkerStyles.Values)
                manifest.AddResource(model);
        });

        AddCommand(["markers"], "Show or hide the lineup markers (all grenade types) on the map", (p, _) => WithPractice(p, ToggleMarkers));
        AddCommand(["t"], "Switch to the Terrorist side (practice)", (p, _) => WithPractice(p, q => ChangeSide(q, CsTeam.Terrorist)));
        AddCommand(["ct"], "Switch to the Counter-Terrorist side (practice)", (p, _) => WithPractice(p, q => ChangeSide(q, CsTeam.CounterTerrorist)));
        AddCommand(["spec"], "Go to spectator (practice)", (p, _) => WithPractice(p, q => ChangeSide(q, CsTeam.Spectator)));
    }

    private void ToggleMarkers(CCSPlayerController player)
    {
        _markersOn = !_markersOn;
        RefreshMarkers();
        Chat.To(player, _markersOn ? "Lineup markers {green}shown{default}." : "Lineup markers {red}hidden{default}.");
    }

    /// <summary>Rebuilds the markers from the saved lineups (called after saving, deleting, and when a round starts).</summary>
    private void RefreshMarkers()
    {
        RemoveMarkers();
        if (!IsActive || !_markersOn)
            return;

        foreach (var lineup in _lineups.Lineups)
        {
            if (!MarkerStyles.TryGetValue(lineup.Grenade, out var style))
                continue;

            // Spawn first, then set the model (a weapon model only draws on a prop_dynamic_override set up this way),
            // non-solid so nobody bumps into it, and the glow switched on explicitly.
            var marker = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
            if (marker == null || !marker.IsValid)
                continue;

            marker.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DEBRIS;
            marker.Collision.SolidType = SolidType_t.SOLID_NONE;
            marker.Teleport(new Vector(lineup.X, lineup.Y, lineup.Z + MarkerHeight), new QAngle(0, 45, 0), new Vector(0, 0, 0));
            marker.DispatchSpawn();
            marker.SetModel(style.Model);
            if (marker.CBodyComponent?.SceneNode is { } node)
                node.GetSkeletonInstance().Scale = MarkerScale;
            Utilities.SetStateChanged(marker, "CBaseEntity", "m_CBodyComponent");
            Utilities.SetStateChanged(marker, "CCollisionProperty", "m_CollisionGroup");
            Utilities.SetStateChanged(marker, "CCollisionProperty", "m_nSolidType");

            marker.Glow.GlowColorOverride = style.Glow;
            marker.Glow.GlowRange = 3000;
            marker.Glow.GlowType = 3;
            marker.Glow.Glowing = true;
            Utilities.SetStateChanged(marker, "CBaseModelEntity", "m_Glow");
            _markers.Add(marker);
            _markerSpots.Add((lineup, new System.Numerics.Vector3(lineup.X, lineup.Y, lineup.Z + MarkerHeight)));
        }
    }

    private void RemoveMarkers()
    {
        foreach (var marker in _markers.Where(m => m.IsValid))
            marker.Remove();
        _markers.Clear();
        _markerSpots.Clear();
    }

    /// <summary>Moves you to another side; alive players respawn there with the normal practice loadout.</summary>
    private void ChangeSide(CCSPlayerController player, CsTeam team)
    {
        if (player.Team == team)
        {
            Chat.To(player, "You're already on that side.");
            return;
        }

        player.ChangeTeam(team);
        if (team == CsTeam.Spectator)
            return;

        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var moved = Utilities.GetPlayerFromSlot(slot);
            if (Players.IsValid(moved))
                moved!.Respawn();
        });
    }
}
