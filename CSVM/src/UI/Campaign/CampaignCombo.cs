using System;
using System.Collections.Generic;

namespace CSVM.UI.Campaign;

/// <summary>
/// A campaign screen's drop-down list, the original's <c>D</c> widget (<c>PS_D_PILOTPLANE</c>,
/// <c>OL_D_AMMO0</c>): a field showing the current entry, and a list that opens over the screen.
/// The shell has no pointer, so the confirm opens it, the cursor axis moves inside it and the
/// confirm commits; a closed field still steps on the horizontal axis, which is how every other
/// campaign row changes.
///
/// ⚠ The combo never decides its own value. <see cref="Confirm"/> and <see cref="Next"/>
/// return a candidate and <see cref="Select"/> is the only thing that moves the pick, because the
/// screens that own one refuse some picks: a plane another crew slot flies is answered with a
/// dialog and the field left where it was (<c>PLANESELECTION.SCRIPT</c>'s own revert).
/// </summary>
public sealed class CampaignCombo
{
    private readonly List<string> _entries = new();

    /// <summary>Builds the widget at its authored geometry: <c>LAYOUT.CSV</c>'s X and Y, the
    /// <c>D</c> row's own width, <c>STDITEMH</c> and <c>ITEMSDISPLAYED</c>.</summary>
    public CampaignCombo(float x, float y, float width, float rowHeight, int rowsDisplayed)
    {
        X = x;
        Y = y;
        Width = width;
        RowHeight = rowHeight;
        RowsDisplayed = Math.Max(1, rowsDisplayed);
    }

    /// <summary>The field's authored left edge.</summary>
    public float X { get; }

    /// <summary>The field's authored top edge. The list opens below it.</summary>
    public float Y { get; }

    /// <summary>The field's authored width, which the open list shares.</summary>
    public float Width { get; }

    /// <summary>One row's height, the layout's <c>STDITEMH</c>.</summary>
    public float RowHeight { get; }

    /// <summary>How many rows the open list shows before it scrolls.</summary>
    public int RowsDisplayed { get; }

    /// <summary>The entries, in the order the list draws them.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Which entry the field shows, and what a fresh open highlights.</summary>
    public int Selected { get; private set; }

    /// <summary>Whether the list is open, which is what makes the widget own the cursor.</summary>
    public bool Open { get; private set; }

    /// <summary>The entry the cursor is on while the list is open.</summary>
    public int Highlight { get; private set; }

    /// <summary>The first entry of the visible window, which scrolling moves.</summary>
    public int First { get; private set; }

    /// <summary>The field's own words: the selected entry, or "" when there are none.</summary>
    public string Text => Selected >= 0 && Selected < _entries.Count ? _entries[Selected] : string.Empty;

    /// <summary>How many rows the open list actually draws.</summary>
    public int Visible => Math.Min(RowsDisplayed, _entries.Count);

    /// <summary>Whether the list is longer than its window, which is when a scrollbar is drawn.</summary>
    public bool Scrolls => _entries.Count > RowsDisplayed;

    /// <summary>Replaces the entries and the pick, and closes the list. The screens rebuild their
    /// entries whenever the thing behind them changes, so this is the normal way one is filled.</summary>
    public void Load(IReadOnlyList<string> entries, int selected)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        Open = false;
        Select(selected);
    }

    /// <summary>Moves the pick, and the window with it. The only thing that changes the field.</summary>
    public void Select(int index)
    {
        Selected = _entries.Count == 0 ? 0 : Math.Clamp(index, 0, _entries.Count - 1);
        Highlight = Selected;
        Scroll();
    }

    /// <summary>Opens the list on the current pick. False when there is nothing to choose
    /// between, which leaves the confirm for the page to do something else with.</summary>
    public bool Expand()
    {
        if (Open || _entries.Count < 2)
        {
            return false;
        }

        Open = true;
        Highlight = Selected;
        Scroll();
        return true;
    }

    /// <summary>Moves the cursor inside the open list, wrapping the way every launchscreen list
    /// wraps, and scrolls the window to keep the cursor in it.</summary>
    public bool Move(int dir)
    {
        if (!Open || dir == 0 || _entries.Count == 0)
        {
            return false;
        }

        Highlight = (((Highlight + dir) % _entries.Count) + _entries.Count) % _entries.Count;
        Scroll();
        return true;
    }

    /// <summary>Closes the list and hands back the entry the cursor was on. The caller applies it
    /// through <see cref="Select"/>, or refuses it and leaves the pick alone. Null when the list
    /// was not open.</summary>
    public int? Confirm()
    {
        if (!Open)
        {
            return null;
        }

        Open = false;
        int at = Highlight;
        Highlight = Selected;
        Scroll();
        return at;
    }

    /// <summary>Closes the list, changing nothing: the back press.</summary>
    public bool Collapse()
    {
        if (!Open)
        {
            return false;
        }

        Open = false;
        Highlight = Selected;
        Scroll();
        return true;
    }

    /// <summary>Moves the open list's window so <paramref name="first"/> heads it, clamped, and
    /// pulls the highlight inside it: the pointer's wheel and thumb move the window rather than
    /// the cursor. Nothing happens on a closed list or one that fits its window.</summary>
    public bool ScrollTo(int first)
    {
        if (!Open || !Scrolls)
        {
            return false;
        }

        int was = First;
        First = Math.Clamp(first, 0, _entries.Count - RowsDisplayed);
        Highlight = Math.Clamp(Highlight, First, First + RowsDisplayed - 1);
        return First != was;
    }

    /// <summary>The entry a closed step in <paramref name="dir"/> would land on, wrapping. A
    /// candidate, not a move: the caller decides whether that pick is allowed.</summary>
    public int Next(int dir)
    {
        if (_entries.Count == 0)
        {
            return 0;
        }

        return (((Selected + dir) % _entries.Count) + _entries.Count) % _entries.Count;
    }

    // Pulls the window over the cursor: it moves only when the cursor has left it, so a list
    // scrolls at its edges rather than keeping the cursor centred.
    private void Scroll()
    {
        if (_entries.Count <= RowsDisplayed)
        {
            First = 0;
            return;
        }

        First = Math.Clamp(First, Highlight - RowsDisplayed + 1, Highlight);
        First = Math.Clamp(First, 0, _entries.Count - RowsDisplayed);
    }
}
