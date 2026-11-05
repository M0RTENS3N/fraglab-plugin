using System.Drawing;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Retake;

/// <summary>A map event runs for one round only and is announced at the start of that round.</summary>
public enum RoundEvent
{
    None,
    Fog,
    MoonGravity,
    SlipperyIce,
    Turbo,
    QuadDamage
}

/// <summary>
/// Map events for fun retake. Each one changes a server setting (or the map's fog controller) for the round
/// and restores the exact previous values afterwards.
/// </summary>
public sealed partial class RetakeMode
{
    private static readonly Dictionary<RoundEvent, (string Name, string Description)> EventInfo = new()
    {
        [RoundEvent.Fog] = ("Thick fog", "enemies far away are hidden in the fog"),
        [RoundEvent.MoonGravity] = ("Moon gravity", "gravity is weak, jumps go very high"),
        [RoundEvent.SlipperyIce] = ("Slippery ice", "the ground has almost no friction"),
        [RoundEvent.Turbo] = ("Turbo", "everyone speeds up and stops much faster"),
        [RoundEvent.QuadDamage] = ("Quad damage", "all damage is doubled")
    };

    private RoundEvent _roundEvent;
    private readonly Dictionary<string, float> _convarBackup = new();
    private readonly List<FogBackup> _fogBackup = new();

    private readonly List<CubemapFogBackup> _cubemapFogBackup = new();
    private CEnvCubemapFog? _createdFog;

    private sealed record FogBackup(CFogController Fog, bool Enable, float Start, float End, float MaxDensity, Color Color);
    private sealed record CubemapFogBackup(CEnvCubemapFog Fog, bool Active, float Start, float End, float MaxOpacity, float Exponent);

    private void RegisterEvents()
    {
        AddCommand(["event"], "Show this round's map event", (p, _) => ShowEvent(p));
        Plugin.RegisterListener<Listeners.CheckTransmit>(HideEnemiesInFog);
    }

    /// <summary>
    /// The client draws players on top of the fog, so during the fog event enemies further away than
    /// FogEnd (and their weapons) are simply not sent to living players.
    /// </summary>
    private void HideEnemiesInFog(CCheckTransmitInfoList infoList)
    {
        if (!IsActive || _roundEvent != RoundEvent.Fog)
            return;

        var players = Utilities.GetPlayers().Where(p => p.PawnIsAlive && p.PlayerPawn.Value?.AbsOrigin != null).ToList();
        var maxDistance = PerkCfg.FogEnd;

        foreach (var (info, viewer) in infoList)
        {
            var eye = viewer?.PlayerPawn.Value?.AbsOrigin;
            if (viewer == null || !viewer.PawnIsAlive || eye == null)
                continue;

            foreach (var enemy in players)
            {
                if (enemy.Team == viewer.Team || enemy.Slot == viewer.Slot)
                    continue;
                var pawn = enemy.PlayerPawn.Value!;
                if (Distance(pawn.AbsOrigin!, eye) <= maxDistance)
                    continue;

                info.TransmitEntities.Remove(pawn);
                var weapons = pawn.WeaponServices?.MyWeapons;
                if (weapons == null)
                    continue;
                foreach (var weapon in weapons)
                {
                    if (weapon.Value is { IsValid: true } w)
                        info.TransmitEntities.Remove(w);
                }
            }
        }
    }

    private void ShowEvent(CCSPlayerController? player)
    {
        if (player == null)
            return;

        Chat.To(player, _roundEvent == RoundEvent.None
            ? "No map event this round."
            : $"Map event: {EventInfo[_roundEvent].Name} - {EventInfo[_roundEvent].Description}");
    }

    /// <summary>Text for the round start screen; empty when this round has no event.</summary>
    private string EventCenterText() =>
        _roundEvent == RoundEvent.None ? "" : $"\nMAP EVENT: {EventInfo[_roundEvent].Name} - {EventInfo[_roundEvent].Description}";

    /// <summary>Most rounds have no event; the others get one random event, announced in chat.</summary>
    private void RollRoundEvent()
    {
        if (Random.Shared.Next(100) >= PerkCfg.EventChance)
            return;

        var options = Enum.GetValues<RoundEvent>().Where(e => e != RoundEvent.None).ToArray();
        if (options.Length == 0)
            return;

        _roundEvent = options[Random.Shared.Next(options.Length)];
        switch (_roundEvent)
        {
            case RoundEvent.Fog:
                ApplyFog();
                break;
            case RoundEvent.MoonGravity:
                SetConVar("sv_gravity", PerkCfg.MoonGravity);
                break;
            case RoundEvent.SlipperyIce:
                SetConVar("sv_friction", PerkCfg.SlipperyFriction);
                break;
            case RoundEvent.Turbo:
                SetConVar("sv_accelerate", PerkCfg.TurboAccelerate);
                break;
        }

        var (name, description) = EventInfo[_roundEvent];
        Chat.All($"{{gold}}MAP EVENT THIS ROUND:{{default}} {name} - {description}. Type {{green}}!event{{default}} to see it again.");
    }

    /// <summary>Restores every setting the round's event changed. Runs at round end and when the mode is switched off.</summary>
    private void ResetRoundEvent()
    {
        foreach (var (name, value) in _convarBackup)
            ConVar.Find(name)?.SetValue(value);
        _convarBackup.Clear();

        foreach (var backup in _fogBackup)
        {
            if (!backup.Fog.IsValid)
                continue;
            backup.Fog.Fog.Enable = backup.Enable;
            backup.Fog.Fog.Start = backup.Start;
            backup.Fog.Fog.End = backup.End;
            backup.Fog.Fog.Maxdensity = backup.MaxDensity;
            backup.Fog.Fog.ColorPrimary = backup.Color;
            Utilities.SetStateChanged(backup.Fog, "CFogController", "m_fog");
        }
        _fogBackup.Clear();

        foreach (var backup in _cubemapFogBackup)
        {
            if (!backup.Fog.IsValid)
                continue;
            backup.Fog.Active = backup.Active;
            backup.Fog.StartDistance = backup.Start;
            backup.Fog.EndDistance = backup.End;
            backup.Fog.FogMaxOpacity = backup.MaxOpacity;
            backup.Fog.FogFalloffExponent = backup.Exponent;
            CubemapFogChanged(backup.Fog);
            backup.Fog.AcceptInput(backup.Active ? "Enable" : "Disable");
        }
        _cubemapFogBackup.Clear();

        if (_createdFog != null && _createdFog.IsValid)
            _createdFog.AcceptInput("Kill");
        _createdFog = null;

        if (_roundEvent != RoundEvent.None)
            Chat.All($"Map event over: {EventInfo[_roundEvent].Name}.");
        _roundEvent = RoundEvent.None;
    }

    /// <summary>Sets a convar for the round, remembering the value it had before.</summary>
    private void SetConVar(string name, float value)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null)
            return;

        if (!_convarBackup.ContainsKey(name))
            _convarBackup[name] = cvar.GetPrimitiveValue<float>();
        cvar.SetValue(value);
    }

    /// <summary>
    /// Makes the fog thick for the round. CS2 maps draw their fog with env_cubemap_fog (a map without one gets a new one);
    /// the old env_fog_controller is changed too. The previous settings are kept for the reset.
    /// </summary>
    private void ApplyFog()
    {
        var cubemapFogs = Utilities.FindAllEntitiesByDesignerName<CEnvCubemapFog>("env_cubemap_fog").Where(f => f.IsValid).ToList();
        if (cubemapFogs.Count == 0)
        {
            _createdFog = Utilities.CreateEntityByName<CEnvCubemapFog>("env_cubemap_fog");
            if (_createdFog != null)
            {
                SetCubemapFog(_createdFog);
                _createdFog.DispatchSpawn();
                _createdFog.AcceptInput("Enable");
            }
        }

        foreach (var fog in cubemapFogs)
        {
            _cubemapFogBackup.Add(new CubemapFogBackup(fog, fog.Active, fog.StartDistance, fog.EndDistance, fog.FogMaxOpacity, fog.FogFalloffExponent));
            SetCubemapFog(fog);
            CubemapFogChanged(fog);
            fog.AcceptInput("Enable");
        }

        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CFogController>("env_fog_controller"))
        {
            if (!fog.IsValid)
                continue;

            var values = fog.Fog;
            _fogBackup.Add(new FogBackup(fog, values.Enable, values.Start, values.End, values.Maxdensity, values.ColorPrimary));

            values.Enable = true;
            values.Start = PerkCfg.FogStart;
            values.End = PerkCfg.FogEnd;
            values.Maxdensity = 1f;
            values.ColorPrimary = Color.FromArgb(255, 70, 70, 80);
            Utilities.SetStateChanged(fog, "CFogController", "m_fog");
        }
    }

    private void SetCubemapFog(CEnvCubemapFog fog)
    {
        fog.Active = true;
        fog.StartDisabled = false;
        fog.StartDistance = PerkCfg.FogStart;
        fog.EndDistance = PerkCfg.FogEnd;
        fog.FogMaxOpacity = 1f;
        fog.FogFalloffExponent = 1f;
    }

    private static void CubemapFogChanged(CEnvCubemapFog fog)
    {
        foreach (var field in new[] { "m_bActive", "m_flStartDistance", "m_flEndDistance", "m_flFogMaxOpacity", "m_flFogFalloffExponent" })
            Utilities.SetStateChanged(fog, "CEnvCubemapFog", field);
    }
}
