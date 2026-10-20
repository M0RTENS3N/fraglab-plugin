using System.Net;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace FragLab.Core.Menus;

/// <summary>
/// Center-screen menus. Normal menus freeze the player and use movement keys:
/// W/S scroll (hold to scroll fast), E or D select, A back, R close.
/// Menus with <see cref="Menu.AllowMovement"/> leave the player free to walk and use
/// R next, Shift+R previous, E select.
/// </summary>
public sealed class MenuService
{
    private const int VisibleLines = 6;
    private const int RepeatDelayTicks = 24;
    private const int RepeatIntervalTicks = 4;

    private const PlayerButtons Up = PlayerButtons.Forward;
    private const PlayerButtons Down = PlayerButtons.Back;
    private const PlayerButtons Select = PlayerButtons.Use | PlayerButtons.Moveright;
    private const PlayerButtons BackKey = PlayerButtons.Moveleft;
    private const PlayerButtons CloseKey = PlayerButtons.Reload;

    // Walkable menus avoid the movement keys.
    private const PlayerButtons WalkNext = PlayerButtons.Reload;
    private const PlayerButtons WalkSelect = PlayerButtons.Use;
    private const PlayerButtons WalkReverse = PlayerButtons.Speed;

    private readonly Dictionary<int, Session> _sessions = new();

    public bool FreezePlayers { get; set; } = true;

    public void Register(BasePlugin plugin)
    {
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid != null)
                _sessions.Remove(e.Userid.Slot);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (e.Userid != null && _sessions.ContainsKey(e.Userid.Slot))
                Close(e.Userid);
            return HookResult.Continue;
        });
    }

    public bool IsOpen(CCSPlayerController player) => _sessions.ContainsKey(player.Slot);

    /// <summary>Opens a menu on top of the current one; "back" returns to the previous menu.</summary>
    public void Open(CCSPlayerController player, Menu menu)
    {
        if (!_sessions.TryGetValue(player.Slot, out var session))
        {
            session = new Session(player.SteamID) { LastButtons = player.Buttons };
            _sessions[player.Slot] = session;
        }

        session.Stack.Push(menu);
        UpdateFreeze(player, session);
    }

    public void Close(CCSPlayerController player)
    {
        if (!_sessions.Remove(player.Slot, out var session))
            return;
        if (session.Frozen)
            SetFrozen(player, false);
        player.PrintToCenterHtml(" ", 1);
    }

    public void CloseAll()
    {
        foreach (var player in Players.Connected().Where(IsOpen).ToList())
            Close(player);
    }

    private void Back(CCSPlayerController player, Session session)
    {
        session.Stack.Pop();
        if (session.Stack.Count == 0)
        {
            Close(player);
            return;
        }

        session.Stack.Peek().Build();
        UpdateFreeze(player, session);
    }

    /// <summary>Freezes the player only while the top menu needs the movement keys.</summary>
    private void UpdateFreeze(CCSPlayerController player, Session session)
    {
        var freeze = FreezePlayers && !session.Stack.Peek().AllowMovement;
        if (freeze == session.Frozen)
            return;
        SetFrozen(player, freeze);
        session.Frozen = freeze;
    }

    private void OnTick()
    {
        if (_sessions.Count == 0)
            return;

        foreach (var (slot, session) in _sessions.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (!Players.IsValid(player) || player!.SteamID != session.SteamId)
            {
                _sessions.Remove(slot);
                continue;
            }

            HandleInput(player, session);

            if (!_sessions.TryGetValue(slot, out var current) || current.Stack.Count == 0)
                continue;

            // The center panel fades almost immediately, so it has to be re-sent every tick.
            // The HTML is only rebuilt when the menu changes.
            var menu = current.Stack.Peek();
            var state = (menu, menu.Cursor, menu.Items.Count, menu.Version);
            if (current.LastHtml == null || !state.Equals(current.LastState))
            {
                current.LastHtml = Render(menu);
                current.LastState = state;
            }

            player.PrintToCenterHtml(current.LastHtml);
        }
    }

    private void HandleInput(CCSPlayerController player, Session session)
    {
        var buttons = player.Buttons;
        var pressed = buttons & ~session.LastButtons;
        session.LastButtons = buttons;

        var menu = session.Stack.Peek();

        if (menu.AllowMovement)
        {
            if ((pressed & WalkNext) != 0 && menu.Items.Count > 0)
            {
                var step = (buttons & WalkReverse) != 0 ? -1 : 1;
                menu.Cursor = (menu.Cursor + step + menu.Items.Count) % menu.Items.Count;
            }

            if ((pressed & WalkSelect) != 0)
                SelectCurrent(player, menu);
            return;
        }

        // Holding W/S repeats after a short delay.
        var scroll = buttons & (Up | Down);
        if (scroll != 0 && (pressed & scroll) == 0)
        {
            session.HeldTicks++;
            if (session.HeldTicks >= RepeatDelayTicks && (session.HeldTicks - RepeatDelayTicks) % RepeatIntervalTicks == 0)
                pressed |= scroll;
        }
        else
        {
            session.HeldTicks = 0;
        }

        if (menu.Items.Count > 0)
        {
            if ((pressed & Up) != 0)
                menu.Cursor = (menu.Cursor - 1 + menu.Items.Count) % menu.Items.Count;
            if ((pressed & Down) != 0)
                menu.Cursor = (menu.Cursor + 1) % menu.Items.Count;
        }

        if ((pressed & CloseKey) != 0)
        {
            Close(player);
            return;
        }

        if ((pressed & BackKey) != 0)
        {
            Back(player, session);
            return;
        }

        if ((pressed & Select) != 0)
            SelectCurrent(player, menu);
    }

    private void SelectCurrent(CCSPlayerController player, Menu menu)
    {
        if (menu.Cursor >= menu.Items.Count)
            return;

        var item = menu.Items[menu.Cursor];
        if (item.OnSelect == null)
            return;

        item.OnSelect(player);

        // If the action didn't open another menu or close this one, refresh it.
        if (_sessions.TryGetValue(player.Slot, out var after) && after.Stack.Count > 0 && after.Stack.Peek() == menu)
            menu.Build();
    }

    private static string Render(Menu menu)
    {
        var sb = new StringBuilder();
        sb.Append($"<font class='fontSize-m' color='#FFD700'>{Escape(menu.Title)}</font>");
        if (menu.Items.Count > VisibleLines)
            sb.Append($"<font class='fontSize-s' color='#888888'> {menu.Cursor + 1}/{menu.Items.Count}</font>");
        sb.Append("<br>");

        var start = Math.Clamp(menu.Cursor - VisibleLines / 2, 0, Math.Max(0, menu.Items.Count - VisibleLines));
        var end = Math.Min(menu.Items.Count, start + VisibleLines);

        for (var i = start; i < end; i++)
        {
            var item = menu.Items[i];
            var color = item.Color ?? (item.OnSelect == null ? "#888888" : "#FFFFFF");
            var text = Escape(item.Text);
            sb.Append(i == menu.Cursor
                ? $"<font color='#FFA500'>&gt; </font><font color='{color}'><b>{text}</b></font><br>"
                : $"<font color='{color}'>{text}</font><br>");
        }

        sb.Append(menu.AllowMovement
            ? "<font class='fontSize-s' color='#888888'>R next | Shift+R previous | E select | walk freely</font>"
            : "<font class='fontSize-s' color='#888888'>W/S move | E select | A back | R close</font>");
        return sb.ToString();
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static void SetFrozen(CCSPlayerController player, bool frozen)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive)
            return;

        var moveType = frozen ? MoveType_t.MOVETYPE_OBSOLETE : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        if (frozen)
            pawn.Teleport(null, null, new Vector(0, 0, 0));
    }

    private sealed class Session
    {
        public Session(ulong steamId)
        {
            SteamId = steamId;
        }

        public ulong SteamId { get; }
        public Stack<Menu> Stack { get; } = new();
        public PlayerButtons LastButtons { get; set; }
        public int HeldTicks { get; set; }
        public string? LastHtml { get; set; }
        public (Menu, int, int, int) LastState { get; set; }
        public bool Frozen { get; set; }
    }
}
