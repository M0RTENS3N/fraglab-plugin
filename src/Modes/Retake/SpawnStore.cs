using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Modes.Retake;

public enum Bombsite
{
    A = 0,
    B = 1
}

public sealed class RetakeSpawn
{
    public CsTeam Team { get; set; }
    public Bombsite Site { get; set; }

    /// <summary>The T that spawns here plants the bomb.</summary>
    public bool IsPlanter { get; set; }

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }

    [JsonIgnore]
    public Vector Position => new(X, Y, Z);
    [JsonIgnore]
    public QAngle Angles => new(0, Yaw, 0);

    public float DistanceTo(Vector v)
    {
        var dx = X - v.X;
        var dy = Y - v.Y;
        var dz = Z - v.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// Per-map retake spawns. Custom spawns (made with the editor) live in &lt;plugin dir&gt;/spawns/&lt;map&gt;.json;
/// maps without them fall back to the built-in defaults in &lt;plugin dir&gt;/data/retake-spawns/&lt;map&gt;.json.
/// Saving always writes the custom file, so plugin updates never overwrite your own spawns.
/// </summary>
public sealed class SpawnStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly string _defaultsDirectory;

    public SpawnStore(string pluginDirectory)
    {
        _directory = Path.Combine(pluginDirectory, "spawns");
        _defaultsDirectory = Path.Combine(pluginDirectory, "data", "retake-spawns");
    }

    public string? Map { get; private set; }

    public List<RetakeSpawn> Spawns { get; private set; } = [];

    public bool IsDirty { get; private set; }

    /// <summary>True when the loaded spawns are the built-in defaults rather than a custom file.</summary>
    public bool UsingDefaults { get; private set; }

    private string PathFor(string map) => Path.Combine(_directory, $"{map}.json");

    public void Load(string map)
    {
        Map = map;
        IsDirty = false;

        var custom = PathFor(map);
        var defaults = Path.Combine(_defaultsDirectory, $"{map}.json");
        UsingDefaults = !File.Exists(custom) && File.Exists(defaults);

        var path = UsingDefaults ? defaults : custom;
        Spawns = File.Exists(path)
            ? JsonSerializer.Deserialize<List<RetakeSpawn>>(File.ReadAllText(path), JsonOptions) ?? []
            : [];
    }

    /// <summary>Writes the spawns to the custom file and returns its path.</summary>
    public string Save()
    {
        if (Map == null)
            throw new InvalidOperationException("No map loaded");
        Directory.CreateDirectory(_directory);
        var path = PathFor(Map);
        File.WriteAllText(path, JsonSerializer.Serialize(Spawns, JsonOptions));
        IsDirty = false;
        UsingDefaults = false;
        return path;
    }

    /// <summary>Deletes the custom file so the built-in defaults are used again.</summary>
    public bool ResetToDefaults()
    {
        if (Map == null)
            return false;
        var custom = PathFor(Map);
        if (File.Exists(custom))
            File.Delete(custom);
        Load(Map);
        return UsingDefaults;
    }

    public void Add(RetakeSpawn spawn)
    {
        Spawns.Add(spawn);
        IsDirty = true;
    }

    public RetakeSpawn? Nearest(Vector position, float maxDistance)
    {
        return Spawns
            .Where(s => s.DistanceTo(position) <= maxDistance)
            .MinBy(s => s.DistanceTo(position));
    }

    public void Remove(RetakeSpawn spawn)
    {
        if (Spawns.Remove(spawn))
            IsDirty = true;
    }

    public IEnumerable<RetakeSpawn> For(Bombsite site, CsTeam team) =>
        Spawns.Where(s => s.Site == site && s.Team == team);

    /// <summary>Roughly where the bomb gets planted: the middle of the site's planter spawns (or T spawns).</summary>
    public Vector? SiteCenter(Bombsite site)
    {
        var spots = For(site, CsTeam.Terrorist).Where(s => s.IsPlanter).ToList();
        if (spots.Count == 0)
            spots = For(site, CsTeam.Terrorist).ToList();
        if (spots.Count == 0)
            return null;
        return new Vector(spots.Average(s => s.X), spots.Average(s => s.Y), spots.Average(s => s.Z));
    }

    /// <summary>A site is playable when it has at least one spawn per team.</summary>
    public bool IsPlayable(Bombsite site) =>
        For(site, CsTeam.Terrorist).Any() && For(site, CsTeam.CounterTerrorist).Any();
}
