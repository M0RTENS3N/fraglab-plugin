using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using FragLab.Modes.Practice;
using FragLab.Modes.Retake;
using FragLab.Modes.Scrim;
using FragLab.Skins;

namespace FragLab;

/// <summary>
/// Generated on first load at addons/counterstrikesharp/configs/plugins/FragLabPlugin/FragLabPlugin.json
/// </summary>
public sealed class FragLabConfig : BasePluginConfig
{
    public const int CurrentVersion = 9;

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = CurrentVersion;

    /// <summary>Supports color tags like {green}, {red}, {default}.</summary>
    public string ChatPrefix { get; set; } = "{green}[FragLab]{default}";

    /// <summary>Mode active after server start: "scrim", "retake", "funretake", "prac" or "none".</summary>
    public string DefaultMode { get; set; } = "scrim";

    /// <summary>CounterStrikeSharp permission required for admin commands.</summary>
    public string AdminFlag { get; set; } = "@css/generic";

    /// <summary>Freeze players while a menu is open so W/A/S/D only navigate the menu.</summary>
    public bool FreezeWhileInMenu { get; set; } = true;

    public SkinsConfig Skins { get; set; } = new();

    public ScrimConfig Scrim { get; set; } = new();

    public RetakeConfig Retake { get; set; } = new();

    /// <summary>Fun retake perks. Rounds, spawns and buying come from Retake.</summary>
    public FunRetakeConfig FunRetake { get; set; } = new();

    public PracticeConfig Practice { get; set; } = new();
}
