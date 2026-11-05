using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Chaos;

public enum ChaosEvent
{
    LowGravity,
    SpeedRush,
    HeadshotsOnly,
    OneShot,
    Vampire,
    Ghosts,
    Bhop,
    Juggernauts,
    Explosive,
    Tiny
}

/// <summary>Chaos events: every ChaosInterval seconds one random rule change for ChaosDuration seconds.</summary>
public sealed partial class ChaosMode
{
    private static readonly Dictionary<ChaosEvent, (string Name, string Description)> ChaosInfo = new()
    {
        [ChaosEvent.LowGravity] = ("Low gravity", "jump to the moon"),
        [ChaosEvent.SpeedRush] = ("Speed rush", "everyone runs much faster"),
        [ChaosEvent.HeadshotsOnly] = ("Headshots only", "body shots do nothing"),
        [ChaosEvent.OneShot] = ("One shot", "every hit is deadly"),
        [ChaosEvent.Vampire] = ("Vampire", "damage you deal heals you"),
        [ChaosEvent.Ghosts] = ("Ghosts", "everyone is almost invisible"),
        [ChaosEvent.Bhop] = ("Bunny hop", "hold space to bhop"),
        [ChaosEvent.Juggernauts] = ("Juggernauts", "everyone has 300 HP"),
        [ChaosEvent.Explosive] = ("Explosive", "everyone explodes when they die"),
        [ChaosEvent.Tiny] = ("Tiny", "everyone is half size")
    };

    private ChaosEvent? _chaos;
    private float _chaosUntil;
    private float _nextChaosAt;
    private bool _chaosWarned;
    private readonly Dictionary<string, string> _chaosBackup = new();

    private string ChaosHud()
    {
        if (_chaos is { } chaos)
            return $"\nCHAOS: {ChaosInfo[chaos].Name} ({Math.Max(0, _chaosUntil - Server.CurrentTime):0}s)";
        var next = _nextChaosAt - Server.CurrentTime;
        return next <= 10 ? $"\nChaos in {Math.Max(0, next):0}..." : "";
    }

    private void TickChaos()
    {
        if (_gameOver)
            return;

        var now = Server.CurrentTime;
        if (_chaos != null)
        {
            if (now >= _chaosUntil)
                EndChaos(announce: true);
            return;
        }

        if (!_chaosWarned && _nextChaosAt - now <= 5f)
        {
            _chaosWarned = true;
            Chat.All("{red}Chaos in 5 seconds...");
        }

        if (now >= _nextChaosAt)
        {
            var options = Cfg.ChaosEvents
                .Select(n => Enum.TryParse<ChaosEvent>(n, true, out var e) ? e : (ChaosEvent?)null)
                .OfType<ChaosEvent>().Distinct().ToList();
            if (options.Count == 0)
            {
                _nextChaosAt = now + Cfg.ChaosInterval;
                return;
            }
            StartChaos(options[Random.Shared.Next(options.Count)]);
        }
    }

    private void StartChaos(ChaosEvent chaos)
    {
        if (_chaos != null)
            EndChaos(announce: false);

        _chaos = chaos;
        _chaosUntil = Server.CurrentTime + Cfg.ChaosDuration;

        switch (chaos)
        {
            case ChaosEvent.LowGravity:
                SetConVar("sv_gravity", "250");
                break;
            case ChaosEvent.Bhop:
                SetConVar("sv_autobunnyhopping", "1");
                SetConVar("sv_enablebunnyhopping", "1");
                break;
        }

        foreach (var player in Players.Playing().Where(p => p.PawnIsAlive))
        {
            if (player.PlayerPawn.Value is { } pawn)
                ApplyChaosOnSpawn(player, pawn);
        }

        var (name, description) = ChaosInfo[chaos];
        Chat.All($"{{red}}CHAOS:{{default}} {{gold}}{name}{{default}} - {description} for {Cfg.ChaosDuration:0} seconds!");
        foreach (var player in Players.Humans())
            player.PrintToCenter($"CHAOS: {name}!");
    }

    private void EndChaos(bool announce)
    {
        if (_chaos is not { } chaos)
            return;

        foreach (var (name, value) in _chaosBackup)
            Server.ExecuteCommand($"{name} {value}");
        _chaosBackup.Clear();

        foreach (var player in Players.Playing().Where(p => p.PawnIsAlive))
        {
            if (player.PlayerPawn.Value is not { } pawn)
                continue;
            switch (chaos)
            {
                case ChaosEvent.Ghosts:
                    SetAlpha(pawn, 255);
                    break;
                case ChaosEvent.Juggernauts:
                    SetHealth(pawn, Math.Min(pawn.Health, 100), 100);
                    break;
                case ChaosEvent.Tiny:
                    SetScale(pawn, 1f);
                    break;
            }
        }

        _chaos = null;
        _chaosWarned = false;
        _nextChaosAt = Server.CurrentTime + Cfg.ChaosInterval;
        if (announce)
            Chat.All($"Chaos over: {ChaosInfo[chaos].Name}. Next chaos in {Cfg.ChaosInterval:0} seconds.");
    }

    /// <summary>Events that change the player itself also apply to everyone who spawns while they run.</summary>
    private void ApplyChaosOnSpawn(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        switch (_chaos)
        {
            case ChaosEvent.Ghosts:
                SetAlpha(pawn, 45);
                break;
            case ChaosEvent.Juggernauts:
                SetHealth(pawn, 300, 300);
                break;
            case ChaosEvent.Tiny:
                SetScale(pawn, 0.5f);
                break;
        }
    }

    private void ChaosDamage(CTakeDamageInfo info)
    {
        var bullet = (info.BitsDamageType & DamageTypes_t.DMG_BULLET) != 0;
        switch (_chaos)
        {
            case ChaosEvent.HeadshotsOnly when bullet && info.GetHitGroup() != HitGroup_t.HITGROUP_HEAD:
                info.Damage = 0;
                break;
            case ChaosEvent.OneShot when info.Attacker.Value is { DesignerName: "player" }:
                info.Damage *= 10f;
                break;
        }
    }

    /// <summary>Explosive: hurts everyone close to the player who died.</summary>
    private void Explode(CCSPlayerController victim)
    {
        var origin = victim.PlayerPawn.Value?.AbsOrigin;
        if (origin == null)
            return;

        // Applied next frame: killing players inside the death event would nest death events.
        var center = new Vector(origin.X, origin.Y, origin.Z);
        var slot = victim.Slot;
        var name = victim.PlayerName;
        Server.NextFrame(() => ExplodeAt(center, slot, name));
    }

    private void ExplodeAt(Vector center, int victimSlot, string victimName)
    {
        if (!IsActive || _gameOver)
            return;

        const float radius = 300f;
        const int maxDamage = 120;
        var hit = 0;

        foreach (var player in Players.Playing().Where(p => p.PawnIsAlive && p.Slot != victimSlot))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn?.AbsOrigin == null || IsProtected(player.Slot))
                continue;

            var dx = pawn.AbsOrigin.X - center.X;
            var dy = pawn.AbsOrigin.Y - center.Y;
            var dz = pawn.AbsOrigin.Z - center.Z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > radius)
                continue;

            var damage = (int)(maxDamage * (1f - distance / radius));
            if (damage <= 0)
                continue;

            hit++;
            if (pawn.Health <= damage)
                pawn.CommitSuicide(false, true);
            else
                SetHealth(pawn, pawn.Health - damage, pawn.MaxHealth);
        }

        if (hit > 0)
            Chat.All($"{{red}}KABOOM!{{default}} {victimName} exploded and hit {hit}.");
    }

    private void SetConVar(string name, string value)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null)
            return;
        if (!_chaosBackup.ContainsKey(name))
            _chaosBackup[name] = CurrentValue(cvar);
        Server.ExecuteCommand($"{name} {value}");
    }

    /// <summary>The convar's value as console text; bool convars can't be read as float.</summary>
    private static string CurrentValue(ConVar cvar)
    {
        try
        {
            return cvar.GetPrimitiveValue<float>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return cvar.GetPrimitiveValue<bool>() ? "1" : "0";
        }
    }

    private void OnChaosNow(CCSPlayerController? player, CommandInfo info)
    {
        ChaosEvent chaos;
        if (info.ArgCount >= 2)
        {
            if (!Enum.TryParse(info.GetArg(1), true, out chaos))
            {
                Chat.To(player, $"Unknown chaos event. Options: {string.Join(", ", Enum.GetNames<ChaosEvent>())}");
                return;
            }
        }
        else
        {
            var all = Enum.GetValues<ChaosEvent>();
            chaos = all[Random.Shared.Next(all.Length)];
        }
        StartChaos(chaos);
    }

    private void ShowChaos(CCSPlayerController? player)
    {
        if (_chaos is { } chaos)
            Chat.To(player, $"Chaos: {{gold}}{ChaosInfo[chaos].Name}{{default}} - {ChaosInfo[chaos].Description} ({_chaosUntil - Server.CurrentTime:0}s left).");
        else
            Chat.To(player, $"No chaos right now. Next one in {Math.Max(0, _nextChaosAt - Server.CurrentTime):0} seconds.");
    }

    private static void SetAlpha(CCSPlayerPawn pawn, int alpha)
    {
        pawn.RenderMode = alpha < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
        pawn.Render = Color.FromArgb(alpha, pawn.Render.R, pawn.Render.G, pawn.Render.B);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_nRenderMode");
    }

    private static void SetScale(CCSPlayerPawn pawn, float scale)
    {
        if (pawn.CBodyComponent?.SceneNode is not { } node)
            return;
        node.Scale = scale;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
    }
}
