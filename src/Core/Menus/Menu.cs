using CounterStrikeSharp.API.Core;

namespace FragLab.Core.Menus;

public sealed class MenuItem
{
    public MenuItem(string text, Action<CCSPlayerController>? onSelect, string? color)
    {
        Text = text;
        OnSelect = onSelect;
        Color = color;
    }

    public string Text { get; }

    /// <summary>Null for a non-selectable line.</summary>
    public Action<CCSPlayerController>? OnSelect { get; }

    /// <summary>Hex color like "#eb4b4b", or null for white.</summary>
    public string? Color { get; }
}

/// <summary>
/// A scrollable list rendered in the center of the screen. When a builder is given,
/// the items are rebuilt after every selection so markers like "[x]" stay current.
/// </summary>
public sealed class Menu
{
    private readonly Action<Menu>? _builder;

    public Menu(string title, Action<Menu>? builder = null)
    {
        Title = title;
        _builder = builder;
        Build();
    }

    public string Title { get; }

    public List<MenuItem> Items { get; } = new();

    public int Cursor { get; set; }

    /// <summary>Don't freeze the player; navigate with R / Shift+R / E instead of W/A/S/D.</summary>
    public bool AllowMovement { get; init; }

    public Menu Add(string text, Action<CCSPlayerController>? onSelect = null, string? color = null)
    {
        Items.Add(new MenuItem(text, onSelect, color));
        return this;
    }

    /// <summary>Changes whenever the items are rebuilt, so renderers know to redraw.</summary>
    public int Version { get; private set; }

    public void Build()
    {
        Version++;
        if (_builder == null)
            return;
        Items.Clear();
        _builder(this);
        Cursor = Math.Clamp(Cursor, 0, Math.Max(0, Items.Count - 1));
    }
}
