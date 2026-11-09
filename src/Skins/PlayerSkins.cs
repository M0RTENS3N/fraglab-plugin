namespace FragLab.Skins;

public sealed class SkinChoice
{
    public int Paint { get; set; }
    public float Wear { get; set; }
    public int Seed { get; set; }
}

public sealed class PlayerSkins
{
    /// <summary>Knife definition index (e.g. 507 = Karambit); null keeps the default knife.</summary>
    public int? Knife { get; set; }

    /// <summary>Glove definition index; null keeps the agent's default gloves.</summary>
    public int? Gloves { get; set; }

    /// <summary>Name of the ready-made loadout in use (Green/Red/Blue), or null for hand-picked skins.</summary>
    public string? Preset { get; set; }

    /// <summary>Wear used for newly selected skins.</summary>
    public float PreferredWear { get; set; } = 0.0001f;

    /// <summary>Selected paint per weapon/knife/glove definition index.</summary>
    public Dictionary<int, SkinChoice> Items { get; set; } = new();

    /// <summary>Knife + gloves combos the player saved in the skins menu.</summary>
    public List<KnifeGloveCombo> Combos { get; set; } = new();
}

/// <summary>A saved knife + gloves pair, with the paints chosen for each.</summary>
public sealed class KnifeGloveCombo
{
    public string Name { get; set; } = "";
    public int? Knife { get; set; }
    public int? Gloves { get; set; }
    public SkinChoice? KnifeChoice { get; set; }
    public SkinChoice? GloveChoice { get; set; }
}
