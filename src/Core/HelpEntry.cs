namespace FragLab.Core;

/// <summary>A command as shown by !help.</summary>
public sealed class HelpEntry
{
    public HelpEntry(string section, string[] names, string description, bool adminOnly)
    {
        Section = section;
        Command = names[0];
        Aliases = names.Skip(1).ToArray();

        // Descriptions may end with ": !usage <args>", which is split off as the usage line.
        var split = description.IndexOf(": !", StringComparison.Ordinal);
        Description = split >= 0 ? description[..split] : description;
        Usage = split >= 0 ? description[(split + 2)..] : $"!{Command}";
        AdminOnly = adminOnly;
    }

    public string Section { get; }

    /// <summary>Primary name without prefix, e.g. "noclip" (runs as css_noclip).</summary>
    public string Command { get; }

    public string[] Aliases { get; }
    public string Description { get; }

    /// <summary>e.g. "!savenade &lt;name&gt;".</summary>
    public string Usage { get; }

    public bool AdminOnly { get; }

    /// <summary>Commands with required arguments can't be run straight from the menu.</summary>
    public bool NeedsArguments => Usage.Contains('<');

    public string AliasText => Aliases.Length == 0 ? "" : $" (also {string.Join(", ", Aliases.Select(a => "!" + a))})";
}
