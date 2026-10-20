using System.Text.Json;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace FragLab.Core;

/// <summary>
/// One-time move from the old plugin name (UltimatePlugin) to FragLabPlugin: the config and saved data
/// (spawns, lineups, skin choices, saved buys, nade finder measurements) live in folders named after the
/// plugin, so without this everything would start from scratch after the rename.
/// Existing FragLab files are never overwritten.
/// </summary>
public static class UltimateMigration
{
    private const string OldName = "UltimatePlugin";
    private const string NewName = "FragLabPlugin";
    private const string Marker = ".imported-from-ultimate";

    private static string ConfigsDirectory =>
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins");

    /// <summary>The old config with paths and names updated, or null when there is nothing (left) to import.</summary>
    public static FragLabConfig? ImportConfig(ILogger? logger)
    {
        try
        {
            var oldFile = Path.Combine(ConfigsDirectory, OldName, $"{OldName}.json");
            var newDirectory = Path.Combine(ConfigsDirectory, NewName);
            var marker = Path.Combine(newDirectory, Marker);
            if (!File.Exists(oldFile) || File.Exists(marker))
                return null;

            var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var json = File.ReadAllText(oldFile)
                .Replace("\"ultimate/", "\"fraglab/")
                .Replace("ultimate_backup", "fraglab_backup")
                .Replace("[Ultimate]", "[FragLab]");
            var config = JsonSerializer.Deserialize<FragLabConfig>(json, options);
            if (config == null)
                return null;

            Directory.CreateDirectory(newDirectory);
            File.WriteAllText(marker, $"Imported from {oldFile} on {DateTime.Now:yyyy-MM-dd HH:mm}");
            logger?.LogInformation("Imported settings from the old {Old} config", OldName);
            return config;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Could not import the old {Old} config: {Message}", OldName, ex.Message);
            return null;
        }
    }

    /// <summary>Copies saved data from the old plugin folder into this one (only files that don't exist yet).</summary>
    public static void CopyData(string moduleDirectory, ILogger logger)
    {
        var oldDirectory = Path.Combine(Path.GetDirectoryName(moduleDirectory) ?? moduleDirectory, OldName);
        if (!Directory.Exists(oldDirectory))
            return;

        if (File.Exists(Path.Combine(oldDirectory, $"{OldName}.dll")))
        {
            logger.LogWarning("The old {Old} is still installed next to FragLab Plugin. Delete addons/counterstrikesharp/plugins/{Old} " +
                              "(and cfg/ultimate) so commands don't run twice.", OldName, OldName);
        }

        var copied = 0;
        try
        {
            foreach (var folder in new[] { "spawns", "lineups", "playerdata" })
                copied += CopyMissing(Path.Combine(oldDirectory, folder), Path.Combine(moduleDirectory, folder));

            foreach (var file in new[] { "grenade-collision.txt", "jump-throw.txt" })
            {
                var from = Path.Combine(oldDirectory, "data", file);
                var to = Path.Combine(moduleDirectory, "data", file);
                if (File.Exists(from) && !File.Exists(to))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to);
                    copied++;
                }
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning("Copying data from the old {Old} folder stopped: {Message}", OldName, ex.Message);
        }

        if (copied > 0)
            logger.LogInformation("Copied {Count} saved files from the old {Old} folder", copied, OldName);
    }

    private static int CopyMissing(string from, string to)
    {
        if (!Directory.Exists(from))
            return 0;

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            copied++;
        }
        return copied;
    }
}
