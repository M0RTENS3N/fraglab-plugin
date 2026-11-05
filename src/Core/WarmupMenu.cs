using CounterStrikeSharp.API.Core;
using FragLab.Core.Menus;

namespace FragLab.Core;

/// <summary>
/// The !warm menu, shown during scrim warmup (and from deathmatch, to get back): play retake, deathmatch,
/// or go back to the scrim warmup. Switching modes is admin-only, like !mode.
/// </summary>
public static class WarmupMenu
{
    public static void Open(FragLabPlugin plugin, CCSPlayerController? player)
    {
        if (player == null || !Players.IsValid(player))
            return;
        if (!Players.IsAdmin(player, plugin.Config.AdminFlag))
        {
            Chat.To(player, "{red}Only admins can change the warmup mode.");
            return;
        }

        var current = plugin.Modes.Active?.Name;
        plugin.Menus.Open(player, new Menu("Warmup", menu =>
        {
            if (current != "retake")
                menu.Add("Retake", p => Switch(plugin, p, "retake"));
            if (current != "dm")
                menu.Add("Deathmatch", p => Switch(plugin, p, "dm"));
            if (current != "scrim")
                menu.Add("Scrim warmup (back to the match)", p => Switch(plugin, p, "scrim"));
            menu.Add("Close", p => plugin.Menus.Close(p), "#888888");
        }));
    }

    private static void Switch(FragLabPlugin plugin, CCSPlayerController player, string mode)
    {
        plugin.Menus.Close(player);
        if (!plugin.Modes.Switch(mode))
        {
            Chat.To(player, $"{{red}}Mode {mode} is not available.");
            return;
        }
        Chat.All($"{{green}}{player.PlayerName}{{default}} switched to {{green}}{mode}{{default}}.");
    }
}
