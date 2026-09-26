using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.UI.Boards;

namespace CSVM.UI.Menu.Original;

/// <summary>How a page answers an art name with one frame's pixel size, the fallback taken where
/// the file cannot be measured.</summary>
internal delegate (float Width, float Height) StripSizer(BoardArt? art, float fallbackWidth, float fallbackHeight);

/// <summary>
/// The window rule every open dropdown of the Original presentation follows, shared by the Instant
/// Action screen, the loadout screen and the two Preferences option pages. An open list shows the
/// authored <c>TotalDisplayed</c> rows and no more; every item is built so the pointer and the walk
/// address it by index, the ones outside the window hidden rather than dropped; and a list longer
/// than its window hangs the widget's own arrows and thumb inside its own right edge, the rows
/// giving up that column. A page describes its list as an open drop list and takes the rest from
/// here. The authored numbers are in <c>docs/org/menu-inventory.md</c>.
/// </summary>
internal static class OriginalDropLists
{
    // The suffixes an open list's two chrome rows carry, so a press on an arrow is told from a
    // press on an item by the key alone.
    internal const string UpSuffix = "up";
    internal const string DownSuffix = "down";

    // The scroll thumb's height where its art cannot be measured, the shipped bar's own. The
    // measured tile's height is also the floor a proportional thumb never drops below, since a
    // shorter one stops reading as a grip.
    private const float FallbackScrollThumbHeight = 11f;

    // An arrow's size where its own art cannot be measured, matching the Instant Action screen's
    // own fallbacks, since the arrows are that screen's strips wherever a page borrows them.
    private const float FallbackArrowWidth = 15f;
    private const float FallbackArrowHeight = 14f;

    // The n-th art a row names as a strip; arrows and radios carry their frame count in the row,
    // the dropdown arrows are the same four-frame strips the page buttons draw.
    internal static BoardArt? StripArt(IReadOnlyList<string> art, int index, int frames = 4) =>
        index < art.Count && art[index].Length > 0 ? new BoardArt(BoardArtLibrary.Ui, art[index], Math.Max(1, frames)) : null;

    // An open dropdown's list off its own widget: the box it hangs under, the authored
    // TotalDisplayed as its window (never more rows than the list has items, never none) and the
    // D row's scroll art, the thumb at art 0 and the arrows at 1 and 2. A page with no widget of
    // its own shows every item.
    internal static OpenDropList Over(
        string key, MenuLayoutWidget? widget, IReadOnlyList<string> items,
        (float X, float Y, float Width, float Height) box, Func<int, bool>? allowed = null) =>
        new(key, items, box.X, box.Y, box.Width, box.Height,
            Math.Clamp(widget?.Int("TotalDisplayed", items.Count) ?? items.Count, 1, Math.Max(1, items.Count)),
            widget == null ? null : StripArt(widget.Art, 1),
            widget == null ? null : StripArt(widget.Art, 2),
            widget == null ? null : StripArt(widget.Art, 0, 1),
            allowed);

    // The window's first row: pulled onto the focused item when a walk has left the window behind,
    // then clamped, since the window follows the focus.
    internal static int Top(OpenDropList drop, int top, int focused)
    {
        if (focused >= 0 && focused < drop.Count)
        {
            if (focused < top)
            {
                top = focused;
            }
            else if (focused >= top + drop.Window)
            {
                top = focused - drop.Window + 1;
            }
        }

        return Math.Clamp(top, 0, drop.LastTop);
    }

    // The item index an open list's row carries, or -1 for its arrows and for any other key.
    internal static int IndexOf(string key)
    {
        int colon = key.LastIndexOf(':');
        return colon > 0 && int.TryParse(
            key[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : -1;
    }

    // An open list's rows under its box: every item keyed <key>:<index>, so a pose or a walk picks
    // one whatever the window shows, and the ones outside the window built but hidden, since the
    // rows are the shell's hit-test surface and a dropped row would let a pointer hit what it
    // cannot see. A scrolling list adds its two arrows in the column the rows gave up, each live
    // only towards more list.
    internal static void AddRows(OpenDropList drop, int top, List<OriginalRow> rows, StripSizer stripSize)
    {
        var upSize = stripSize(drop.Up, FallbackArrowWidth, FallbackArrowHeight);
        var downSize = stripSize(drop.Down, FallbackArrowWidth, FallbackArrowHeight);
        float column = drop.Scrolls ? upSize.Width : 0f;
        for (int i = 0; i < drop.Count; i++)
        {
            bool visible = i >= top && i < top + drop.Window;
            rows.Add(new OriginalRow($"{drop.Key}:{i}", drop.Items[i], OriginalRowKind.ListRow,
                drop.X, drop.Y + (drop.ItemHeight * (i - top + 1)), drop.Width - column, drop.ItemHeight,
                drop.Allowed?.Invoke(i) ?? true, 0, null, visible));
        }

        if (!drop.Scrolls)
        {
            return;
        }

        rows.Add(new OriginalRow(drop.Key + ":" + UpSuffix, string.Empty, OriginalRowKind.Button,
            drop.X + drop.Width - upSize.Width, drop.Y + drop.ItemHeight, upSize.Width, upSize.Height,
            top > 0, 0, drop.Up));
        rows.Add(new OriginalRow(drop.Key + ":" + DownSuffix, string.Empty, OriginalRowKind.Button,
            drop.X + drop.Width - downSize.Width, drop.Y + (drop.ItemHeight * (drop.Window + 1)) - downSize.Height,
            downSize.Width, downSize.Height, top + drop.Window < drop.Count, 0, drop.Down));
    }

    // An open list's window for the pointer's wheel and its thumb drag, the thumb on the track
    // between the two arrows inside the right edge and as long as the share of the list the window
    // shows; null while the items fit the authored window.
    internal static ListWindow? Window(OpenDropList drop, int top, StripSizer stripSize)
    {
        if (!drop.Scrolls)
        {
            return null;
        }

        var upSize = stripSize(drop.Up, FallbackArrowWidth, FallbackArrowHeight);
        var downSize = stripSize(drop.Down, FallbackArrowWidth, FallbackArrowHeight);
        var thumb = stripSize(drop.Thumb, upSize.Width, FallbackScrollThumbHeight);
        float y = drop.Y + drop.ItemHeight;
        float height = drop.Window * drop.ItemHeight;
        float trackHeight = height - upSize.Height - downSize.Height;
        float thumbHeight = ListWindow.ThumbHeightFor(trackHeight, drop.Window, drop.Count, thumb.Height);
        int first = Math.Clamp(top, 0, drop.LastTop);
        return new ListWindow(
            drop.X, y, drop.Width, height,
            drop.X + drop.Width - upSize.Width,
            ListWindow.ThumbYFor(y + upSize.Height, trackHeight, thumbHeight, first, drop.LastTop),
            thumb.Width, thumbHeight,
            y + upSize.Height, trackHeight, drop.Count, drop.Window, first);
    }

    // Puts an open list's window at top and answers where it landed; a focused item the move would
    // hide is pulled to the window's nearer edge, since the window otherwise follows the focus
    // straight back.
    internal static int Scroll(OpenDropList drop, int top, ref int focused)
    {
        int first = Math.Clamp(top, 0, drop.LastTop);
        if (focused >= 0 && focused < drop.Count)
        {
            focused = Math.Clamp(focused, first, first + drop.Window - 1);
        }

        return first;
    }
}

/// <summary>One open dropdown as a page windows it: the key its rows are named under, its items,
/// the authored box the list hangs beneath, how many rows the window shows, the widget's own scroll
/// art, and which items can be picked (every one where a page says nothing).</summary>
internal sealed record OpenDropList(
    string Key, IReadOnlyList<string> Items, float X, float Y, float Width, float ItemHeight,
    int Window, BoardArt? Up, BoardArt? Down, BoardArt? Thumb, Func<int, bool>? Allowed)
{
    public int Count => Items.Count;

    public bool Scrolls => Count > Window;

    public int LastTop => Math.Max(0, Count - Window);
}
