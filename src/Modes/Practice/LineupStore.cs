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
