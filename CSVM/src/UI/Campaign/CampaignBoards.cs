using System;
using System.Collections.Generic;
using CSVM.UI.Boards;

namespace CSVM.UI.Campaign;

/// <summary>Which of a screen's authored description panes a row's text is written into. The ammo
/// screen authors two, one per half of the screen, and <c>ORDINANCELAYOUT.SCRIPT</c> keeps both
/// filled at once; every other screen authors at most the upper one.</summary>
public enum BoardDetailPane
{
    /// <summary>The pane a screen's single description column is, the ammo screen's
    /// <c>OL_S_AMMODESC</c> beside the gun groups.</summary>
    Upper,

    /// <summary>The ammo screen's second column, <c>OL_S_ROCKETDESC</c> beside the pylons and under
    /// its own heading.</summary>
    Lower,
}

/// <summary>Which authored button a page row presses, and which of its repeats: the flight check
/// carries one CHANGE AMMO per crew slot, 0 the pilot and 1 the wingman.</summary>
public readonly record struct BoardButtonRef(BoardButton Button, int Slot = 0)
{
    /// <summary>A row that is list text rather than a button.</summary>
    public static readonly BoardButtonRef None = default;
}

/// <summary>
/// The fixed chrome of the campaign screens, and the composer that turns a page plus a cursor into
/// a <see cref="ComposedBoard"/>. Every button, pane and text slot names its <c>LAYOUT.CSV</c>
/// section and row and is read from the decoded layout through <see cref="CampaignLayout"/>, the
/// value the board drew before the layout existed standing beside it as the fallback, so the
/// screen composes the same whether the artifact is present or not. The briefing is the exception:
/// its chrome is <c>Briefing.zrd</c>'s own (<c>docs/formats/briefing.md</c>) and no layout row
/// describes it. A value marked pinned was measured off the reference screenshot and differs from
/// its row by a pixel or a bitmap; <c>docs/org/campaign-board.md</c> names each with the row's
/// value, and the pinned one is what draws.
/// </summary>
public static class CampaignBoards
{
    /// <summary>A drop-down field's height: the sixteen pixels the reference screenshot draws
    /// between one field's top edge and its bottom, which is also the field's hit rectangle for a
    /// pointer-driven presentation.</summary>
    public const float ComboFieldHeight = 16f;

    /// <summary>The messagebox's left button row, the two-button box's first answer.</summary>
    public const string DialogLeftKey = "MB_B_LEFT";

    /// <summary>The messagebox's centred button row, the one-button box's OK.</summary>
    public const string DialogCenterKey = "MB_B_CENTER";

    /// <summary>The messagebox's right button row, the two-button box's second answer.</summary>
    public const string DialogRightKey = "MB_B_RIGHT";

    // The original's own button strips: four stacked frames, disabled / normal / rollover /
    // depressed, in the column order LAYOUT.CSV's own colour fields carry them.
    private const int StripFrames = 4;

    // The list font, in authored pixels: the listbox row heights the layout carries are 20 to 24,
    // so a 14px face is what fits a row without touching the one under it.
    private const float ListFont = 14f;

    // The objectives note's own face. Its entries are written sentences in a 185-wide column, so
    // they take a smaller face than a listbox row does.
    private const float NoteFont = 11f;

    // A face that fits inside a drop-down field.
    private const float ComboFont = 11f;

    // Both arrow strips are four frames in the same disabled / normal / rollover / depressed order
    // every button strip uses: the field's arrow is 16x12 and the scrollbar's 16x11. A closed field
    // draws the normal frame, which is the black triangle the reference screenshot shows.
    private const int ArrowFrames = 4;
    private const int ArrowNormal = 1;
    private const float ComboArrowWidth = 16f;
    private const float ComboArrowHeight = 12f;
    private const float ScrollArrowHeight = 11f;

    // Where the 410x300 messagebox art lands on the 800x600 board, which is centred: [@MessageBox@]
    // gives the box its internal geometry and no screen position, and the reference screenshots put
    // its edges here. The label's face and width, and its three-pixel drop into the plaque, are
    // measured off the same shots.
    private const float DialogX = 195f;
    private const float DialogY = 150f;

    // The widget set every messagebox row constant names, and the background art that set draws.
    private const string DefaultPrefix = "MB";
    private const string DefaultBackground = "MB_Background.png";
    private const float DialogFont = 12f;
    private const float DialogButtonWidth = 62f;
    private const float DialogLabelDrop = 3f;

    // MB_B_Icon.Png stacks three icons rather than a button's four states, in DialogIcon's order:
    // the query the confirms draw, the warning the notices draw, and the skull credits alone asks
    // for.
    private const int DialogIconFrames = 3;

    // The scrollbar thumb's own art height, which a proportional thumb never drops below since a
    // shorter one stops reading as a grip, and where a field's words sit inside its box.
    private const float ScrollThumbHeight = 12f;
    private const float ComboTextInset = 4f;
    private const float ComboTextDrop = 1f;

    // The panes in draw order, which is also the order a screen authors them in.
    private static readonly BoardDetailPane[] DetailPanes = { BoardDetailPane.Upper, BoardDetailPane.Lower };

    // A drop-down's own colours: SHAREDITEMS.SCRIPT's BUA field and CUA list, the pair every
    // campaign D row binds as its WM, fill with 0xffc8d4e6 and bar the picked row 0xffa7b9d7.
    // LAYOUT.CSV's D rows carry no colour column.
    private static readonly byte[] ComboPaper = { 200, 212, 230 };
    private static readonly byte[] ComboPicked = { 167, 185, 215 };

    // The drop-down and scrollbar strips every campaign D row names, which [GLOBALVARS] also
    // carries as GN_DROPDOWN / GN_DROPUP and FC_UP / FC_DOWN / FC_SLIDER; those macros are what
    // ComposeCombo reads them through, since a CampaignCombo carries no row of its own.
    private static readonly BoardArt ComboDownArrow = Ui("GN_B_ListboxarrowSMALLdown.png", ArrowFrames);
    private static readonly BoardArt ComboUpArrow = Ui("GN_B_ListboxarrowSMALLup.png", ArrowFrames);
    private static readonly BoardArt ScrollUp = Ui("FC_B_ScrollUp.png", ArrowFrames);
    private static readonly BoardArt ScrollDown = Ui("FC_B_ScrollDown.png", ArrowFrames);
    private static readonly BoardArt ScrollThumb = Ui("FC_B_ScrollBar.png");

    private static readonly BoardArt PaperButton = Ui("SB_B_PaperButton.png", StripFrames);
    private static readonly BoardArt FlightPaperButton = Ui("FC_B_PaperButton.Png", StripFrames);
    private static readonly BoardArt ReturnToCabinArt = Ui("GN_B_ReturntoCabin.png", StripFrames);

    // The results card's two tabs share one strip. Which of them draws over the card and which
    // behind it is the whole of the selection, so the page draws the unselected one itself
    // (CampaignScrapbookPage.Pictures) and only the selected one reaches this slot table.
    private static readonly BoardArt StatCardTab = Ui("SB_B_Statcardtab.png", StripFrames);

    // SB_B_CURRENT and SBTOC_B_CURRENT are the same bookmark at the same place on both scrapbook
    // screens, and both open the book on the campaign's own current mission.
    private static readonly BoardArt CurrentMissionTab = Ui("SB_B_Currentmissiontab.png", StripFrames);

    // SB_B_NEXT, the book's forward page tab. The table of contents is a page of that same book, so
    // it takes the tab at the same place off [@ScrapBook@]'s row; [@ScrapBook_TOC@] authors no arrow
    // of its own, leaving that page with nothing a pad can turn forward by.
    private static readonly BoardArt MoreTab = Ui("SB_B_more_tab.png", StripFrames);

    // The briefing's plaque is the one that ships beside the mission art rather than with the
    // screen chrome, and the one that carries no words: its label is drawn over it in one of three
    // fonts, which is the whole of its focus state.
    private static readonly BoardArt BriefButton = new(BoardArtLibrary.Rimage, "brief_button1");

    // Where each screen's buttons sit, keyed by screen then by button and crew slot: the layout
    // row each is read from, and the row's own value as the fallback. ⚠ CM_B_START keeps
    // CM_B_Start.png where its row names GN_B_Continue.png (a pixel narrower), and the four paper
    // plaques keep the measured 131 / 349 where their rows say 132 / 350; both are pinned so the
    // board draws what the reference shows, and docs/org/campaign-board.md carries the discrepancy.
    private static readonly Dictionary<CampaignScreen, BoardSlot[]> Buttons = new()
    {
        [CampaignScreen.Roster] = new[]
        {
            new BoardSlot(BoardButton.Continue, 0, CampaignLayout.RosterSection, "CM_B_START",
                Ui("CM_B_Start.png", StripFrames), 474, 293, PinnedArt: true),
            new BoardSlot(BoardButton.DeletePlayer, 0, CampaignLayout.RosterSection, "CM_B_DELETEPLAYER",
                Ui("CM_B_DeletePlayer.png", StripFrames), 193, 547),
            new BoardSlot(BoardButton.CancelProfile, 0, CampaignLayout.RosterSection, "CM_B_CANCEL",
                Ui("CM_B_Cancel.png", StripFrames), 444, 547),
        },
        [CampaignScreen.Cabin] = new[]
        {
            new BoardSlot(BoardButton.NextMission, 0, CampaignLayout.CabinSection, "PC_B_NEWMISSION",
                Ui("PC_B_NextMission.png", StripFrames), 87, 504),
            new BoardSlot(BoardButton.PreviousMissions, 0, CampaignLayout.CabinSection, "PC_B_PREVIOUS",
                Ui("PC_B_PreviousMissions.png", StripFrames), 361, 539),
            new BoardSlot(BoardButton.PlaneConstruction, 0, CampaignLayout.CabinSection, "PC_B_PLANEX",
                Ui("PC_B_PlaneConstruction.png", StripFrames), 452, 431),
            new BoardSlot(BoardButton.ReturnToMainMenu, 0, CampaignLayout.CabinSection, "PC_B_RETURNMM",
                Ui("PC_B_ReturnMainMenu.png", StripFrames), 593, 561),
            new BoardSlot(BoardButton.ChangeMemento, 0, CampaignLayout.CabinSection, "PC_B_CHANGEMOMENTO",
                Ui("PC_B_ChangeMemento.png", StripFrames), 259, 384),
        },

        // The chooser's two arrows carry each other's art name in the layout: the row at the left
        // edge names MS_B_Right.png and the one at the right edge MS_B_Left.png. The rows are read
        // as authored, so the strips land where the original draws them.
        [CampaignScreen.MementoSelection] = new[]
        {
            new BoardSlot(BoardButton.PreviousMemento, 0, CampaignLayout.MementoSection, "MS_B_ARROWL",
                Ui("MS_B_Right.png", StripFrames), 175, 257),
            new BoardSlot(BoardButton.NextMemento, 0, CampaignLayout.MementoSection, "MS_B_ARROWR",
                Ui("MS_B_Left.png", StripFrames), 578, 249),
            new BoardSlot(BoardButton.AcceptMemento, 0, CampaignLayout.MementoSection, "MS_B_ACCEPT",
                Ui("MS_B_AcceptChanges.png", StripFrames), 219, 564),
            new BoardSlot(BoardButton.CancelMemento, 0, CampaignLayout.MementoSection, "MS_B_CANCEL",
                Ui("MS_B_CancelChanges.png", StripFrames), 406, 564),
        },
        [CampaignScreen.PreviousMissions] = new[]
        {
            new BoardSlot(BoardButton.ViewMission, 0, CampaignLayout.ContentsSection, "SBTOC_B_VIEW",
                PaperButton, 440, 505, true),
            new BoardSlot(BoardButton.ReplayMission, 0, CampaignLayout.ContentsSection, "SBTOC_B_REPLAY",
                PaperButton, 596, 505, true),
            new BoardSlot(BoardButton.ScrapbookNext, 0, CampaignLayout.BookSection, "SB_B_NEXT",
                MoreTab, 708, 465),
            new BoardSlot(BoardButton.CurrentMission, 0, CampaignLayout.ContentsSection, "SBTOC_B_CURRENT",
                CurrentMissionTab, 558, 7, true),
            new BoardSlot(BoardButton.ReturnToCabin, 0, CampaignLayout.ContentsSection, "SBTOC_B_RETURN",
                ReturnToCabinArt, 593, 561),
        },
        [CampaignScreen.Scrapbook] = new[]
        {
            new BoardSlot(BoardButton.ReplayMission, 0, CampaignLayout.BookSection, "SB_B_REPLAY",
                PaperButton, 594, 505, true),
            new BoardSlot(BoardButton.ViewAllMissions, 0, CampaignLayout.BookSection, "SB_B_TOC",
                Ui("SB_B_ViewAllMissions.png", StripFrames), 375, 560),
            new BoardSlot(BoardButton.ReturnToCabin, 0, CampaignLayout.BookSection, "SB_B_RETURNPC",
                ReturnToCabinArt, 593, 561),
            new BoardSlot(BoardButton.ScrapbookPrev, 0, CampaignLayout.BookSection, "SB_B_PREV",
                Ui("SB_B_back_tab.png", StripFrames), 0, 465),
            new BoardSlot(BoardButton.ScrapbookNext, 0, CampaignLayout.BookSection, "SB_B_NEXT",
                MoreTab, 708, 465),
            new BoardSlot(BoardButton.CurrentMission, 0, CampaignLayout.BookSection, "SB_B_CURRENT",
                CurrentMissionTab, 558, 7, true),
            new BoardSlot(BoardButton.BestTab, 0, CampaignLayout.BookSection, "SB_B_BEST",
                StatCardTab, 432, 283, true),
            new BoardSlot(BoardButton.MostTab, 0, CampaignLayout.BookSection, "SB_B_MOST",
                StatCardTab, 594, 283, true),
        },
        [CampaignScreen.ScrapbookZoom] = new[]
        {
            new BoardSlot(BoardButton.CloseZoom, 0, CampaignLayout.ZoomSection, "SBZ_B_RETURN",
                Ui("GN_B_Continue.png", StripFrames), 640, 519),
            new BoardSlot(BoardButton.ExportScrap, 0, CampaignLayout.ZoomSection, "SBZ_B_EXPORT",
                Ui("SB_B_ExportToDesktop.png", StripFrames), 593, 561),
        },

        // Briefing.zrd's BUTTONS section, not a layout row: no section, so the fallback is the value.
        [CampaignScreen.Briefing] = new[]
        {
            new BoardSlot(BoardButton.ReplayBriefing, 0, null, null, BriefButton, 197, 560, true),
            new BoardSlot(BoardButton.ReturnToCabin, 0, null, null, BriefButton, 397, 560, true),
            new BoardSlot(BoardButton.GoToFlightCheck, 0, null, null, BriefButton, 597, 560, true),
        },
        [CampaignScreen.FlightCheck] = new[]
        {
            new BoardSlot(BoardButton.ChangePlane, 0, CampaignLayout.FlightCheckSection, "FC_B_CHANGEPLANE",
                FlightPaperButton, 128, 131, true, PinnedY: true),
            new BoardSlot(BoardButton.ChangeAmmo, 0, CampaignLayout.FlightCheckSection, "FC_B_CHANGEAMMO",
                FlightPaperButton, 274, 131, true, PinnedY: true),
            new BoardSlot(BoardButton.ChangePlane, 1, CampaignLayout.FlightCheckSection, "FC_B_CHANGEPLANEW",
                FlightPaperButton, 128, 349, true, PinnedY: true),
            new BoardSlot(BoardButton.ChangeAmmo, 1, CampaignLayout.FlightCheckSection, "FC_B_CHANGEAMMOW",
                FlightPaperButton, 274, 349, true, PinnedY: true),
            new BoardSlot(BoardButton.ReturnToBriefing, 0, CampaignLayout.FlightCheckSection, "FC_B_RETURNBRIEF",
                Ui("FC_B_ReturnToBriefing.png", StripFrames), 341, 553),
            new BoardSlot(BoardButton.FlyMission, 0, CampaignLayout.FlightCheckSection, "FC_B_FLYMISSION",
                Ui("FC_B_FlyMission.png", StripFrames), 551, 553),
        },
        [CampaignScreen.Ammo] = new[]
        {
            new BoardSlot(BoardButton.AcceptLoadout, 0, CampaignLayout.AmmoSection, "OL_B_ACCEPT",
                Ui("OL_B_AcceptLoadout.png", StripFrames), 341, 553),
            new BoardSlot(BoardButton.CancelLoadout, 0, CampaignLayout.AmmoSection, "OL_B_CANCEL",
                Ui("OL_B_CancelLoadout.png", StripFrames), 551, 553),
        },

        // PS_B_SELLP and PS_B_SELLW are authored beside the two EXPORT buttons and are deliberately
        // absent: PLANESELECTION.SCRIPT deactivates both unconditionally at gui_create.
        // ⚠ ChangeAmmo here is the remake picker's plaque over EXPORT's geometry (menu-inventory.md).
        [CampaignScreen.PlaneSelection] = new[]
        {
            new BoardSlot(BoardButton.ExportPlane, 0, CampaignLayout.PlaneSelectionSection, "PS_B_EXPORTP",
                FlightPaperButton, 560, 168, true),
            new BoardSlot(BoardButton.ChangeAmmo, 0, CampaignLayout.PlaneSelectionSection, "PS_B_EXPORTP",
                FlightPaperButton, 560, 168, true),
            new BoardSlot(BoardButton.ExportPlane, 1, CampaignLayout.PlaneSelectionSection, "PS_B_EXPORTw",
                FlightPaperButton, 560, 385, true),
            new BoardSlot(BoardButton.AcceptSelections, 0, CampaignLayout.PlaneSelectionSection, "PS_B_ACCEPT",
                Ui("PS_B_AcceptSelections.png", StripFrames), 341, 553),
            new BoardSlot(BoardButton.CancelSelections, 0, CampaignLayout.PlaneSelectionSection, "PS_B_CANCEL",
                Ui("PS_B_CancelSelections.png", StripFrames), 551, 553),
        },
    };

    // The screen chrome that is neither the page's own art nor a button: each screen's background
    // pane and the profile screen's dialog panel, at their rows' positions.
    private static readonly Dictionary<CampaignScreen, BoardPane[]> Chrome = new()
    {
        // The profile dialog stands over the main menu it opened from: [@MainMenu@]'s title mark
        // over the flag movie. [@Campaign@] authors no movie row of its own, so the mark stands on
        // a plain ground here where the top level it opened from has the flag running behind it.
        [CampaignScreen.Roster] = new[]
        {
            new BoardPane(CampaignLayout.MainMenuSection, "MM_LOGO", Ui("MM_Logo.png"), 134, 13),
            new BoardPane(CampaignLayout.RosterSection, "CM_BACKGROUND", Ui("CM_BackGround.png"), 193, 251),
        },
        [CampaignScreen.PreviousMissions] = new[]
        {
            new BoardPane(CampaignLayout.ContentsSection, "SBTOC_BACKGROUND", Ui("SB_BackgroundTOC.jpg"), 0, 0),
        },
        [CampaignScreen.Scrapbook] = new[]
        {
            new BoardPane(CampaignLayout.BookSection, "SB_BACKGROUND", Ui("SB_BackGround.jpg"), 0, 0),
        },
        [CampaignScreen.MementoSelection] = new[]
        {
            new BoardPane(CampaignLayout.MementoSection, "MS_BACKGROUND", Ui("MS_BackGround2.png"), 0, 0),
        },
        [CampaignScreen.FlightCheck] = new[]
        {
            new BoardPane(CampaignLayout.FlightCheckSection, "FC_BACKGROUND", Ui("FC_BackGround.jpg"), 0, 0),
        },
        [CampaignScreen.Ammo] = new[]
        {
            new BoardPane(CampaignLayout.AmmoSection, "OL_BACKGROUND", Ui("OL_BackGround.jpg"), 0, 0),
        },
        [CampaignScreen.PlaneSelection] = new[]
        {
            new BoardPane(CampaignLayout.PlaneSelectionSection, "PS_BACKGROUND", Ui("PS_BackGround.jpg"), 0, 0),
        },
    };

    /// <summary>The board for a page with the cursor on <paramref name="focusedRow"/>. A row the
    /// page names a button for becomes that button's plaque; every other row lists down the
    /// screen's own authored text slots. <paramref name="pressed"/> draws the focused plaque in
    /// its depressed frame for the frames a confirm is held. <paramref name="layout"/> is where
    /// the chrome is read from, the fallback when a caller has none.</summary>
    public static ComposedBoard For(
        ICampaignPage page, int focusedRow, bool pressed = false, string detail = "",
        CampaignModal? modal = null, CampaignLayout? layout = null)
    {
        layout ??= CampaignLayout.Fallback;
        var backdrop = new List<BoardPicture>();
        if (Chrome.TryGetValue(page.Screen, out var chrome))
        {
            foreach (var pane in chrome)
            {
                backdrop.Add(pane.Resolve(layout));
            }
        }

        var lines = new List<BoardLine>(page.Captions);
        var plaques = new List<BoardPlaque>();
        var fills = new List<BoardFill>(page.Fills);
        var pictures = new List<BoardPicture>(page.Pictures);
        var overlays = new List<BoardPanel>();
        var slots = Buttons.TryGetValue(page.Screen, out var found) ? found : Array.Empty<BoardSlot>();
        int listIndex = 0;

        // Read once, not per step: a page computes its row list to answer this, and the loop below
        // cannot change how many rows there are.
        int rowCount = page.RowCount;
        for (int row = 0; row < rowCount; row++)
        {
            if (page.Combo(row) is { } combo)
            {
                ComposeCombo(combo, row == focusedRow, layout, fills, pictures, lines, overlays);
                continue;
            }

            var reference = page.Button(row);
            if (reference.Button != BoardButton.None && Find(slots, reference) is { } slot)
            {
                bool focused = row == focusedRow;
                var (art, x, y) = slot.Resolve(layout);
                plaques.Add(new BoardPlaque(
                    art, x, y, row,
                    ComposedBoard.PlaqueFrame(art.Frames, focused, focused && pressed),
                    slot.Labelled ? page.RowText(row) : string.Empty,
                    ComposedBoard.PlaqueInk(focused, focused && pressed)));
                continue;
            }

            var (tx, ty, width) = TextSlot(page.Screen, listIndex++, layout);
            lines.Add(new BoardLine(
                page.RowText(row), tx, ty, width, ListFont,
                row == focusedRow ? BoardInk.RowFocused : BoardInk.Row, row));
        }

        // Every authored pane is filled, not only the one the cursor is in: the ammo screen reads a
        // gun's ammunition and a pylon's ordnance side by side. The caller's own string stands in
        // for the focused row's description, which is where a refusal reaches the board.
        foreach (var pane in DetailPanes)
        {
            if (DetailSlot(page.Screen, pane, layout) is not { } note)
            {
                continue;
            }

            int source = page.DetailRow(pane, focusedRow);
            string text = source == focusedRow ? detail : source >= 0 ? page.Detail(source) : string.Empty;
            if (text.Length > 0)
            {
                lines.Add(new BoardLine(text, note.X, note.Y, note.Width, ListFont, BoardInk.Detail));
            }
        }

        if (modal != null)
        {
            overlays.Add(Dialog(modal, layout));
        }

        return new ComposedBoard(
            pictures, page.Strokes, lines, plaques, page.Notes, backdrop, fills, overlays);
    }

    /// <summary>The messagebox as its own panel, centred on the board. Every position inside it is
    /// <c>[@MessageBox@]</c>'s own row (<c>MB_P_BACKGROUND</c>, <c>MB_P_ICON</c>,
    /// <c>MB_B_CENTER</c>, <c>MB_T_MESSAGE</c>), offset by where the 410x300 art lands; the
    /// reference (<c>OriginalScreenshots/Campaign Flight Check Change Plane Export dialog.png</c>)
    /// puts its edges at the centred one and shows OK on its normal frame in the box's own white,
    /// the pointer being elsewhere.</summary>
    public static BoardPanel Dialog(CampaignModal modal, CampaignLayout? layout = null) =>
        Dialog(
            modal.Message,
            new[]
            {
                new DialogButton(
                    DialogCenterKey, modal.Button,
                    ComposedBoard.PlaqueFrame(StripFrames, focused: false, pressed: false), ComposedBoard.DialogInk(pressed: false)),
            },
            modal.Icon,
            layout);

    /// <summary>The messagebox over any of its button sets: the single centred OK (<c>MB_B_CENTER</c>,
    /// the <c>0x1</c> box), the two-button pair (<c>MB_B_LEFT</c> and <c>MB_B_RIGHT</c>, the
    /// <c>0x4</c> box the delete confirm asks for) or all three (the <c>0x8</c> box the airframe
    /// swap asks for), each button drawn in the strip frame and label ink its caller names, which
    /// is how a pointer-driven presentation shows which one is under the pointer. The icon is its
    /// caller's too. <paramref name="chrome"/> picks the widget set; null is the shared box.</summary>
    public static BoardPanel Dialog(
        string message, IReadOnlyList<DialogButton> buttons, DialogIcon icon, CampaignLayout? layout = null,
        DialogChrome? chrome = null)
    {
        layout ??= CampaignLayout.Fallback;
        var set = chrome ?? DialogChrome.Default;
        const string section = CampaignLayout.DialogSection;
        // The icon row alone keeps the mb_ prefix, whatever set the rest of the box is drawn from.
        var (iconX, iconY) = layout.At(section, "MB_P_ICON", 36f, 65f);
        var (messageX, messageY, messageWidth) = layout.Box(section, set.Row("MB_T_MESSAGE"), 94f, 70f, 282f);
        var pictures = new List<BoardPicture>
        {
            new(layout.Art(section, set.Row("MB_P_BACKGROUND"), Ui(set.Background)), set.X, set.Y),
            new(layout.Art(section, "MB_P_ICON", Ui("MB_B_Icon.Png", DialogIconFrames)), set.X + iconX, set.Y + iconY, (int)icon),
        };
        var lines = new List<BoardLine>
        {
            new(message, set.X + messageX, set.Y + messageY, messageWidth, DialogFont, BoardInk.Dialog),
        };
        foreach (var button in buttons)
        {
            var (art, x, y) = DialogSlot(button.Key, layout, set);
            pictures.Add(new BoardPicture(art, x, y, button.Frame));
            lines.Add(new BoardLine(button.Label, x, y + DialogLabelDrop, DialogButtonWidth, DialogFont,
                button.Ink, Justify: BoardJustify.Center));
        }

        return new BoardPanel(Array.Empty<BoardFill>(), pictures, lines);
    }

    /// <summary>Where one of the messagebox's three buttons sits on the board, and its strip: the
    /// row's own position inside the art offset by where the art lands. The fallbacks are the
    /// shipped rows' values (<c>74</c>, <c>174</c> and <c>274</c> at <c>254</c>).
    /// <paramref name="chrome"/> reads the same button in another widget set.</summary>
    public static (BoardArt Art, float X, float Y) DialogSlot(
        string key, CampaignLayout? layout = null, DialogChrome? chrome = null)
    {
        layout ??= CampaignLayout.Fallback;
        var set = chrome ?? DialogChrome.Default;
        float fallbackX = key switch
        {
            DialogLeftKey => 74f,
            DialogRightKey => 274f,
            _ => 174f,
        };
        string row = set.Row(key);
        var (x, y) = layout.At(CampaignLayout.DialogSection, row, fallbackX, 254f);
        return (layout.Art(CampaignLayout.DialogSection, row, Ui("MB_B_Buttons.Png", StripFrames)), set.X + x, set.Y + y);
    }

    /// <summary>Where one of a screen's authored buttons sits and what art it draws, for a page
    /// that has to draw that button itself rather than let it become a plaque: the results card's
    /// unselected tab, which the original puts behind the card. Null when the screen has no such
    /// button.</summary>
    public static (BoardArt Art, float X, float Y)? SlotOf(
        CampaignScreen screen, BoardButton button, CampaignLayout? layout = null) =>
        SlotOf(screen, new BoardButtonRef(button), layout);

    /// <summary>The same slot by its full reference, crew slot included: the flight check's second
    /// CHANGE AMMO is the wingman's plaque, at its own row. This is where a pointer-driven
    /// presentation reads the rectangle it hit-tests a page's button row against.</summary>
    public static (BoardArt Art, float X, float Y)? SlotOf(
        CampaignScreen screen, BoardButtonRef reference, CampaignLayout? layout = null)
    {
        var slots = Buttons.TryGetValue(screen, out var found) ? found : Array.Empty<BoardSlot>();
        return Find(slots, reference)?.Resolve(layout ?? CampaignLayout.Fallback);
    }

    /// <summary>The briefing parchment's objectives list, at the <c>LIST</c> widget's own authored
    /// geometry: <c>POSITION [35, 335]</c>, <c>WORDWRAP [185, 240]</c>, <c>SPACING [5]</c>. Its
    /// entries are written sentences in a narrow column, so they take a smaller face than a listbox
    /// row and a wrapped one pushes the next entry down instead of being given a fixed pitch.</summary>
    public static BoardNote ObjectivesNote(IReadOnlyList<string> entries) =>
        new(entries, 35f, 335f, 185f, 240f, 5f, NoteFont, BoardInk.Row);

    /// <summary>One authored panel a screen writes a description into, or null where the screen
    /// does not author that pane and the shell's own hint line has to carry the words. The ammo
    /// screen authors both. ⚠ Each y is pinned at its measurement, four pixels over the row the
    /// shipped layout carries, so a row supplies the column's x and width alone.</summary>
    public static (float X, float Y, float Width)? DetailSlot(
        CampaignScreen screen, BoardDetailPane pane, CampaignLayout? layout = null)
    {
        if (screen != CampaignScreen.Ammo)
        {
            return null;
        }

        var (key, rowY, pinnedY) = pane == BoardDetailPane.Upper
            ? ("OL_S_AMMODESC", 96f, 92f)
            : ("OL_S_ROCKETDESC", 332f, 328f);
        var (x, _, width) = (layout ?? CampaignLayout.Fallback).Box(
            CampaignLayout.AmmoSection, key, 566f, rowY, 172f);
        return (x, pinnedY, width);
    }

    /// <summary>Whether row <paramref name="row"/>'s own description reaches a pane, which is what
    /// the shell asks before putting it in the hint band instead. The ammo screen's ACCEPT and
    /// CANCEL answer false: both of its panes stay on the gun and the pylon they describe, as the
    /// original's do, so those two rows' hints have nowhere on the board to go.</summary>
    public static bool DetailPaned(ICampaignPage page, int row, CampaignLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        foreach (var pane in DetailPanes)
        {
            if (DetailSlot(page.Screen, pane, layout) != null && page.DetailRow(pane, row) == row)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An open drop-down's list as a pointer sees it: the window under the field, the
    /// thumb on its track in the scrollbar column and as long as the share of the list the window
    /// shows; null while the list is closed or fits its window.</summary>
    public static ListWindow? ComboWindow(CampaignCombo combo)
    {
        ArgumentNullException.ThrowIfNull(combo);
        if (!combo.Open || !combo.Scrolls)
        {
            return null;
        }

        float top = combo.Y + ComboFieldHeight;
        float height = combo.Visible * combo.RowHeight;
        return new ListWindow(
            combo.X, top, combo.Width, height,
            combo.X + combo.Width - ComboArrowWidth, ThumbY(combo, top, height), ComboArrowWidth,
            ThumbHeight(combo, height),
            top + ScrollArrowHeight, height - (ScrollArrowHeight * 2f),
            combo.Entries.Count, combo.RowsDisplayed, combo.First);
    }

    /// <summary>Where the <paramref name="index"/>-th list row of a screen sits, as x, y and wrap
    /// width in authored pixels. Each screen's own text widgets, walked in their authored order;
    /// a screen with more rows than widgets keeps stepping by the last one's line height.</summary>
    public static (float X, float Y, float Width) TextSlot(
        CampaignScreen screen, int index, CampaignLayout? layout = null)
    {
        layout ??= CampaignLayout.Fallback;
        switch (screen)
        {
            // The name field, then the roster listbox's visible rows at its own item height.
            case CampaignScreen.Roster when index == 0:
                return layout.Box(CampaignLayout.RosterSection, "CM_E_NAME", 243f, 297f, 221f);
            case CampaignScreen.Roster:
                {
                    var (x, y, width) = layout.Box(CampaignLayout.RosterSection, "CM_L_PLAYERS", 245f, 356f, 305f);
                    int pitch = layout.Int(CampaignLayout.RosterSection, "CM_L_PLAYERS", "ItemHeight", 20);
                    return (x, y + ((index - 1) * pitch), width);
                }

            // One heading per crew slot, at the PILOT and WINGMAN widgets. The row carries the
            // heading and the aircraft's name in one line where the layout splits them over two
            // widgets, so the wrap width is the pair's and not FC_T_PILOT's own 94.
            case CampaignScreen.FlightCheck:
                {
                    var (x, y) = layout.At(
                        CampaignLayout.FlightCheckSection, index == 0 ? "FC_T_PILOT" : "FC_T_WINGMAN",
                        138f, index == 0 ? 102f : 320f);
                    return (x, y, 400f);
                }

            // ⚠ The ammo screen has no entry here, and must not regain one: its picks are drop-down
            // fields and its captions are the page's own lines, so no row of it reaches this table.
            default:
                return (20f, 20f + (index * 20f), 400f);
        }
    }

    // One drop-down: the field goes into the screen's own layers, and an open list goes into an
    // overlay instead, because it hangs across whatever the screen draws under it.
    private static void ComposeCombo(
        CampaignCombo combo, bool focused, CampaignLayout layout, List<BoardFill> fills,
        List<BoardPicture> pictures, List<BoardLine> lines, List<BoardPanel> overlays)
    {
        fills.AddRange(Box(combo.X, combo.Y, combo.Width, ComboFieldHeight, ComboPaper));
        pictures.Add(new BoardPicture(
            combo.Open
                ? layout.GlobalArt("GN_DROPUP", ComboUpArrow)
                : layout.GlobalArt("GN_DROPDOWN", ComboDownArrow),
            combo.X + combo.Width - ComboArrowWidth,
            combo.Y + ((ComboFieldHeight - ComboArrowHeight) / 2f),
            ArrowNormal));
        lines.Add(FieldText(combo.Text, combo.X, combo.Y, combo.Width - ComboArrowWidth,
            focused ? BoardInk.RowFocused : BoardInk.Row));
        if (combo.Open)
        {
            overlays.Add(OpenList(combo, layout));
        }
    }

    // The open list under its field: the box, the picked row's bar, the visible entries, and the
    // scrollbar when the entries outrun the window.
    private static BoardPanel OpenList(CampaignCombo combo, CampaignLayout layout)
    {
        float top = combo.Y + ComboFieldHeight;
        float height = combo.Visible * combo.RowHeight;
        var fills = new List<BoardFill>(Box(combo.X, top, combo.Width, height, ComboPaper));
        var pictures = new List<BoardPicture>();
        var lines = new List<BoardLine>();
        for (int seen = 0; seen < combo.Visible; seen++)
        {
            int entry = combo.First + seen;
            if (entry >= combo.Entries.Count)
            {
                break;
            }

            float y = top + (seen * combo.RowHeight);
            if (entry == combo.Highlight)
            {
                fills.Add(new BoardFill(
                    combo.X + 1f, y, combo.Width - 2f, combo.RowHeight,
                    ComboPicked[0], ComboPicked[1], ComboPicked[2]));
            }

            lines.Add(FieldText(combo.Entries[entry], combo.X, y, combo.Width, BoardInk.Row));
        }

        if (combo.Scrolls)
        {
            float bar = combo.X + combo.Width - ComboArrowWidth;
            pictures.Add(new BoardPicture(layout.GlobalArt("FC_UP", ScrollUp), bar, top, ArrowNormal));
            pictures.Add(new BoardPicture(
                layout.GlobalArt("FC_DOWN", ScrollDown), bar, top + height - ScrollArrowHeight, ArrowNormal));
            pictures.Add(new BoardPicture(
                layout.GlobalArt("FC_SLIDER", ScrollThumb), bar, ThumbY(combo, top, height),
                Height: ThumbHeight(combo, height)));
        }

        return new BoardPanel(fills, pictures, lines);
    }

    // Where the thumb sits in the track between the two arrows: the window's own position in the
    // list, so a full list's thumb is at the bottom and an unscrolled one's is at the top.
    private static float ThumbY(CampaignCombo combo, float top, float height) =>
        ListWindow.ThumbYFor(
            top + ScrollArrowHeight, height - (ScrollArrowHeight * 2f), ThumbHeight(combo, height),
            combo.First, combo.Entries.Count - combo.RowsDisplayed);

    // How long the thumb stands, which is what says how much of the list is in view: the share the
    // window shows, floored at the art's own height so a very long list still leaves a grip.
    private static float ThumbHeight(CampaignCombo combo, float height) =>
        ListWindow.ThumbHeightFor(
            height - (ScrollArrowHeight * 2f), combo.RowsDisplayed, combo.Entries.Count, ScrollThumbHeight);

    // A field's words, inset from its left edge and sat on the row's own baseline the way the
    // reference draws them: the text is vertically centred in a 16-pixel field at an 11-pixel face.
    private static BoardLine FieldText(string text, float x, float y, float width, BoardInk ink) =>
        new(text, x + ComboTextInset, y + ComboTextDrop, width - ComboTextInset, ComboFont, ink);

    // A filled rectangle and its one-pixel outline, which is what every field and list box on these
    // screens is: the engine's own list class draws no art for either.
    private static BoardFill[] Box(float x, float y, float width, float height, byte[] paper) =>
        new[]
        {
            new BoardFill(x, y, width, height, paper[0], paper[1], paper[2]),
            new BoardFill(x, y, width, height, 0, 0, 0, 1f, Border: true),
        };

    private static BoardArt Ui(string name, int frames = 1) =>
        new(BoardArtLibrary.Ui, name, frames);

    private static BoardSlot? Find(BoardSlot[] slots, BoardButtonRef reference)
    {
        foreach (var slot in slots)
        {
            if (slot.Button == reference.Button && slot.Slot == reference.Slot)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>One button of a composed messagebox: its layout row, its words, the strip frame to
    /// draw and the ink its label takes.</summary>
    public readonly record struct DialogButton(string Key, string Label, int Frame, BoardInk Ink);

    /// <summary>
    /// Which messagebox widget set a dialog composes and where that set's background art lands:
    /// <c>MESSAGEBOX.SCRIPT</c> picks the prefix off two globals, <c>mb_</c> by default and
    /// <c>ma_</c> over <c>CR_AboutMessageBox.png</c> when a screen sets <c>XR</c>, and centres the
    /// pane by its own size. <see cref="Background"/> is the fallback art for the set's background
    /// row. ⚠ The icon takes no prefix: it is <c>mb_p_icon</c> in every set, at that one row's own
    /// place, so a variant must not read one under its own.
    /// </summary>
    public readonly record struct DialogChrome(string Prefix, float X, float Y, string Background)
    {
        /// <summary>The shared <c>mb_</c> box at its centred corner, which every campaign dialog
        /// and every refusal draws.</summary>
        public static DialogChrome Default { get; } = new(DefaultPrefix, DialogX, DialogY, DefaultBackground);

        /// <summary>One of the messagebox's rows under this set's prefix. The row constants name
        /// the <c>mb_</c> rows, so a caller hands the shared key over and a variant reads the same
        /// row in its own set.</summary>
        public string Row(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return key.StartsWith(DefaultPrefix, StringComparison.Ordinal)
                ? Prefix + key[DefaultPrefix.Length..]
                : key;
        }
    }

    // One button: the layout row it is read from (null for the briefing's zrd-authored plaques)
    // and the fallback beside it. Labelled says the row carries a ResID, so the plaque's words are
    // drawn over blank art; every screen-specific button bakes its own words into its strip
    // instead. PinnedY and PinnedArt keep the measured value over the row's.
    private readonly record struct BoardSlot(
        BoardButton Button, int Slot, string? Section, string? Key, BoardArt Art, float X, float Y,
        bool Labelled = false, bool PinnedY = false, bool PinnedArt = false)
    {
        public (BoardArt Art, float X, float Y) Resolve(CampaignLayout layout)
        {
            if (Section == null || Key == null)
            {
                return (Art, X, Y);
            }

            var art = PinnedArt ? Art : layout.Art(Section, Key, Art);
            var (x, y) = layout.At(Section, Key, X, Y);
            return (art, x, PinnedY ? Y : y);
        }
    }

    // One fixed pane of a screen's chrome, read the same way a button is.
    private readonly record struct BoardPane(string Section, string Key, BoardArt Art, float X, float Y)
    {
        public BoardPicture Resolve(CampaignLayout layout)
        {
            var (x, y) = layout.At(Section, Key, X, Y);
            return new BoardPicture(layout.Art(Section, Key, Art), x, y);
        }
    }
}
