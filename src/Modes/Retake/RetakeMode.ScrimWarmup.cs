using CounterStrikeSharp.API.Core;
using FragLab.Core;
using FragLab.Modes.Scrim;

namespace FragLab.Modes.Retake;

/// <summary>Ready-up, force start and the warmup menu while retake runs as a scrim warmup (see ScrimMode.WarmupRetake).</summary>
public sealed partial class RetakeMode
{
    private ScrimMode? Scrim() => Plugin.Modes.Find("scrim") as ScrimMode;

    /// <summary>"!Sovs" readies a team during the retake warmup too.</summary>
    public override bool HandleChatWord(CCSPlayerController player, string word) => Scrim()?.TryReadyByTeamName(player, word) ?? false;

    private void RegisterScrimWarmup()
    {
        AddCommand(["ready", "r", "rdy"], "Ready up for the scrim (during retake warmup)", (p, _) => Scrim()?.WarmupReadyFromRetake(p));
        AddCommand(["forcestart", "start"], "Start the scrim match (during retake warmup)", (p, _) => Scrim()?.WarmupForceStartFromRetake(p), adminOnly: true);
        AddCommand(["warm", "warmup"], "Warmup menu (admin): retake and deathmatch warmup", (p, _) => Scrim()?.WarmupMenuFromRetake(p), adminOnly: true);
    }
}
