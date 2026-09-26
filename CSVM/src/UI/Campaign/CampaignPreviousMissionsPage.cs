using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Menu;

namespace CSVM.UI.Campaign;

/// <summary>
/// The book's results page (scrapbook spread 1): the outcome line, the four rows the original
/// draws, and the two tabs. It is computed from one mission's record,
/// <c>docs/org/debrief.md#the-screen-is-the-scrapbook</c>. Row titles and the outcome text are
/// literal strings rather than read off <c>ui_strings.json</c> at runtime, following
/// <see cref="Screens.IaWrapupBoard"/>'s own precedent. Positions are <c>[@ScrapBook@]</c>'s
/// <c>SB_T_*</c> and <c>SB_KILL*</c> rows, read through the <see cref="CampaignLayout"/> a caller
/// hands in with the shipped values as the fallback. Wired into <see cref="CampaignFlow"/> as
/// <see cref="CampaignScreen.Scrapbook"/>'s <see cref="CampaignScrapbookPage"/>, which also draws
/// spread 1's shipped scraps alongside this class's rows and stamps
/// (<see cref="ScrapbookComposition"/>).
/// </summary>
public static class CampaignScrapbookResults
{
    // LAYOUT.CSV [@ScrapBook@]: STATTITLEX/STATX are the title and value columns; SLINE0/SLINE1
    // are the outcome and heading rows; SLINE2/SLINE4/SLINE5/SLINE6 are the four drawn rows.
    // SLINE3, the cut Rockets Expended row, is not among them.
    private const string Section = CampaignLayout.BookSection;
    private const float TitleX = 417f;
    private const float ValueX = 642f;
    private const float OutcomeY = 339f;
    private const float HeadingY = 369f;
    private const float TimeY = 412f;
    private const float HitsY = 436f;
    private const float CashY = 460f;
    private const float PlanesY = 484f;

    // AB14I, the row font every SB_T_* widget in [@ScrapBook@] carries.
    private const float RowFont = 14f;

    // KTEXTW, the SB_KILLTEXT box width.
    private const float StampTextWidth = 15f;

    private const string MissionCompletedText = "Mission Completed"; // langui 1213
    private const string MissionFailedText = "Mission Failed"; // langui 1214
    private const string NotYetFlownText = "Not yet flown"; // langui 1219
    private const string ResultsHeadingText = "Mission Results"; // langui 1202
    private const string RunTimeTitle = "Run Time"; // langui 1203
    private const string GunHitRatioTitle = "Gun Hit Ratio"; // langui 1205
    private const string CashEarnedTitle = "Cash Earned"; // langui 1206
    private const string PlanesDownedTitle = "Overall Planes Downed"; // langui 1207

    // SB_killMARKERcombined.png, the MARKER macro's own spelling: 22 frames of 70x100, the eleven
    // airframes then the same eleven starred.
    private static readonly BoardArt KillMarker = new(BoardArtLibrary.Ui, "SB_killMARKERcombined.png", 22);

    // SB_KILL0..SB_KILL10's top-left, LAYOUT.CSV [@ScrapBook@]. Not in reading order.
    private static readonly (float X, float Y)[] StampSlots =
    {
        (560f, 109f), (467f, 93f), (604f, 173f), (604f, 50f), (520f, 42f),
        (679f, 153f), (540f, 195f), (682f, 46f), (476f, 184f), (416f, 154f), (417f, 43f),
    };

    // SB_KILLTEXT0..SB_KILLTEXT10, the count drawn over each stamp.
    private static readonly (float X, float Y)[] StampTextSlots =
    {
        (588f, 128f), (494f, 114f), (632f, 195f), (633f, 72f), (549f, 64f),
        (707f, 176f), (568f, 218f), (709f, 69f), (504f, 207f), (445f, 177f), (444f, 66f),
    };

    /// <summary>Best to Date (langui 1159) is the merged half at <c>+0x54</c>. Most Recent
    /// (langui 1160) is the attempt half at <c>+0x00</c>. See <c>docs/formats/saved-games.md</c>,
    /// "The mission-result array".</summary>
    public static string TabTitle(bool bestToDate) => bestToDate ? "Best to Date" : "Most Recent";

    /// <summary>Whether the outcome line reads Mission Completed: the selected half's mask
    /// carries the primary bit. The original's <c>0x0040a7e6</c> reads it from <c>+0x00</c> for
    /// Most Recent and from <c>+0x24</c> for Best to Date. Both hold the bit under the same
    /// condition, so <see cref="MissionRun.BestAttemptMask"/> is not read here, which keeps a
    /// profile written before that field reading correctly.</summary>
    public static bool Won(MissionResult result, bool bestToDate) =>
        ((bestToDate ? result.Best : result.Latest).CompletedMask
            & CampaignProgression.PrimaryObjectiveMask) != 0;

    /// <summary>The sum of both per-airframe kill arrays over their eleven slots, truncated to
    /// sixteen bits the way <c>0x0040a8df</c> does. There is no stored total field, so this always
    /// agrees with whatever the stamps draw.</summary>
    public static int PlanesDowned(MissionResult result, bool bestToDate)
    {
        var run = bestToDate ? result.Best : result.Latest;
        int total = 0;
        for (int i = 0; i < CampaignProgression.AirframeCount; i++)
        {
            total += run.Kills[i] + run.AceKills[i];
        }

        return total & 0xffff;
    }

    /// <summary>The filled kill-stamp slots, densely from slot 0. The plain tally's airframes come
    /// in ascending index order, skipping zeros, then the ace tally's the same way, stopping at
    /// eleven. See <c>docs/org/debrief.md#the-stamps-and-the-total</c>. The same airframe can fill
    /// two slots, once plain and once starred.</summary>
    public static IReadOnlyList<KillStamp> Stamps(MissionResult result, bool bestToDate)
    {
        var run = bestToDate ? result.Best : result.Latest;
        var stamps = new List<KillStamp>();
        for (int i = 0; i < CampaignProgression.AirframeCount && stamps.Count < CampaignProgression.AirframeCount; i++)
        {
            if (run.Kills[i] > 0)
            {
                stamps.Add(new KillStamp(stamps.Count, i, run.Kills[i]));
            }
        }

        for (int i = 0; i < CampaignProgression.AirframeCount && stamps.Count < CampaignProgression.AirframeCount; i++)
        {
            if (run.AceKills[i] > 0)
            {
                stamps.Add(new KillStamp(stamps.Count, i + CampaignProgression.AirframeCount, run.AceKills[i]));
            }
        }

        return stamps;
    }

    /// <summary>The stamp art, one <c>BoardPicture</c> per filled slot at its <c>SB_KILL</c>
    /// row. ⚠ The eleven slots are not in reading order: <c>SB_KILL1</c> sits left of and above
    /// <c>SB_KILL0</c>.</summary>
    public static IReadOnlyList<BoardPicture> StampPictures(
        MissionResult result, bool bestToDate, CampaignLayout? layout = null)
    {
        layout ??= CampaignLayout.Fallback;
        var pictures = new List<BoardPicture>();
        foreach (var stamp in Stamps(result, bestToDate))
        {
            string key = $"SB_KILL{stamp.Slot}";
            var (x, y) = layout.At(Section, key, StampSlots[stamp.Slot].X, StampSlots[stamp.Slot].Y);
            pictures.Add(new BoardPicture(layout.Art(Section, key, KillMarker), x, y, stamp.Frame));
        }

        return pictures;
    }

    /// <summary>The kill count drawn over each filled stamp, at its <c>SB_KILLTEXT</c> row.</summary>
    public static IReadOnlyList<BoardLine> StampLabels(
        MissionResult result, bool bestToDate, CampaignLayout? layout = null)
    {
        layout ??= CampaignLayout.Fallback;
        var lines = new List<BoardLine>();
        foreach (var stamp in Stamps(result, bestToDate))
        {
            var (x, y, width) = layout.Box(
                Section, $"SB_KILLTEXT{stamp.Slot}",
                StampTextSlots[stamp.Slot].X, StampTextSlots[stamp.Slot].Y, StampTextWidth);
            lines.Add(new BoardLine(
                stamp.Count.ToString(CultureInfo.InvariantCulture), x, y, width, RowFont, BoardInk.Row));
        }

        return lines;
    }

    /// <summary>The results card's own placeholder for a page whose mission has no recorded
    /// attempt yet: langui 1219, at the outcome line's own row.</summary>
    public static BoardLine NotYetFlown(CampaignLayout? layout = null)
    {
        var (x, y) = (layout ?? CampaignLayout.Fallback).At(Section, "SB_T_COMPLETETITLE", TitleX, OutcomeY);
        return new BoardLine(NotYetFlownText, x, y, 0, RowFont, BoardInk.Heading, Italic: true);
    }

    /// <summary>The outcome line, the heading and the four drawn rows (title then value), each at
    /// its own <c>SB_T_*</c> row. Time is <c>mm:ss</c> off milliseconds, truncated the way
    /// <c>FUN_00419630</c> writes it. The hit ratio is hits over shots as a percentage. It
    /// truncates toward zero the way the screen's own <c>ftol</c> call does, never rounding.</summary>
    public static IReadOnlyList<BoardLine> Rows(MissionResult result, bool bestToDate, CampaignLayout? layout = null)
    {
        layout ??= CampaignLayout.Fallback;
        var run = bestToDate ? result.Best : result.Latest;
        int totalSeconds = run.TimeMs / 1000;
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        int hitRatio = run.Shots <= 0 ? 0 : run.Hits * 100 / run.Shots;

        var lines = new List<BoardLine>
        {
            Heading(layout, "SB_T_COMPLETETITLE", Won(result, bestToDate) ? MissionCompletedText : MissionFailedText, OutcomeY),
            Heading(layout, "SB_T_RESULTSTITLE", ResultsHeadingText, HeadingY),
        };
        AddRow(lines, layout, "SB_T_TIMETITLE", RunTimeTitle, "SB_T_TIME", $"{minutes:00}:{seconds:00}", TimeY);
        AddRow(lines, layout, "SB_T_HITTITLE", GunHitRatioTitle, "SB_T_HIT", $"{hitRatio}%", HitsY);
        AddRow(lines, layout, "SB_T_CASHTITLE", CashEarnedTitle, "SB_T_CASH", $"${run.Money}", CashY);
        AddRow(lines, layout, "SB_T_PLANESTITLE", PlanesDownedTitle, "SB_T_PLANES",
            PlanesDowned(result, bestToDate).ToString(CultureInfo.InvariantCulture), PlanesY);
        return lines;
    }

    private static BoardLine Heading(CampaignLayout layout, string key, string text, float fallbackY)
    {
        var (x, y) = layout.At(Section, key, TitleX, fallbackY);
        return new BoardLine(text, x, y, 0, RowFont, BoardInk.Heading, Italic: true);
    }

    // One drawn row: its title at the title column's row and its value at the value column's.
    private static void AddRow(
        List<BoardLine> lines, CampaignLayout layout, string titleKey, string title, string valueKey, string value, float fallbackY)
    {
        var (titleX, titleY) = layout.At(Section, titleKey, TitleX, fallbackY);
        var (valueX, valueY) = layout.At(Section, valueKey, ValueX, fallbackY);
        lines.Add(new BoardLine(title, titleX, titleY, 0, RowFont, BoardInk.Row, Italic: true));
        lines.Add(new BoardLine(value, valueX, valueY, 0, RowFont, BoardInk.Row, Italic: true));
    }

    /// <summary>One filled kill-stamp slot. The <paramref name="Slot"/> is the <c>SB_KILL</c> and
    /// <c>SB_KILLTEXT</c> ordinal, 0-10 and not in reading order. The <paramref name="Frame"/> is
    /// the strip frame: the airframe index, or that plus eleven for the starred/ace variant. The
    /// <paramref name="Count"/> is the number drawn over it.</summary>
    public readonly record struct KillStamp(int Slot, int Frame, int Count);
}

/// <summary>
/// The scrapbook's table of contents, <c>SCRAPBOOK_TOC.SCRIPT</c>, opens at the career row, then
/// carries one 80-pixel row per mission the profile has completed. Each row is an aircraft
/// silhouette beside the mission's short name, the area it was flown over and the plane that flew
/// it. Four rows show at once, with the listbox's scrollbar beside them, then VIEW SELECTED,
/// REPLAY MISSION and RETURN TO CABIN. A confirm picks a row, and a second confirm is REPLAY
/// MISSION's press, which the career row never offers, so there it views instead. The secondary press is VIEW SELECTED on the cursor's row.
/// The forward page tab is this screen's own, the original leaving the contents with no arrow a
/// pad can turn forward by.
/// </summary>
public sealed class CampaignPreviousMissionsPage : CampaignPage
{
    // SBTOC_L_TOCList, the listbox row of LAYOUT.CSV's [@ScrapBook_TOC@], as the fallback. It
    // carries X, Y, wrap width, the height of ONE row (not of the widget), and how many rows are
    // on screen at once.
    private const string Section = CampaignLayout.ContentsSection;
    private const float FallbackListX = 420f;
    private const float FallbackListY = 140f;
    private const float FallbackListWidth = 325f;
    private const int FallbackRowHeight = 80;
    private const int FallbackVisibleRows = 4;

    // The row sub-script's own columns. The icon pane sits two pixels in, and its text column
    // starts a further 20 past the pane's width. The three text rows sit +10, +30 and +50 down.
    private const float IconInset = 2f;
    private const float IconWidth = 80f;
    private const float TextGap = 20f;
    private const float FirstLineY = 10f;
    private const float LinePitch = 20f;

    // The row face. Every row is drawn in @globals@gfont3d, whose size is in no layout row, so
    // this is measured off the reference shot; docs/org/campaign-board.md carries the measurement.
    private const float RowFont = 17f;

    // SBTOC_T_CHARACTER and SBTOC_T_MISSIONS: both 300 wide from x 425, justify 2, the first at
    // y 64 and the second at 114. Neither names a face either, so both sizes are measured too.
    private const float HeaderX = 425f;
    private const float HeaderWidth = 300f;
    private const float NameY = 64f;
    private const float NameFont = 14f;
    private const float HeadingY = 114f;
    private const float HeadingFont = 11f;

    // The picked row's wash and its outline, the sub-script's own setpencolor arguments
    // 0x80f2e7b7 and 0xffdd9017. The focused row takes the same outline over the half-strength
    // wash the original draws under a mouse pointer.
    private const byte WashRed = 0xf2;
    private const byte WashGreen = 0xe7;
    private const byte WashBlue = 0xb7;
    private const byte EdgeRed = 0xdd;
    private const byte EdgeGreen = 0x90;
    private const byte EdgeBlue = 0x17;
    private const float PickedWash = 0x80 / 255f;
    private const float FocusWash = 0x40 / 255f;

    // The scrollbar column, measured off Campaign CAP-41 Previous Mission 2.png. The strip stands
    // in the list's own last 16 pixels, with an 11-pixel arrow at each end of the window. Its
    // track is the list's KF colour, 0xff282418.
    private const float ScrollX = 730f;
    private const float ScrollWidth = 16f;
    private const float ScrollButton = 11f;
    private const byte TrackRed = 0x28;
    private const byte TrackGreen = 0x24;
    private const byte TrackBlue = 0x18;

    // The career row's three lines, the fallbacks under langui 1217, 1218 and 511
    // (docs/org/debrief.md, "The contents list is the campaign position").
    private const string CareerTitle = "Starting My Career";
    private const string CareerArea = "Above the clouds";
    private const string CareerPlane = "Gypsy Magic";

    // The card fan past the eleven airframes in FC_PlaneIcons.png, which uiData 2409 answers for
    // ordinal 0.
    private const int CareerIconFrame = CampaignProgression.AirframeCount;

    // fc_planeicons.png as the row sub-script mounts it: 12 frames of 80x80. The eleven airframes
    // come in id order, then the card fan the not-yet-started career row takes.
    private static readonly BoardArt PlaneIcons = new(BoardArtLibrary.Ui, "FC_PlaneIcons.png", 12);

    // The listbox's <SLIDER>, <UP> and <DOWN>: a 16x11 thumb and two four-frame 16x11 strips, the
    // fallbacks under the L row's own three art columns.
    private static readonly BoardArt FallbackScrollThumb = new(BoardArtLibrary.Ui, "CM_B_ScrollBar.png");
    private static readonly BoardArt FallbackScrollUp = new(BoardArtLibrary.Ui, "CM_B_ScrollUp.png", 4);
    private static readonly BoardArt FallbackScrollDown = new(BoardArtLibrary.Ui, "CM_B_ScrollDown.png", 4);

    // The list's geometry and art, resolved once from the flow's layout.
    private readonly float _listX;
    private readonly float _listY;
    private readonly float _listWidth;
    private readonly float _rowHeight;
    private readonly int _visibleRows;
    private readonly BoardArt _scrollThumb;
    private readonly BoardArt _scrollUp;
    private readonly BoardArt _scrollDown;

    // The list row a press marks as the one REPLAY MISSION and VIEW SELECTED act on. It is -1
    // until the player has picked one. The two buttons then fall back to the first finished
    // mission, so a press before ever selecting still does something sensible.
    private int _selected = -1;

    // The first row of the four the window shows. It is kept across visits, so paging away from
    // the list and back does not jump it to the top.
    private int _top;

    /// <summary>Binds the page to its flow and reads the list's row off the flow's layout.</summary>
    public CampaignPreviousMissionsPage(CampaignFlow flow)
        : base(flow)
    {
        var layout = flow.Layout;
        const string list = "SBTOC_L_TOCList";
        (_listX, _listY, _listWidth) = layout.Box(Section, list, FallbackListX, FallbackListY, FallbackListWidth);
        _rowHeight = layout.Int(Section, list, "ItemHeight", FallbackRowHeight);
        _visibleRows = Math.Max(1, layout.Int(Section, list, "TotalDisplayed", FallbackVisibleRows));
        _scrollThumb = layout.Art(Section, list, FallbackScrollThumb, "Slider");
        _scrollUp = layout.Art(Section, list, FallbackScrollUp, "UpArrow");
        _scrollDown = layout.Art(Section, list, FallbackScrollDown, "DownArrow");
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.PreviousMissions;

    /// <inheritdoc/>
    public override string Title => "PREVIOUS MISSIONS";

    /// <summary>The one screen with a secondary press, so it says what X does rather than leaving
    /// the shortcut to be discovered. Kept short, because the band is centred over the board. A
    /// longer line runs its right end under the CURRENT MISSION bookmark at the top of this
    /// screen.
    /// </summary>
    public override string Footer =>
        "↑↓  Choose       Enter / A  Select       X  View       Esc / B  Back";

    /// <inheritdoc/>
    public override int RowCount => Slots() + Buttons().Count;

    /// <summary>The two header widgets, then the three lines of every row the window shows.</summary>
    public override IReadOnlyList<BoardLine> Captions
    {
        get
        {
            var layout = Flow.Layout;
            int slots = Slots();
            int top = Window(slots);
            var (nameX, nameY, nameWidth) = layout.Box(Section, "SBTOC_T_CHARACTER", HeaderX, NameY, HeaderWidth);
            var (headX, headY, headWidth) = layout.Box(Section, "SBTOC_T_MISSIONS", HeaderX, HeadingY, HeaderWidth);
            var lines = new List<BoardLine>
            {
                new(Flow.Profile?.Name ?? string.Empty, nameX, nameY, nameWidth, NameFont,
                    BoardInk.Heading, Justify: layout.Justify(Section, "SBTOC_T_CHARACTER", BoardJustify.Right)),
                new(Flow.Strings.Text(1131, "Previous Missions"), headX, headY, headWidth,
                    HeadingFont, BoardInk.Heading, Justify: layout.Justify(Section, "SBTOC_T_MISSIONS", BoardJustify.Right)),
            };

            for (int i = 0; i < _visibleRows && top + i < slots; i++)
            {
                float y = _listY + (i * _rowHeight) + FirstLineY;
                foreach (string text in RowLines(top + i))
                {
                    lines.Add(new BoardLine(text, TextX, y, 0f, RowFont, BoardInk.Row, Italic: true));
                    y += LinePitch;
                }
            }

            return lines;
        }
    }

    /// <summary>Each shown row's aircraft silhouette, then the scrollbar's two arrows and its thumb
    /// once the list is longer than its window.</summary>
    public override IReadOnlyList<BoardPicture> Pictures
    {
        get
        {
            int slots = Slots();
            int top = Window(slots);
            var pictures = new List<BoardPicture>();
            for (int i = 0; i < _visibleRows && top + i < slots; i++)
            {
                pictures.Add(new BoardPicture(
                    PlaneIcons, _listX + IconInset, _listY + (i * _rowHeight), Airframe(top + i)));
            }

            if (slots > _visibleRows)
            {
                var (thumbY, thumbHeight) = Thumb(slots, top);
                pictures.Add(new BoardPicture(_scrollUp, ScrollX, _listY, Frame: 1));
                pictures.Add(new BoardPicture(
                    _scrollDown, ScrollX, _listY + WindowHeight - ScrollButton, Frame: 1));
                pictures.Add(new BoardPicture(
                    _scrollThumb, ScrollX, thumbY, Width: ScrollWidth, Height: thumbHeight));
            }

            return pictures;
        }
    }

    /// <summary>The picked row's wash and outline, the focused row's fainter pair, and the
    /// scrollbar's track behind its thumb.</summary>
    public override IReadOnlyList<BoardFill> Fills
    {
        get
        {
            int slots = Slots();
            int top = Window(slots);
            var fills = new List<BoardFill>();
            for (int i = 0; i < _visibleRows && top + i < slots; i++)
            {
                if (Wash(top + i) is not { } wash)
                {
                    continue;
                }

                float y = _listY + (i * _rowHeight);
                fills.Add(new BoardFill(
                    _listX, y, _listWidth, _rowHeight, WashRed, WashGreen, WashBlue, wash));
                fills.Add(new BoardFill(
                    _listX, y, _listWidth, _rowHeight, EdgeRed, EdgeGreen, EdgeBlue, Border: true));
            }

            if (slots > _visibleRows)
            {
                fills.Add(new BoardFill(
                    ScrollX, _listY + ScrollButton, ScrollWidth, WindowHeight - (2f * ScrollButton),
                    TrackRed, TrackGreen, TrackBlue));
            }

            return fills;
        }
    }

    /// <summary>The list as a pointer sees it, its window and the scrollbar's thumb; null while
    /// the missions fit the window and there is no scrollbar.</summary>
    public ListWindow? PointerWindow
    {
        get
        {
            int slots = Slots();
            int top = Window(slots);
            if (slots <= _visibleRows)
            {
                return null;
            }

            var (thumbY, thumbHeight) = Thumb(slots, top);
            return new ListWindow(
                _listX, _listY, _listWidth, WindowHeight,
                ScrollX, thumbY, ScrollWidth, thumbHeight,
                _listY + ScrollButton, WindowHeight - (2f * ScrollButton),
                slots, _visibleRows, top);
        }
    }

    // How tall the window is, which is where the scrollbar's lower arrow sits.
    private float WindowHeight => _visibleRows * _rowHeight;

    // Where a row's text column starts: past the icon pane and its gap.
    private float TextX => _listX + IconWidth + TextGap;

    /// <summary>A list row's rectangle inside the window, for a presentation that hit-tests the
    /// rows. The result is null for a button row, and for a list row scrolled out of the
    /// window.</summary>
    public (float X, float Y, float Width, float Height)? RowBox(int row)
    {
        int slots = Slots();
        int top = Window(slots);
        if (row < 0 || row >= slots || row < top || row >= top + _visibleRows)
        {
            return null;
        }

        return (_listX, _listY + ((row - top) * _rowHeight), _listWidth, _rowHeight);
    }

    /// <summary>Puts the window's first row at <paramref name="top"/>, clamped. The cursor is
    /// pulled to the window's nearer edge when it stands on a list row the move would hide. This
    /// serves the pointer's wheel and thumb, which move the window rather than the cursor.</summary>
    public void ScrollTo(int top)
    {
        int count = Slots();
        _top = Math.Clamp(top, 0, Math.Max(0, count - _visibleRows));
        if (Flow.Row < count)
        {
            Flow.FocusRow(Math.Clamp(Flow.Row, _top, _top + _visibleRows - 1));
        }
    }

    /// <summary>A list row draws no list text of its own: its three lines already stand at their
    /// authored positions inside the row.</summary>
    public override string RowText(int row) => ButtonAt(row) switch
    {
        BoardButton.ViewMission => "VIEW SELECTED",
        BoardButton.ReplayMission => "REPLAY MISSION",
        BoardButton.CurrentMission => Flow.Strings.Text(1200, "Current Mission"),
        BoardButton.ReturnToCabin => "RETURN TO CABIN",
        _ => string.Empty,
    };

    /// <inheritdoc/>
    public override BoardButtonRef Button(int row) =>
        ButtonAt(row) is var button && button != BoardButton.None
            ? new BoardButtonRef(button)
            : BoardButtonRef.None;

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        int slots = Slots();
        if (row == 0 && slots > 0)
        {
            return _selected == 0
                ? "Selected. Confirm again to open it"
                : Flow.Strings.Text(1217, CareerTitle);
        }

        if (row < slots)
        {
            return row == _selected
                ? "Selected. Confirm again to replay it"
                : Flow.Strings.Text(3450 + row - 1, $"Mission {row}");
        }

        return ButtonAt(row) switch
        {
            BoardButton.ViewMission => "Opens the scrapbook at this page",
            BoardButton.ReplayMission when SelectedSeq(slots) is { } seq =>
                Flow.Strings.Text(3450 + seq, $"Mission {seq + 1}"),
            BoardButton.ScrapbookNext => "Forward into the book",
            BoardButton.CurrentMission => "Opens the scrapbook at the current mission",
            _ => "Back to the cabin",
        };
    }

    /// <summary>A mission row's first confirm picks it, and its second replays it. That is the
    /// original's own double-click on a row, folded onto a pad's single button. The career row is
    /// never replayed, so its second confirm opens it instead. VIEW SELECTED and the bookmark both
    /// open the book (<c>uiData</c> 2405 mode 1) on the picked page and on the campaign's own
    /// current mission.</summary>
    public override bool Accept(int row)
    {
        int slots = Slots();
        if (row < slots)
        {
            if (row != _selected)
            {
                _selected = row;
                return true;
            }

            return row == 0 ? View(0) : Replay(row - 1);
        }

        switch (ButtonAt(row))
        {
            case BoardButton.ViewMission:
                if (SelectedSlot(slots) is { } viewing)
                {
                    View(viewing);
                }

                return true;
            case BoardButton.ReplayMission:
                return SelectedSeq(slots) is { } replaying && Replay(replaying);
            case BoardButton.ScrapbookNext:
                return View(0);
            case BoardButton.CurrentMission:
                Flow.OpenScrapbook(CurrentSeq());
                return true;
            default:
                Flow.OpenCabin();
                return true;
        }
    }

    /// <summary>VIEW SELECTED without walking down to the button. On a list row it picks that row
    /// and opens the book there. On any other row it opens the book on whatever is picked already.
    /// Nothing to open (no profile seated) leaves the press unhandled.</summary>
    public override bool Secondary(int row)
    {
        int slots = Slots();
        if (row >= 0 && row < slots)
        {
            _selected = row;
        }

        return SelectedSlot(slots) is { } slot && View(slot);
    }

    // How many rows the list offers: the career page, then one per story position the profile has
    // completed. That count is uiData 2409's own count of the campaign position plus one. The
    // typed gallery word puts every mission in the list instead, the 24 that callback answers
    // whenever fViewAll is set. Read fresh every call rather than cached. A replay recorded
    // through the briefing or flight-check screens must show up here the next time this draws.
    private int Slots()
    {
        if (Flow.Profile is not { } profile)
        {
            return 0;
        }

        return Flow.Cheats.RevealAll
            ? CampaignCheats.RevealedMissions + 1
            : CampaignProgression.CompletedSeqs(profile).Count + 1;
    }

    // The buttons under the list, in the order they take rows, the arrow among them where the book
    // itself carries it. REPLAY MISSION is offered only where uiData 2411 offers it, on a picked
    // mission whose record holds a time. The other four are created active and stay so.
    private List<BoardButton> Buttons()
    {
        var buttons = new List<BoardButton> { BoardButton.ViewMission };
        if (SelectedSeq(Slots()) is { } seq && Flow.Profile is { } profile
            && CampaignProgression.ResultOf(profile, seq) is { } result
            && (result.Latest.TimeMs != 0 || result.Best.TimeMs != 0))
        {
            buttons.Add(BoardButton.ReplayMission);
        }

        buttons.Add(BoardButton.ScrapbookNext);
        buttons.Add(BoardButton.CurrentMission);
        buttons.Add(BoardButton.ReturnToCabin);
        return buttons;
    }

    private BoardButton ButtonAt(int row)
    {
        var buttons = Buttons();
        int offset = row - Slots();
        return offset >= 0 && offset < buttons.Count ? buttons[offset] : BoardButton.None;
    }

    // The mission the campaign is on, which the bookmark opens the book at. That is the next
    // unflown one, or the last of the twenty-four once the campaign is finished.
    private int CurrentSeq()
    {
        if (Flow.Profile is not { } profile)
        {
            return 0;
        }

        return Math.Clamp(
            CampaignProgression.NextMissionSeq(profile), 0, CampaignSequence.MissionCount - 1);
    }

    // How strongly a row's wash draws, or null for a row that is neither picked nor focused.
    private float? Wash(int row) =>
        row == _selected ? PickedWash : row == Flow.Row ? FocusWash : null;

    private bool Replay(int seq)
    {
        Flow.SetMission(seq);
        Flow.GoTo(CampaignScreen.Briefing);
        return true;
    }

    // uiData 2405 mode 1 on a list row: the book opens on that slot's first spread. The career
    // page takes seq -1, the way the original's ordinal 0 opens mission 0.
    private bool View(int slot)
    {
        Flow.OpenScrapbook(slot - 1);
        return true;
    }

    // The list row the two buttons act on: the player's own pick, or the first finished mission
    // when nothing has been picked yet. That is the career row while nothing has been flown.
    private int? SelectedSlot(int slots)
    {
        if (slots <= 0)
        {
            return null;
        }

        return _selected >= 0 && _selected < slots ? _selected : Math.Min(1, slots - 1);
    }

    // The mission that pick stands for, or null on the career row. On mission 0, uiData 2411
    // answers 0 before the record array is read, so REPLAY MISSION is never offered there.
    private int? SelectedSeq(int slots) =>
        SelectedSlot(slots) is { } slot && slot > 0 ? slot - 1 : null;

    // The first row of the shown window, moved only as far as it must to keep the cursor's own row
    // on screen. A cursor parked on one of the three buttons leaves it where the list last stood.
    private int Window(int count)
    {
        int last = Math.Max(0, count - _visibleRows);
        int top = Math.Clamp(_top, 0, last);
        if (Flow.Row < count)
        {
            top = Math.Clamp(Math.Clamp(top, Flow.Row - _visibleRows + 1, Flow.Row), 0, last);
        }

        _top = top;
        return top;
    }

    // The thumb's run down the track. It is as tall a fraction of the track as the window is of
    // the list, and never shorter than an arrow. It steps so the last row scrolled to lands it
    // flush at the bottom.
    private (float Y, float Height) Thumb(int count, int top)
    {
        float track = WindowHeight - (2f * ScrollButton);
        float height = Math.Max(ScrollButton, track * _visibleRows / count);
        int last = Math.Max(1, count - _visibleRows);
        return (_listY + ScrollButton + ((track - height) * top / last), height);
    }

    // The three lines uiData 2409 hands the row sub-script. A mission slot takes the mission's
    // short name, the area of the chapter it belongs to, and the plane its best-of run flew. The
    // career page takes three fixed strings, and reads no record at all.
    private string[] RowLines(int slot)
    {
        if (slot == 0)
        {
            return new[]
            {
                Flow.Strings.Text(1217, CareerTitle),
                Flow.Strings.Text(1218, CareerArea),
                // The default-name rows carry an empty font tag, so the extraction keeps the
                // tag's closing bracket; the name is what follows it (docs/formats/strings.md).
                Flow.Strings.Text(511, CareerPlane).TrimStart(']'),
            };
        }

        int seq = slot - 1;
        return new[]
        {
            Flow.Strings.Text(3480 + seq, $"Mission {seq + 1}"),
            Flow.Strings.Text(1220 + (seq / 5), string.Empty),
            Run(seq)?.PlaneName ?? string.Empty,
        };
    }

    // The icon strip's frame for a row: the card fan on the career page. Every other row takes the
    // airframe its best-of run flew, clamped inside the eleven the strip carries before that fan.
    private int Airframe(int slot) => slot == 0
        ? CareerIconFrame
        : Math.Clamp(Run(slot - 1)?.Airframe ?? 0, 0, CampaignProgression.AirframeCount - 1);

    private MissionRun? Run(int seq) =>
        Flow.Profile is { } profile && CampaignProgression.ResultOf(profile, seq) is { } result
            ? result.Best
            : null;
}
