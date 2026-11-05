using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace FragLab.Modes.Chaos;

/// <summary>The leader glows gold through walls: a hidden copy follows their animation, a glowing copy follows that.</summary>
public sealed partial class ChaosMode
{
    private (CBaseModelEntity Glow, CBaseModelEntity Relay)? _leaderGlow;

    private void CreateLeaderGlow(CCSPlayerController leader)
    {
        RemoveLeaderGlow();
        if (!Cfg.LeaderGlow || _gameOver)
            return;

        var pawn = leader.PlayerPawn.Value;
        var model = pawn?.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        if (pawn == null || string.IsNullOrEmpty(model))
            return;

        var relay = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        var glow = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (relay == null || glow == null)
            return;

        relay.SetModel(model);
        relay.Spawnflags = 256u;
        relay.RenderMode = RenderMode_t.kRenderNone;
        relay.DispatchSpawn();

        glow.SetModel(model);
        glow.Spawnflags = 256u;
        glow.DispatchSpawn();
        glow.Glow.GlowColorOverride = Color.FromArgb(255, 255, 200, 0);
        glow.Glow.GlowRange = 5000;
        glow.Glow.GlowRangeMin = 20;
        glow.Glow.GlowTeam = -1;
        glow.Glow.GlowType = 3;

        relay.AcceptInput("FollowEntity", pawn, relay, "!activator");
        glow.AcceptInput("FollowEntity", relay, glow, "!activator");

        _leaderGlow = (glow, relay);
    }

    private void RemoveLeaderGlow()
    {
        if (_leaderGlow is not { } glow)
            return;
        if (glow.Glow.IsValid)
            glow.Glow.AcceptInput("Kill");
        if (glow.Relay.IsValid)
            glow.Relay.AcceptInput("Kill");
        _leaderGlow = null;
    }

    /// <summary>The leader doesn't see their own glow (it would cover their screen in third person / spectating).</summary>
    private void HideLeaderGlowFromLeader(CCheckTransmitInfoList infoList)
    {
        if (!IsActive || _leaderGlow is not { } glow)
            return;

        foreach (var (info, viewer) in infoList)
        {
            if (viewer == null || viewer.Slot != _leaderSlot)
                continue;
            if (glow.Glow.IsValid)
                info.TransmitEntities.Remove(glow.Glow);
            if (glow.Relay.IsValid)
                info.TransmitEntities.Remove(glow.Relay);
        }
    }
}
