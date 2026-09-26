using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;

namespace CSVM.UI.Menu.Original;

/// <summary>The layout-widget readings more than one Original screen module needs. They cover the
/// slot number a numbered widget key carries and where a section's background pane lands on the
/// board. They also cover a strip's one-frame size and the rows of a screen drawn by the shared
/// board component. The campaign module and the shell's own per-seat aircraft screen build those
/// rows off an <see cref="ICampaignPage"/>. Held here because a module is a sealed class of its
/// own, so a rule two of them follow can live in neither. The open-dropdown rule is the other such
/// reading, in <c>OriginalDropList.cs</c>.</summary>
internal static class OriginalWidgets
{
    // A page row that is neither a button nor a field: a roster name, a mission row, a scrap.
    internal const string RowKeyPrefix = "ROW:";

    // A page row carrying a drop-down field.
    internal const string FieldKeyPrefix = "FIELD:";

    // One entry of an open drop-down list, keyed by its index into the list.
    internal const string EntryKeyPrefix = "ENTRY:";

    // A plaque whose art the measurer cannot see. The briefing's brief_button1 is mission art
    // under extracted/rimage, 196x32 in the shipped file. Every other plaque is a rof strip.
    private const float BriefPlaqueWidth = 196f;
    private const float BriefPlaqueHeight = 32f;

    // The roster's CONTINUE strip, the size a plaque falls back to where its own file is missing.
    private const float FallbackPlaqueWidth = 113f;
    private const float FallbackPlaqueHeight = 34f;

    // A capture with no authored region of its own stands in the page's forced region, which is
    // also the rectangle it is drawn in.
    private const float CaptureRegionWidth = 164f;
    private const float CaptureRegionHeight = 123f;

    // The name box's own height where the row carries none.
    private const float FallbackFieldHeight = 20f;
    // A slot number a keyed widget carries after a shared prefix, or null for a key from another
    // family or a plain key with none. The ammunition, pylon, zone and paint fields are numbered
    // this way.
    internal static int? Indexed(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(key.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;
    }

    // Where a section's pane lands on the board. Art smaller than the board is centred rather than
    // left at its authored corner. PLANENAME.SCRIPT initializes pn_p_background with relative = 1.
    // It then sets the screen's own location to ((getresx() - its width) / 2, (getresy() - its
    // height) / 2). The messagebox and the loadout section do the same, the 410x300 pane landing
    // on the 195,150 the shots measure. A pane that fills the board centres onto its own corner,
    // and one authored away from the corner keeps it.
    internal static (float X, float Y) PaneOrigin(
        MenuLayoutScreen screen, string key, Func<string, (int Width, int Height)?> measure)
    {
        if (screen.Widget(key) is not { Art.Count: > 0 } pane)
        {
            return (0f, 0f);
        }

        float x = pane.Int("X");
        float y = pane.Int("Y");
        if (x != 0f || y != 0f || measure(pane.Art[0]) is not { } size)
        {
            return (x, y);
        }

        return (
            Math.Max(0f, (float)Math.Floor((BoardFit.AuthoredWidth - size.Width) / 2f)),
            Math.Max(0f, (float)Math.Floor((BoardFit.AuthoredHeight - size.Height) / 2f)));
    }

    internal static void AddPane(
        MenuLayoutScreen screen, List<BoardPicture> pictures, string key, Func<string, (int Width, int Height)?> measure)
    {
        if (screen.Widget(key) is { Art.Count: > 0 } pane)
        {
            var at = PaneOrigin(screen, key, measure);
            pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, pane.Art[0], Math.Max(1, pane.Frames)), at.X, at.Y));
        }
    }

    // A strip's one-frame size from the measurer, or the fallback when the file is not there.
    internal static (float Width, float Height) StripSize(
        BoardArt? art, Func<string, (int Width, int Height)?> measure, float fallbackWidth, float fallbackHeight)
    {
        if (art == null || measure(art.Name) is not { } size)
        {
            return (fallbackWidth, fallbackHeight);
        }

        return (size.Width, (float)Math.Floor(size.Height / (float)Math.Max(1, art.Frames)));
    }

    // A plaque's one-frame size. A rof bitmap is measured as a strip, and the briefing's rimage
    // plaque takes its shipped size. The roster's CONTINUE strip is the fallback.
    internal static (float Width, float Height) PlaqueSizeOf(BoardArt art, Func<string, (int Width, int Height)?> measure)
    {
        if (art.Library == BoardArtLibrary.Rimage)
        {
            return (BriefPlaqueWidth, BriefPlaqueHeight);
        }

        return StripSize(art, measure, FallbackPlaqueWidth, FallbackPlaqueHeight);
    }

    // Whether a row is one entry of an open list, which takes the highlight rather than the focus.
    internal static bool HoverOnly(OriginalRow row) => row.Key.StartsWith(EntryKeyPrefix, StringComparison.Ordinal);

    // One entry of an open list taking the highlight. The list's own cursor moves onto it. The
    // focus stays on the field the list hangs from.
    internal static void Highlight(CampaignCombo? combo, string key)
    {
        if (combo is { } open && Entry(key) is { } entry)
        {
            open.Move(entry - open.Highlight);
        }
    }

    // The list index a row key names, or null where the key is not an entry's.
    internal static int? Entry(string key) =>
        key.StartsWith(EntryKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(EntryKeyPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int entry)
            ? entry
            : null;

    // The rows of a screen drawn by the shared board component. One row stands per page row, at
    // the rectangle the component draws it at. That is a button's slot and strip, a field's box,
    // or a list row's slot. A row with no rectangle keeps its index unseen and unhit, and a row
    // <paramref name="enabled"/> refuses is disabled. Then come <paramref name="openCombo"/>'s
    // entries, where a field of the page stands open.
    internal static void PageRows(
        ICampaignPage page, CampaignCombo? openCombo, Func<int, bool> enabled, CampaignLayout layout,
        Func<string, (int Width, int Height)?> measure, List<OriginalRow> rows)
    {
        var screen = page.Screen;
        int listIndex = 0;
        for (int row = 0; row < page.RowCount; row++)
        {
            string key = RowKey(page, row);
            bool live = enabled(row);
            if (page.Combo(row) is { } combo)
            {
                rows.Add(new OriginalRow(key, combo.Text, OriginalRowKind.Dropdown, combo.X, combo.Y, combo.Width,
                    CampaignBoards.ComboFieldHeight, live, 0, null));
                continue;
            }

            var reference = page.Button(row);
            if (reference.Button != BoardButton.None)
            {
                if (CampaignBoards.SlotOf(screen, reference, layout) is { } slot)
                {
                    var size = PlaqueSizeOf(slot.Art, measure);
                    rows.Add(new OriginalRow(key, page.RowText(row), OriginalRowKind.Button, slot.X, slot.Y, size.Width, size.Height, live, 0, slot.Art));
                }
                else
                {
                    rows.Add(new OriginalRow(key, page.RowText(row), OriginalRowKind.Button, 0f, 0f, 0f, 0f, false, 0, null, false));
                }

                continue;
            }

            // A focusable row with no rectangle (a mission row scrolled out of its window) keeps
            // its place for the keyboard, unseen and unhit.
            var box = ListRowBox(page, row, listIndex++, layout, measure);
            var kind = screen == CampaignScreen.Roster && row == 0 ? OriginalRowKind.TextField : OriginalRowKind.ListRow;
            rows.Add(box is { } b
                ? new OriginalRow(key, page.RowText(row), kind, b.X, b.Y, b.Width, b.Height, live, 0, null)
                : new OriginalRow(key, page.RowText(row), kind, 0f, 0f, 0f, 0f, live, 0, null, false));
        }

        if (openCombo is { } open)
        {
            float top = open.Y + CampaignBoards.ComboFieldHeight;
            for (int seen = 0; seen < open.Visible; seen++)
            {
                int entry = open.First + seen;
                if (entry >= open.Entries.Count)
                {
                    break;
                }

                rows.Add(new OriginalRow(EntryKeyPrefix + entry.ToString(CultureInfo.InvariantCulture), open.Entries[entry],
                    OriginalRowKind.ListRow, open.X, top + (seen * open.RowHeight), open.Width, open.RowHeight, true, 0, null));
            }
        }
    }

    // A row key for a page row. It is the authored button it presses, with its crew slot.
    // Otherwise it is the kind of row with its index.
    private static string RowKey(ICampaignPage page, int row)
    {
        var reference = page.Button(row);
        if (reference.Button != BoardButton.None)
        {
            return reference.Slot > 0
                ? reference.Button + ":" + reference.Slot.ToString(CultureInfo.InvariantCulture)
                : reference.Button.ToString();
        }

        return (page.Combo(row) != null ? FieldKeyPrefix : RowKeyPrefix) + row.ToString(CultureInfo.InvariantCulture);
    }

    // Where a list or text row sits. The roster's box and its list rows take the layout's own item
    // height, and a mission row sits inside the table of contents' window. On the book a scrap
    // takes its authored region, or its picture's bounds where the row authors none. Any other
    // book row takes whatever art it draws itself with, at that strip's frame. The book's answer
    // is per row and not per scrap. A row the page offers and the pointer cannot reach is a
    // control the player has lost.
    private static (float X, float Y, float Width, float Height)? ListRowBox(
        ICampaignPage page, int row, int listIndex, CampaignLayout layout, Func<string, (int Width, int Height)?> measure)
    {
        switch (page)
        {
            case CampaignRosterPage:
                {
                    var (x, y, width) = CampaignBoards.TextSlot(CampaignScreen.Roster, listIndex, layout);
                    float height = row == 0
                        ? layout.Int(CampaignLayout.RosterSection, "CM_E_NAME", "Height", (int)FallbackFieldHeight)
                        : layout.Int(CampaignLayout.RosterSection, "CM_L_PLAYERS", "ItemHeight", 20);
                    return (x, y, width, height);
                }

            case CampaignPreviousMissionsPage contents:
                return contents.RowBox(row);
            case CampaignScrapbookPage book:
                if (book.ScrapOf(row) is { } scrap)
                {
                    return ScrapBox(scrap, measure);
                }

                if (book.ArtOf(row) is not { } drawn)
                {
                    return null;
                }

                var frame = PlaqueSizeOf(drawn.Art, measure);
                return (drawn.X, drawn.Y, frame.Width, frame.Height);
            default:
                return null;
        }
    }

    // A scrap's clickable region: the one SCRAPBOOK.CSV authors, else the fixed region a capture
    // stands in, else the shipped image's own bounds. Null where the file is not there to measure,
    // which leaves the row keyboard-only rather than hit at a guessed size.
    private static (float X, float Y, float Width, float Height)? ScrapBox(
        ScrapbookScrap scrap, Func<string, (int Width, int Height)?> measure)
    {
        if (scrap.Region is { } region)
        {
            return region;
        }

        if (scrap.IsCapture)
        {
            return (scrap.X, scrap.Y, CaptureRegionWidth, CaptureRegionHeight);
        }

        if (measure($"SCRAPBOOK/{scrap.FileName}") is { } size)
        {
            return (scrap.X, scrap.Y, size.Width, size.Height);
        }

        return null;
    }
}
