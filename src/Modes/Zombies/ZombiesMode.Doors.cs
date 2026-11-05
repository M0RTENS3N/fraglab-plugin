using System.Drawing;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using FragLab.Core;

namespace FragLab.Modes.Zombies;

public sealed class ZombieDoor
{
    public string Name { get; set; } = "";
    public int Price { get; set; } = 750;
    public float MinX { get; set; }
    public float MinY { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxY { get; set; }
    public float MaxZ { get; set; }
}

/// <summary>
/// COD-style doors: boxes (drawn with red lasers) that players can't walk through until someone buys them open,
/// with E or !open. Zombies ignore doors. Doors are saved per map in &lt;plugin dir&gt;/zombie-doors/&lt;map&gt;.json
/// and made in-game with !zdoor. Every new game closes them again.
/// </summary>
public sealed partial class ZombiesMode
{
    private const float DoorUseRange = 90f;

    private static readonly JsonSerializerOptions DoorJson = new() { WriteIndented = true };

    private readonly List<ZombieDoor> _doors = new();
    private readonly HashSet<ZombieDoor> _openDoors = new();
    private readonly Dictionary<ZombieDoor, List<CBeam>> _doorBeams = new();
    private readonly Dictionary<int, Vector> _safePosition = new();
    private readonly HashSet<int> _usePressed = new();
    private readonly Dictionary<int, Vector> _doorCorner = new();
    private string? _doorsMap;

    private string DoorFile(string map) => Path.Combine(Plugin.ModuleDirectory, "zombie-doors", $"{map}.json");

    private void RegisterDoors()
    {
        AddCommand(["open", "buydoor"], "Buy open the door you stand next to", (p, _) => WithAlive(p, TryOpenNearestDoor));
        AddCommand(["zdoor"], "Door editor: !zdoor a | b <price> <name> | del | list", OnDoorCommand, adminOnly: true);
        Plugin.RegisterListener<Listeners.OnTick>(DoorTick);
    }

    // ---------------------------------------------------------------- loading and drawing

    private void LoadDoors()
    {
        RemoveDoorBeams();
        _doors.Clear();
        _openDoors.Clear();
        _doorsMap = Server.MapName;

        var file = DoorFile(_doorsMap);
        if (File.Exists(file))
        {
            try
            {
                _doors.AddRange(JsonSerializer.Deserialize<List<ZombieDoor>>(File.ReadAllText(file)) ?? []);
            }
            catch (Exception ex)
            {
                Chat.All($"{{red}}Could not read the zombie doors for this map:{{default}} {ex.Message}");
            }
        }

        foreach (var door in _doors)
            DrawDoor(door);
    }

    private void SaveDoors()
    {
        var file = DoorFile(Server.MapName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(_doors, DoorJson));
    }

    /// <summary>Closes every door again for a new game.</summary>
    private void ResetDoors()
    {
        _safePosition.Clear();
        _usePressed.Clear();
        if (_doorsMap != Server.MapName)
        {
            LoadDoors();
            return;
        }

        _openDoors.Clear();
        RemoveDoorBeams();
        foreach (var door in _doors)
            DrawDoor(door);
    }

    private void DrawDoor(ZombieDoor door)
    {
        if (_doorBeams.ContainsKey(door))
            return;

        var c = new[]
        {
            new Vector(door.MinX, door.MinY, door.MinZ), new Vector(door.MaxX, door.MinY, door.MinZ),
            new Vector(door.MaxX, door.MaxY, door.MinZ), new Vector(door.MinX, door.MaxY, door.MinZ),
            new Vector(door.MinX, door.MinY, door.MaxZ), new Vector(door.MaxX, door.MinY, door.MaxZ),
            new Vector(door.MaxX, door.MaxY, door.MaxZ), new Vector(door.MinX, door.MaxY, door.MaxZ)
        };
        int[][] edges =
        [
            [0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7],
            [0, 6], [1, 7] // crosses through the middle, so it looks like a barrier
        ];

        var beams = new List<CBeam>();
        foreach (var edge in edges)
        {
            var beam = Utilities.CreateEntityByName<CBeam>("beam");
            if (beam == null)
                continue;
            beam.Render = Color.FromArgb(255, 255, 40, 40);
            beam.Width = 1.5f;
            beam.EndWidth = 1.5f;
            beam.Teleport(c[edge[0]], new QAngle(0, 0, 0), new Vector(0, 0, 0));
            beam.EndPos.X = c[edge[1]].X;
            beam.EndPos.Y = c[edge[1]].Y;
            beam.EndPos.Z = c[edge[1]].Z;
            beam.DispatchSpawn();
            beams.Add(beam);
        }
        _doorBeams[door] = beams;
    }

    private void RemoveDoorBeams(ZombieDoor? only = null)
    {
        foreach (var (door, beams) in _doorBeams.ToList())
        {
            if (only != null && door != only)
                continue;
            foreach (var beam in beams.Where(b => b.IsValid))
                beam.Remove();
            _doorBeams.Remove(door);
        }
    }

    // ---------------------------------------------------------------- blocking and buying

    private static bool Inside(ZombieDoor door, Vector p, float margin) =>
        p.X >= door.MinX - margin && p.X <= door.MaxX + margin &&
        p.Y >= door.MinY - margin && p.Y <= door.MaxY + margin &&
        p.Z >= door.MinZ - 72f && p.Z <= door.MaxZ;

    private static float DistanceToDoor(ZombieDoor door, Vector p)
    {
        var dx = Math.Max(Math.Max(door.MinX - p.X, 0), p.X - door.MaxX);
        var dy = Math.Max(Math.Max(door.MinY - p.Y, 0), p.Y - door.MaxY);
        var dz = Math.Max(Math.Max(door.MinZ - p.Z, 0), p.Z - door.MaxZ);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private IEnumerable<ZombieDoor> ClosedDoors() => _doors.Where(d => !_openDoors.Contains(d));

    /// <summary>Every tick: players walking into a closed door are put back, and E next to a door buys it.</summary>
    private void DoorTick()
    {
        if (!IsActive || _editMode || _doors.Count == 0 || _doors.Count == _openDoors.Count)
            return;

        foreach (var player in Humans())
        {
            var pawn = player.PlayerPawn.Value;
            if (!player.PawnIsAlive || pawn?.AbsOrigin == null)
                continue;

            var origin = pawn.AbsOrigin;
            var blocked = ClosedDoors().Any(d => Inside(d, origin, 16f));
            if (blocked)
            {
                if (_safePosition.TryGetValue(player.Slot, out var safe))
                    pawn.Teleport(safe, null, new Vector(0, 0, 0));
            }
            else
            {
                _safePosition[player.Slot] = new Vector(origin.X, origin.Y, origin.Z);
            }

            var use = (player.Buttons & PlayerButtons.Use) != 0;
            if (use && _usePressed.Add(player.Slot))
            {
                if (NearestClosedDoor(player) != null)
                    TryOpenNearestDoor(player);
            }
            else if (!use)
            {
                _usePressed.Remove(player.Slot);
            }
        }
    }

    private ZombieDoor? NearestClosedDoor(CCSPlayerController player)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        if (origin == null)
            return null;
        return ClosedDoors()
            .Select(d => (Door: d, Distance: DistanceToDoor(d, origin)))
            .Where(d => d.Distance <= DoorUseRange)
            .OrderBy(d => d.Distance)
            .Select(d => d.Door)
            .FirstOrDefault();
    }

    /// <summary>Center text hint, e.g. "Press E to open Mid (750)".</summary>
    private string DoorHint(CCSPlayerController player)
    {
        var door = player.PawnIsAlive ? NearestClosedDoor(player) : null;
        return door == null ? "" : $"\nPress E to open {door.Name} ({door.Price})";
    }

    private void TryOpenNearestDoor(CCSPlayerController player)
    {
        var door = NearestClosedDoor(player);
        if (door == null)
        {
            Chat.To(player, "No closed door near you.");
            return;
        }
        if (!TrySpend(player, door.Price))
            return;

        _openDoors.Add(door);
        RemoveDoorBeams(door);
        Chat.All($"{{gold}}{player.PlayerName}{{default}} opened {{green}}{door.Name}{{default}}!");
    }

    // ---------------------------------------------------------------- editor

    private void OnDoorCommand(CCSPlayerController? player, CommandInfo info)
    {
        var sub = info.ArgCount >= 2 ? info.GetArg(1).ToLowerInvariant() : "";
        var origin = player?.PlayerPawn.Value?.AbsOrigin;

        switch (sub)
        {
            case "a" when player != null && origin != null:
                SetDoorCorner(player);
                return;

            case "b" when player != null && origin != null:
                if (info.ArgCount < 4 || !int.TryParse(info.GetArg(2), out var price))
                {
                    Chat.To(player, "Usage: !zdoor b <price> <name>");
                    return;
                }
                FinishDoor(player, price, string.Join(" ", Enumerable.Range(3, info.ArgCount - 3).Select(info.GetArg)));
                return;

            case "del" when player != null:
                DeleteNearestDoor(player);
                return;

            case "list":
                ListDoors(player);
                return;

            default:
                Chat.To(player, "Door editor: stand on one side of a doorway: {green}!zdoor a{default}, then on the other side: " +
                                "{green}!zdoor b <price> <name>{default}. {green}!zdoor del{default} removes the nearest, {green}!zdoor list{default} lists them.");
                return;
        }
    }

    /// <summary>A box between two floor points, from the floor to a bit above head height, at least 32 units thick.</summary>
    private static ZombieDoor MakeDoor(Vector a, Vector b, int price, string name)
    {
        float minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
        float minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
        Widen(ref minX, ref maxX);
        Widen(ref minY, ref maxY);

        return new ZombieDoor
        {
            Name = name,
            Price = price,
            MinX = minX, MaxX = maxX,
            MinY = minY, MaxY = maxY,
            MinZ = Math.Min(a.Z, b.Z) - 8f,
            MaxZ = Math.Max(a.Z, b.Z) + 128f
        };

        static void Widen(ref float min, ref float max)
        {
            if (max - min >= 32f)
                return;
            var center = (min + max) / 2f;
            min = center - 16f;
            max = center + 16f;
        }
    }
}
