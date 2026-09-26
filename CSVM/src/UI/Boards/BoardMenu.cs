using System;
using System.Collections.Generic;

namespace CSVM.UI.Boards;

/// <summary>
/// The cursor and item list a board carries, engine-free so the selection rules are testable off
/// engine the way <see cref="CSVM.Flight.Modes.PauseState"/> is. Holds no input source of its own:
/// the board polls its menu owner through <see cref="Screens.MenuInput"/> and feeds one frame's result to
/// <see cref="Handle"/>. Only the owner's input ever arrives here, which is what stops a second
/// pad steering a menu it does not own.
/// </summary>
public sealed class BoardMenu
{
    private readonly (BoardMenuItem Item, string Label)[] _items;

    /// <summary>Builds a menu over the given rows. A dismissable menu also answers B/Start/Esc with
    /// <see cref="Dismissed"/>; a results board's menu does not, because dismissing it would leave
    /// the player in a halted world with no way back.</summary>
    public BoardMenu(bool dismissable, params (BoardMenuItem Item, string Label)[] items)
    {
        if (items.Length == 0)
            throw new ArgumentException("a board menu needs at least one item", nameof(items));
        _items = items;
        Dismissable = dismissable;
    }

    /// <summary>Fires when the owner confirms a row.</summary>
    public event Action<BoardMenuItem>? Activated;

    /// <summary>Fires when the owner backs out of a dismissable menu.</summary>
    public event Action? Dismissed;

    public IReadOnlyList<(BoardMenuItem Item, string Label)> Items => _items;

    /// <summary>The highlighted row. Starts at the first item, which is always the harmless one
    /// (Resume, else Photo Mode), so a stray confirm on a menu that just appeared is never
    /// destructive. ⚠ That is why Photo Mode leads a results board: the alternative resting row is
    /// Restart, which throws away the run just finished.</summary>
    public int Index { get; private set; }

    public bool Dismissable { get; }

    /// <summary>Applies one frame of the owner's menu input and returns whether the highlight
    /// moved, so a board repaints only when it has to. Confirm wins over back in the same frame:
    /// the row has already been chosen, so the menu is on its way out either way.</summary>
    public bool Handle(int move, bool accept, bool back)
    {
        bool moved = false;
        if (move != 0 && _items.Length > 1)
        {
            Index = Wrap(Index + move, _items.Length);
            moved = true;
        }

        if (accept)
            Activated?.Invoke(_items[Index].Item);
        else if (back && Dismissable)
            Dismissed?.Invoke();

        return moved;
    }

    /// <summary>Puts the cursor on one row and answers whether it moved, for a board whose pointer
    /// shares this cursor with the pad and the keyboard. A row outside the list is refused, so a
    /// hit test that answers -1 leaves the cursor where the other devices left it.</summary>
    public bool MoveTo(int index)
    {
        if (index < 0 || index >= _items.Length || index == Index)
        {
            return false;
        }

        Index = index;
        return true;
    }

    /// <summary>Puts the cursor back on the first row, for a board that reuses one menu across
    /// several appearances rather than rebuilding it.</summary>
    public void Reset() => Index = 0;

    // The launchscreen's own cursor rule, so every list in the game wraps the same way.
    private static int Wrap(int index, int count) => ((index % count) + count) % count;
}
