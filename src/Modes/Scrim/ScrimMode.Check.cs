using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using FragLab.Core;
using Microsoft.Extensions.Logging;

namespace FragLab.Modes.Scrim;

/// <summary>
/// !scrimcheck (admin): compares the live server settings with scrim_live.cfg and lists every one that differs.
/// Run it after going live to make sure nothing from retake or deathmatch is still active.
/// </summary>
public sealed partial class ScrimMode
{
    private void ScrimCheck(CCSPlayerController? player)
    {
        var path = Path.Combine(Server.GameDirectory, "csgo", "cfg", Cfg.LiveConfig);
        if (!File.Exists(path))
        {
            Chat.To(player, $"Can't find {Cfg.LiveConfig} on the server.");
            return;
        }

        var differences = new List<string>();
        var checkedCount = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
                continue;

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var cvar = ConVar.Find(parts[0]);
            if (cvar == null)
                continue;

            checkedCount++;
            var expected = parts[1].Trim().Trim('"');
            var actual = cvar.StringValue ?? "";
            if (!SameValue(actual, expected))
                differences.Add($"{parts[0]} = {actual} (scrim: {expected})");
        }

        if (differences.Count == 0)
        {
            Chat.To(player, $"{{green}}All {checkedCount} checked settings match the scrim config.");
            return;
        }

        Chat.To(player, $"{{red}}{differences.Count} setting(s) differ from the scrim config:");
        foreach (var difference in differences.Take(8))
            Chat.To(player, difference);
        if (differences.Count > 8)
            Chat.To(player, $"...and {differences.Count - 8} more (see the server log).");
        foreach (var difference in differences)
            Plugin.Logger.LogWarning("scrimcheck: {Difference}", difference);
    }

    private static bool SameValue(string actual, string expected)
    {
        if (float.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            float.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
            return MathF.Abs(a - e) < 0.001f;
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
