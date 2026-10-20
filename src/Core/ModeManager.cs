using CounterStrikeSharp.API.Core;

namespace FragLab.Core;

public sealed class ModeManager
{
    private readonly Dictionary<string, GameMode> _modes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameMode> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public GameMode? Active { get; private set; }

    public IEnumerable<GameMode> All => _modes.Values;

    public void Register(GameMode mode)
    {
        _modes[mode.Name] = mode;
        foreach (var alias in mode.Aliases)
            _aliases[alias] = mode;
    }

    public GameMode? Find(string name) => _modes.GetValueOrDefault(name) ?? _aliases.GetValueOrDefault(name);

    /// <summary>Sets the active mode without running activation logic (used before the first map has loaded).</summary>
    public void SetInitial(string name) => Active = Find(name);

    public bool Switch(string name)
    {
        var next = Find(name);
        if (next == null)
            return false;

        Active?.Deactivate();
        Active = next;
        next.Activate();
        return true;
    }

    /// <summary>Turns off all modes (vanilla server behaviour).</summary>
    public void Disable()
    {
        Active?.Deactivate();
        Active = null;
    }

    public void Dispatch(Action<GameMode> action)
    {
        if (Active != null)
            action(Active);
    }

    public HookResult Dispatch(Func<GameMode, HookResult> action) =>
        Active != null ? action(Active) : HookResult.Continue;
}
