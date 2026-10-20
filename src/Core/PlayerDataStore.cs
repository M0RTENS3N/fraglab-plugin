using System.Text.Json;

namespace FragLab.Core;

/// <summary>Per-player data cached in memory and stored as &lt;dir&gt;/&lt;steamid64&gt;.json.</summary>
public sealed class PlayerDataStore<T> where T : new()
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly Dictionary<ulong, T> _cache = new();
    private readonly object _writeLock = new();

    public PlayerDataStore(string directory)
    {
        _directory = directory;
    }

    public T Get(ulong steamId)
    {
        if (_cache.TryGetValue(steamId, out var data))
            return data;

        var path = PathFor(steamId);
        try
        {
            data = File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T()
                : new T();
        }
        catch (JsonException)
        {
            data = new T();
        }

        _cache[steamId] = data;
        return data;
    }

    /// <summary>
    /// Serializes on the game thread (the data isn't thread-safe) but writes the file in the background,
    /// so slow server disks never stall a game tick.
    /// </summary>
    public void Save(ulong steamId)
    {
        if (!_cache.TryGetValue(steamId, out var data))
            return;

        var json = JsonSerializer.Serialize(data, JsonOptions);
        var path = PathFor(steamId);
        Task.Run(() =>
        {
            lock (_writeLock)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    File.WriteAllText(path, json);
                }
                catch (IOException)
                {
                    // Next save will try again.
                }
            }
        });
    }

    public void Forget(ulong steamId) => _cache.Remove(steamId);

    private string PathFor(ulong steamId) => Path.Combine(_directory, $"{steamId}.json");
}
