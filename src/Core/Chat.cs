using System.Reflection;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Core;

/// <summary>
/// Chat helpers. Messages may contain color tags such as {green}, {red}, {default}
/// which map to the fields of <see cref="ChatColors"/>.
/// </summary>
public static class Chat
{
    private static readonly Dictionary<string, string> ColorTags = BuildColorTags();
    private static readonly Regex TagPattern = new(@"\{[a-zA-Z]+\}", RegexOptions.Compiled);

    public static string Prefix { get; set; } = "{green}[FragLab]{default}";

    public static string Colorize(string message)
    {
        foreach (var (tag, code) in ColorTags)
            message = message.Replace(tag, code, StringComparison.OrdinalIgnoreCase);
        return message;
    }

    public static string StripTags(string message) => TagPattern.Replace(message, "");

    /// <summary>Formats with prefix. The leading space is required for colors to render in CS2 chat.</summary>
    private static string Format(string message) => Colorize($" {Prefix} {message}");

    public static void All(string message) => Server.PrintToChatAll(Format(message));

    public static void To(CCSPlayerController? player, string message)
    {
        if (player == null)
        {
            Server.PrintToConsole(StripTags($"{Prefix} {message}"));
            return;
        }

        if (player.IsValid)
            player.PrintToChat(Format(message));
    }

    public static void Team(CsTeam team, string message)
    {
        foreach (var player in Players.OnTeam(team))
            To(player, message);
    }

    public static void CenterAll(string message)
    {
        foreach (var player in Players.Connected())
            player.PrintToCenter(StripTags(message));
    }

    private static Dictionary<string, string> BuildColorTags()
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

        foreach (var field in typeof(ChatColors).GetFields(flags))
            tags[$"{{{field.Name.ToLowerInvariant()}}}"] = field.GetValue(null)?.ToString() ?? "";

        foreach (var prop in typeof(ChatColors).GetProperties(flags))
            tags[$"{{{prop.Name.ToLowerInvariant()}}}"] = prop.GetValue(null)?.ToString() ?? "";

        return tags;
    }
}
