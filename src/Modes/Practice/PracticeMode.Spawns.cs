using System.Drawing;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;
using NumVec = System.Numerics.Vector3;

namespace FragLab.Modes.Practice;

/// <summary>
/// Spawn squares: a small square on the ground at every spawn that official matches use (both teams). !spawnmarks
/// shows or hides them. An admin looks at a square and presses E to switch that spawn off or on; switched-off spawns
/// are saved per map and skipped by !spawn.
/// </summary>
public sealed partial class PracticeMode
{
    private const float SpawnSquareSize = 48f;
    private const float SpawnLookRange = 1500f;
    private const float SpawnLookCone = 0.08f;

    private readonly HashSet<string> _disabledSpawns = new();
    private readonly List<CBeam> _spawnBeams = new();
    private readonly HashSet<int> _spawnUseHeld = new();
    private bool _spawnMarksOn;

    private void RegisterSpawnSquares()
    {
        AddCommand(["spawnmarks", "spawnsquares"], "Spawn squares: !spawnmarks, or !spawnmarks off / on to switch all of them",
            (p, info) => WithPractice(p, q => OnSpawnMarks(q, info)));
        AddCommand(["spawndump"], "Save every spawn of this map (team, position, priority) to a file", (p, _) => WithPractice(p, DumpSpawns), adminOnly: true);
    }

    private string SpawnFile(string map) => Path.Combine(Plugin.ModuleDirectory, "spawn-toggles", $"{map}.json");

    /// <summary>Every spawn official matches use: both teams' competitive spawns on the map.</summary>
    private static List<SpawnPoint> OfficialSpawns() =>
        Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
            .Concat(Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_counterterrorist"))
            .Where(s => s.IsValid && s.AbsOrigin != null)
            .ToList();

    private static string SpawnKey(Vector origin) => $"{origin.X:0.#},{origin.Y:0.#},{origin.Z:0.#}";

    private bool SpawnEnabled(SpawnPoint spawn) =>
        spawn.AbsOrigin != null && !_disabledSpawns.Contains(SpawnKey(spawn.AbsOrigin));

    private void LoadSpawnToggles()
    {
        _disabledSpawns.Clear();
        var file = SpawnFile(Server.MapName);
        if (!File.Exists(file))
            return;
        try
        {
            foreach (var key in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [])
                _disabledSpawns.Add(key);
        }
        catch (JsonException)
        {
            // A broken file means every spawn is on again; the next toggle rewrites it.
        }
    }

    private void SaveSpawnToggles()
    {
        var file = SpawnFile(Server.MapName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(_disabledSpawns.OrderBy(k => k).ToList()));
    }

    /// <summary>Writes every spawn point of the map (team, position, priority, index) to spawn-dumps/&lt;map&gt;.json.</summary>
    private void DumpSpawns(CCSPlayerController player)
    {
        var spawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
            .Select(s => (Spawn: s, Team: "T"))
            .Concat(Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_counterterrorist").Select(s => (Spawn: s, Team: "CT")))
            .Where(s => s.Spawn.IsValid && s.Spawn.AbsOrigin != null)
            .Select(s => new
            {
                s.Team,
                s.Spawn.Index,
                Priority = s.Spawn.Priority,
                Enabled = s.Spawn.Enabled,
                X = s.Spawn.AbsOrigin!.X,
                Y = s.Spawn.AbsOrigin.Y,
                Z = s.Spawn.AbsOrigin.Z,
                Yaw = s.Spawn.AbsRotation?.Y ?? 0f
            })
            .OrderBy(s => s.Team).ThenBy(s => s.Priority).ThenBy(s => s.Index)
            .ToList();

        var file = Path.Combine(Plugin.ModuleDirectory, "spawn-dumps", $"{Server.MapName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(spawns, new JsonSerializerOptions { WriteIndented = true }));
        Chat.To(player, $"Saved {spawns.Count} spawns for {Server.MapName} to spawn-dumps/{Server.MapName}.json. Send me that file.");
    }

    /// <summary>
    /// !spawnmarks toggles the squares. "off" switches every spawn off and "on" every spawn on, so an admin can
    /// switch the official ones on with E (or the other way round) instead of one by one.
    /// </summary>
    private void OnSpawnMarks(CCSPlayerController player, CommandInfo info)
    {
        var arg = info.ArgCount > 1 ? info.GetArg(1).ToLowerInvariant() : "";
        if (arg is "off" or "on")
        {
            _spawnMarksOn = true;
            _disabledSpawns.Clear();
            if (arg == "off")
                foreach (var spawn in OfficialSpawns())
                    _disabledSpawns.Add(SpawnKey(spawn.AbsOrigin!));
            SaveSpawnToggles();
            DrawSpawnMarks();
            Chat.To(player, arg == "off"
                ? "All spawns {red}switched off{default}. Admins: look at the official ones and press {green}E{default} to switch them on."
                : "All spawns {green}switched on{default}.");
            return;
        }

        _spawnMarksOn = !_spawnMarksOn;
        DrawSpawnMarks();
        Chat.To(player, _spawnMarksOn
            ? "Spawn squares {green}shown{default}. Gold = T spawn, blue = CT spawn, red = switched off, grey = the map has it disabled."
            : "Spawn squares {red}hidden{default}.");
    }

    private void DrawSpawnMarks()
    {
        ClearSpawnMarks();
        if (!IsActive || !_spawnMarksOn)
            return;

        foreach (var spawn in OfficialSpawns())
        {
            var origin = spawn.AbsOrigin!;
            var color = !SpawnEnabled(spawn)
                ? Color.FromArgb(255, 160, 40, 40)
                : !spawn.Enabled ? Color.FromArgb(255, 150, 150, 150)
                : spawn.DesignerName == "info_player_terrorist" ? Color.FromArgb(255, 255, 200, 40) : Color.FromArgb(255, 80, 160, 255);
            DrawSquare(origin, color);
        }
    }

    private void DrawSquare(Vector origin, Color color)
    {
        var h = SpawnSquareSize / 2f;
        var z = origin.Z + 2f;
        NumVec[] corners =
        [
            new(origin.X - h, origin.Y - h, z), new(origin.X + h, origin.Y - h, z),
            new(origin.X + h, origin.Y + h, z), new(origin.X - h, origin.Y + h, z)
        ];
        for (var i = 0; i < corners.Length; i++)
        {
            if (MakeBeam(corners[i], corners[(i + 1) % corners.Length], color, 1.2f) is { } beam)
                _spawnBeams.Add(beam);
        }
    }

    private void ClearSpawnMarks()
    {
        foreach (var beam in _spawnBeams.Where(b => b.IsValid))
            beam.Remove();
        _spawnBeams.Clear();
    }

    /// <summary>Called from the 1/32 s poll: an admin looking at a spawn square and pressing E switches that spawn.</summary>
    private void CheckSpawnToggle(CCSPlayerController player)
    {
        if ((player.Buttons & PlayerButtons.Use) == 0 || !_spawnMarksOn || !Players.IsAdmin(player, Plugin.Config.AdminFlag))
        {
            _spawnUseHeld.Remove(player.Slot);
            return;
        }
        if (!_spawnUseHeld.Add(player.Slot))
            return;

        var pawn = player.PlayerPawn.Value;
        if (!player.PawnIsAlive || pawn?.AbsOrigin == null || Plugin.Menus.IsOpen(player))
            return;

        var eye = new NumVec(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + EyeHeight);
        var forward = NadeSimulator.Forward(pawn.EyeAngles.X, pawn.EyeAngles.Y);

        Vector? bestOrigin = null;
        var bestOff = float.MaxValue;
        foreach (var spawn in OfficialSpawns())
        {
            var origin = spawn.AbsOrigin!;
            var target = new NumVec(origin.X, origin.Y, origin.Z + 2f);
            var to = target - eye;
            var along = NumVec.Dot(to, forward);
            if (along <= 0 || along > SpawnLookRange)
                continue;

            var off = (to - forward * along).Length() / along;
            if (off < SpawnLookCone && off < bestOff)
            {
                bestOrigin = origin;
                bestOff = off;
            }
        }

        if (bestOrigin is not { } chosen)
            return;

        var key = SpawnKey(chosen);
        if (!_disabledSpawns.Remove(key))
            _disabledSpawns.Add(key);
        SaveSpawnToggles();
        DrawSpawnMarks();
        Chat.To(player, _disabledSpawns.Contains(key)
            ? "Spawn {red}switched off{default}. !spawn skips it."
            : "Spawn {green}switched on{default}.");
    }
}
