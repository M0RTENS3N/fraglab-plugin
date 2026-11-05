using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using FragLab.Core.Menus;
using NumVec = System.Numerics.Vector3;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Practice;

/// <summary>
/// Ghost replays: the movement before each recorded throw is kept (about 2.5 s, sampled at 32 Hz). Replaying a lineup
/// spawns a ghost bot, a real player pawn so the walk and run animations play, and moves it along the recorded path
/// with the matching velocity. When it reaches the spot, the grenade's predicted flight is drawn with beams.
/// Also the lineup detail menu (go there, replay, delete).
/// </summary>
public sealed partial class PracticeMode
{
    private const float SampleSeconds = 2.5f;
    private const float GhostStep = 1f / 32f;
    private const float ViewerBackOffset = 120f;
    private const float TrajectoryShowSeconds = 6f;
    private const float GhostSpawnTimeout = 4f;
    private const int WaitingForBot = -1;
    private const int GhostGone = -2;

    private readonly Dictionary<int, List<(float Time, PathFrame Frame)>> _history = new();

    private sealed class Ghost
    {
        public required int Viewer;
        public required NadeLineup Lineup;
        public required HashSet<int> BotsBefore;
        public float StartedAt;
        public int BotSlot = WaitingForBot; // WaitingForBot until the bot joins, GhostGone once it threw
        public string BotName = "";
        public int Index;
        public Timer? Timer;
        public readonly List<CBeam> Beams = new();
    }

    private Ghost? _ghost;

    private void RegisterGhost() => Plugin.RegisterListener<Listeners.CheckTransmit>(HideGhostFromOthers);

    // ---------------------------------------------------------------- recording

    /// <summary>Called every 1/32 s while practice is on: keeps only the last few seconds.</summary>
    private void RecordSample(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (!player.PawnIsAlive || pawn?.AbsOrigin == null)
            return;

        var now = Server.CurrentTime;
        if (!_history.TryGetValue(player.Slot, out var list))
            _history[player.Slot] = list = new List<(float, PathFrame)>();

        var origin = pawn.AbsOrigin;
        var eye = pawn.EyeAngles;
        list.Add((now, new PathFrame { X = origin.X, Y = origin.Y, Z = origin.Z, Pitch = eye.X, Yaw = eye.Y }));
        list.RemoveAll(s => now - s.Time > SampleSeconds + 0.5f);
    }

    private List<PathFrame> RecentPath(int slot)
    {
        if (!_history.TryGetValue(slot, out var list))
            return [];
        var now = Server.CurrentTime;
        return list.Where(s => now - s.Time <= SampleSeconds).Select(s => s.Frame).ToList();
    }

    // ---------------------------------------------------------------- ghost

    public void StartGhost(CCSPlayerController viewer, NadeLineup lineup)
    {
        StopGhost();
        if (lineup.Path.Count < 2)
        {
            Chat.To(viewer, "This lineup has no recorded movement. Save it again and throw it once.");
            return;
        }

        // Stand a bit behind the start of the path, looking at it, so the replay can be watched.
        var start = lineup.Path[0];
        Plugin.Menus.Close(viewer);
        if (WatchSpot(viewer, start) is { } spot)
        {
            SetNoclip(viewer, false);
            Teleport(viewer, spot, new QAngle(0, start.Yaw, 0));
        }
        else
        {
            Chat.To(viewer, "No safe spot to watch from next to the ghost, so you stay where you are.");
        }

        // Practice bots stand still (bot_stop), so the ghost bot only moves when we move it.
        var ghost = new Ghost
        {
            Viewer = viewer.Slot,
            Lineup = lineup,
            BotsBefore = Players.Connected().Select(p => p.Slot).ToHashSet(),
            StartedAt = Server.CurrentTime
        };
        _ghost = ghost;
        Server.ExecuteCommand("bot_add t");
        ghost.Timer = Plugin.AddTimer(GhostStep, () => StepGhost(ghost), TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        Chat.To(viewer, $"Ghost replay of {{green}}{lineup.Name}{{default}}. The ghost throws when it reaches the spot.");
    }

    /// <summary>Runs every 1/32 s: waits for the ghost bot, then moves it one recorded frame per tick.</summary>
    private void StepGhost(Ghost ghost)
    {
        var viewer = Utilities.GetPlayerFromSlot(ghost.Viewer);
        if (!IsActive || _ghost != ghost || viewer == null || !Players.IsValid(viewer))
        {
            StopGhost();
            return;
        }

        if (ghost.BotSlot == WaitingForBot)
        {
            var bot = Players.Connected().FirstOrDefault(p =>
                p.IsBot && p.Team == CsTeam.Terrorist && p.PawnIsAlive && !ghost.BotsBefore.Contains(p.Slot));
            if (bot == null)
            {
                if (Server.CurrentTime - ghost.StartedAt > GhostSpawnTimeout)
                {
                    Chat.To(viewer, "{red}The ghost bot didn't spawn.{default} Try the replay again.");
                    StopGhost();
                }
                return;
            }

            ClaimGhostBot(ghost, bot);
        }

        var body = Utilities.GetPlayerFromSlot(ghost.BotSlot)?.PlayerPawn.Value;
        if (body == null || !Utilities.GetPlayerFromSlot(ghost.BotSlot)!.PawnIsAlive)
        {
            StopGhost();
            return;
        }

        var path = ghost.Lineup.Path;
        if (ghost.Index < path.Count)
        {
            var frame = path[ghost.Index];
            // Velocity matches the step, so the bot plays its walk or run animation.
            var velocity = ghost.Index == 0 ? new Vector(0, 0, 0) : Velocity(path[ghost.Index - 1], frame);
            body.Teleport(new Vector(frame.X, frame.Y, frame.Z), new QAngle(0, frame.Yaw, 0), velocity);
            ghost.Index++;
            return;
        }

        // The ghost has thrown: it vanishes (left alone, the bot would turn back to its own aim) and the grenade's
        // flight stays drawn for a few seconds.
        ghost.Timer?.Kill();
        ShowTrajectory(ghost, viewer);
        KickGhostBot(ghost);
        ghost.Timer = Plugin.AddTimer(TrajectoryShowSeconds, StopGhost, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void KickGhostBot(Ghost ghost)
    {
        if (ghost.BotSlot >= 0 && ghost.BotName.Length > 0)
            Server.ExecuteCommand($"kick \"{ghost.BotName}\"");
        ghost.BotName = "";
        ghost.BotSlot = GhostGone;
    }

    /// <summary>
    /// A spot near the start of the path to watch from: behind it first, then closer, then to the sides. A spot only
    /// counts when there's floor at about the same height and no wall in between, so you can't end up outside the map.
    /// </summary>
    private static Vector? WatchSpot(CCSPlayerController viewer, PathFrame start)
    {
        var pawn = viewer.PlayerPawn.Value;
        if (pawn == null)
            return null;

        var simulator = new NadeSimulator(pawn);
        var yaw = start.Yaw * MathF.PI / 180f;
        var forward = new NumVec(MathF.Cos(yaw), MathF.Sin(yaw), 0);
        var right = new NumVec(MathF.Sin(yaw), -MathF.Cos(yaw), 0);
        var from = new NumVec(start.X, start.Y, start.Z + 32f);

        NumVec[] offsets =
        [
            -forward * ViewerBackOffset, -forward * 80f, -forward * 50f,
            right * 80f, -right * 80f, -forward * 60f + right * 60f, -forward * 60f - right * 60f
        ];

        foreach (var offset in offsets)
        {
            var candidate = from + offset;
            if (!simulator.IsClear(from, candidate))
                continue;
            var floor = simulator.FloorBelow(candidate);
            if (floor == candidate || MathF.Abs(floor.Z - start.Z) > 24f)
                continue;
            return new Vector(floor.X, floor.Y, floor.Z + 2f);
        }
        return null;
    }

    /// <summary>
    /// The new bot is the ghost: practice must not place or kick it, it takes no damage, and nobody bumps into it
    /// (so it can't get stuck in you when you watch from a corner).
    /// </summary>
    private static void ClaimGhostBot(Ghost ghost, CCSPlayerController bot)
    {
        ghost.BotSlot = bot.Slot;
        ghost.BotName = bot.PlayerName;
        if (bot.PlayerPawn.Value is not { } pawn)
            return;
        pawn.TakesDamage = false;
        const byte passable = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING;
        SetCollision(pawn, passable, passable);
    }

    /// <summary>Speed needed to get from one recorded frame to the next in one step.</summary>
    private static Vector Velocity(PathFrame from, PathFrame to) => new(
        (to.X - from.X) / GhostStep,
        (to.Y - from.Y) / GhostStep,
        (to.Z - from.Z) / GhostStep);

    private void ShowTrajectory(Ghost ghost, CCSPlayerController viewer)
    {
        var last = ghost.Lineup.Path[^1];
        var pawn = viewer.PlayerPawn.Value;
        if (pawn == null)
            return;

        var simulator = new NadeSimulator(pawn);
        var eye = new NumVec(last.X, last.Y, last.Z + EyeHeight);
        var (position, velocity) = simulator.ThrowStart(eye, last.Pitch, last.Yaw, ThrowTypeOf(ghost.Lineup));
        var points = new List<NumVec>();
        var landing = simulator.Fly(position, velocity, NadeKindOf(ghost.Lineup.Grenade), points);

        var color = Color.FromArgb(255, 255, 200, 60);
        for (var i = 0; i + 4 < points.Count; i += 4)
            AddBeam(ghost, points[i], points[i + 4], color, 1.5f);
        if (points.Count > 0)
            AddBeam(ghost, points[^1], points[^1] + new NumVec(0, 0, 60f), Color.FromArgb(255, 255, 60, 60), 2f);

        Chat.To(viewer, $"Predicted landing: {{green}}{landing.X:0}, {landing.Y:0}, {landing.Z:0}{{default}}.");
    }

    private void AddBeam(Ghost ghost, NumVec from, NumVec to, Color color, float width)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null)
            return;
        beam.Render = color;
        beam.Width = width;
        beam.EndWidth = width;
        beam.Teleport(new Vector(from.X, from.Y, from.Z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
        beam.EndPos.X = to.X;
        beam.EndPos.Y = to.Y;
        beam.EndPos.Z = to.Z;
        beam.DispatchSpawn();
        ghost.Beams.Add(beam);
    }

    private void StopGhost()
    {
        if (_ghost is not { } ghost)
            return;

        ghost.Timer?.Kill();
        KickGhostBot(ghost);

        foreach (var beam in ghost.Beams.Where(b => b.IsValid))
            beam.Remove();
        _ghost = null;
    }

    /// <summary>The ghost bot is only visible to the player who started the replay.</summary>
    private void HideGhostFromOthers(CCheckTransmitInfoList infoList)
    {
        if (!IsActive || _ghost is not { } ghost || ghost.BotSlot < 0)
            return;
        var body = Utilities.GetPlayerFromSlot(ghost.BotSlot)?.PlayerPawn.Value;
        if (body == null)
            return;

        foreach (var (info, viewer) in infoList)
        {
            if (viewer == null || viewer.Slot != ghost.Viewer)
                info.TransmitEntities.Remove(body);
        }
    }

    private static NadeKind NadeKindOf(string grenade) => Normalize(grenade) switch
    {
        "flashbang" => NadeKind.Flash,
        "hegrenade" => NadeKind.HE,
        "molotov" => NadeKind.Molotov,
        _ => NadeKind.Smoke // smokes and decoys fly the same way
    };

    /// <summary>The throw the lineup was recorded with: the click picks the strength, a jump in the name makes it a jump-throw.</summary>
    private static ThrowType ThrowTypeOf(NadeLineup lineup)
    {
        var jump = lineup.Throw.Contains("jump", StringComparison.OrdinalIgnoreCase);
        return (lineup.Strength, jump) switch
        {
            ("medium", true) => ThrowType.JumpMiddle,
            ("medium", false) => ThrowType.Middle,
            ("short", false) => ThrowType.Right,
            (_, true) => ThrowType.JumpLeft,
            _ => ThrowType.Left
        };
    }

    // ---------------------------------------------------------------- lineup actions

    private void OpenLineupDetail(CCSPlayerController player, NadeLineup lineup)
    {
        var admin = Players.IsAdmin(player, Plugin.Config.AdminFlag);
        Plugin.Menus.Open(player, new Menu(lineup.Name, menu =>
        {
            menu.Add("Go there", p => GoToLineup(p, lineup));
            if (lineup.Path.Count >= 2)
                menu.Add("Replay ghost", p => StartGhost(p, lineup));
            else
                menu.Add("Replay ghost (no recording yet)");
            if (admin)
                menu.Add("Delete lineup", p => DeleteLineup(p, lineup), "#eb4b4b");
        }));
    }

    private void DeleteLineup(CCSPlayerController player, NadeLineup lineup)
    {
        if (!Players.IsAdmin(player, Plugin.Config.AdminFlag))
        {
            Chat.To(player, "You don't have permission to use this command.");
            return;
        }

        Plugin.Menus.Close(player);
        if (!_lineups.Lineups.Remove(lineup))
            return;
        if (TrySaveLineups(player))
            Chat.To(player, $"Deleted lineup {{green}}{lineup.Name}{{default}}.");
        OpenLineupMenu(player);
    }
}
