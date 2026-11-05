using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Modes.Practice;

public sealed class NadeLineup
{
    public string Name { get; set; } = "";

    /// <summary>Grenade class, e.g. weapon_smokegrenade. Empty if saved without a grenade in hand.</summary>
    public string Grenade { get; set; } = "";

    public string Author { get; set; } = "";

    /// <summary>How it was thrown, e.g. jumpthrow or runthrow. Recorded with the first throw after !savenade; empty if unknown.</summary>
    public string Throw { get; set; } = "";

    /// <summary>full (left click), medium (both) or short (right click). Empty if unknown.</summary>
    public string Strength { get; set; } = "";

    /// <summary>Movement in the ~2.5 s before the throw, for ghost replays. Empty for lineups saved before ghosts.</summary>
    public List<PathFrame> Path { get; set; } = [];
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }

    [JsonIgnore]
    public Vector Position => new(X, Y, Z);
    [JsonIgnore]
    public QAngle Angles => new(Pitch, Yaw, 0);
}

/// <summary>One recorded frame: where the player stood and where they looked.</summary>
public sealed class PathFrame
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
}

/// <summary>Grenade lineups shared by everyone, stored per map in &lt;plugin dir&gt;/lineups/&lt;map&gt;.json.</summary>
public sealed class LineupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public LineupStore(string pluginDirectory)
    {
        _directory = Path.Combine(pluginDirectory, "lineups");
    }

    public string? Map { get; private set; }

    public List<NadeLineup> Lineups { get; private set; } = [];

    public void Load(string map)
    {
        Map = map;
        var path = PathFor(map);
        try
        {
            Lineups = File.Exists(path)
                ? JsonSerializer.Deserialize<List<NadeLineup>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (JsonException)
        {
            Lineups = [];
        }
    }

    public void Save()
    {
        if (Map == null)
            throw new InvalidOperationException("No map loaded");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathFor(Map), JsonSerializer.Serialize(Lineups, JsonOptions));
    }

    public NadeLineup? Find(string name) =>
        Lineups.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private string PathFor(string map) => Path.Combine(_directory, $"{map}.json");
}
