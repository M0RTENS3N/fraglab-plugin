using CounterStrikeSharp.API.Core;
using FragLab.Core;

namespace FragLab.Modes.Practice;

public sealed partial class PracticeMode
{
    /// <summary>"!mode prac" during a dry run goes back to practice, the same as !pracc.</summary>
    public override void Reselected(CCSPlayerController? player)
    {
        if (_dryRun)
            StopDryRun(player);
        else
            Chat.To(player, "{green}Practice{default} is already active.");
    }
}
